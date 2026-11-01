using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★ 藏身处 + 商人 + bot 预设 维度（v2.3.0）—— 覆盖 TODO 的 #2/#3/#4。
///
/// ★★★ v2.6.12【P0 落点修正】#3 与 #4 的前两项原来写的是**只在启动阶段被读一次**的 config 对象
///   （本 mod 是 [Injectable] 默认 TypePriority = int.MaxValue ⇒ OnLoad 排在所有 SPT 内建之后：
///    GameCallbacks(200000) → TraderCallbacks(700000) → RagfairCallbacks(900000) → 本 mod）
///   ⇒ 那些写入**从来没生效过**（重启也一样）。现在改为**直接改已加载的表**：
///   #3 藏身处  建造 = HideoutTable.Areas[].Stages[].ConstructionTime
///                  ← 读点 HideoutController.cs:86（请求升级时实时读）/ DataCallbacks.cs:89（直接流给客户端）
///              制作 = HideoutTable.Production.Recipes[].ProductionTime
///                  ← 读点 HideoutHelper.cs:629-644（开工时实时读）/ HideoutController.cs:681
///              （旧落点 HideoutConfig.Override{Build,Craft}TimeSeconds 的唯一读点是
///                PostDbLoadService.cs:59-60，启动期 ⇒ 已改成"只写表"）
///   #4 商人    全局价格倍率 = TradersTable[trader].Assort.BarterScheme[*][*][*].Count
///                  ← 读点 TraderAssortHelper（客户端每次拉 assort 都从这张表取，运行期实时读）；
///                    算法与 SPT `TraderController.AdjustTraderItemPrices`(TraderController.cs:62-76) 逐字一致
///              （旧落点 TraderConfig.TraderPriceMultiplier 的唯一读点是 TraderController.Load():47-49，
///                由 TraderCallbacks(700000) 触发 ⇒ 启动期）
///              TraderConfig.UpdateTimeDefault / UpdateTime[].Seconds（刷新间隔 —— 这个字段**读点是运行期**的
///                TraderHelper.GetTraderUpdateSeconds，所以旧落点本来就有效，保持不变）
///              TraderConfig.PurchasesAreFoundInRaid（买到的算 FIR —— 保持不变）
///   #2 bot 预设 BotConfig.PresetBatch（每角色用几套装备预设；调大 = 出装更杂更"武装"）
///              BotConfig.MaxBotCap（每图 bot 上限）
///              落点文件 configs/bot.json → presetBatch / maxBotCap
///
/// 全部可还原（每个落点都 AddRestore + 写后回读自证）。
/// </summary>
internal static class HideoutTraderDimension
{
    /// <summary>
    /// ⚠ v2.6.12 起**本类不再读它**：#3 秒建/秒造改走 <see cref="HideoutTable"/>（旧落点
    /// `Override{Build,Craft}TimeSeconds` 的唯一读点在启动期，写了没人读）。
    /// 保留字段（不删）只为不动 DI 表面 / 不挡别人引用；**别再往里写值**。
    /// </summary>
    public static object HideoutConfig;
    public static object TraderConfig;
    public static object BotConfig;
    /// <summary>★ v2.6.12：已加载的 HideoutTable（#3 的新落点）。</summary>
    public static object HideoutTable;
    /// <summary>★ v2.6.12：已加载的 TradersTable（#4 全局价格倍率的新落点）。</summary>
    public static object TraderTable;
    /// <summary>★ v2.6.12：SPT 自己的"是不是货币"判定器（与 TraderController 用同一个实例/口径）。</summary>
    public static SPTarkov.Server.Core.Helpers.Commerce.PaymentHelper PaymentHelper;
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    private const BindingFlags Any =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

    /// <summary>兜底货币 tpl（PaymentHelper 万一拿不到时用）：卢布/美元/欧元/GP，取自 Money.cs:28-36。</summary>
    private static readonly HashSet<string> FallbackMoney = new(StringComparer.OrdinalIgnoreCase)
    {
        "5449016a4bdc2d6f028b456f",   // Money.ROUBLES
        "5696686a4bdc2da3298b456a",   // Money.DOLLARS
        "569668774bdc2da2298b4568",   // Money.EUROS
        "5d235b4d86f7742e017bc88a",   // Money.GP
    };

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][藏身处/商人] " + m); } catch { } }

    /// <summary>★ v1.0.1【性能·第六轮】元数据查找改走 R.GP / R.GF / R.GFs 缓存
    /// （`GetFields` 原来**每次调用都分配一个新数组**）；控制流与异常语义逐字保留。</summary>
    private static object Prop(object o, string n)
    {
        if (o == null) return null;
        var t = o.GetType();
        var p = R.GP(t, n, BindingFlags.Public | BindingFlags.Instance);
        if (p != null) return p.GetValue(o);
        var f = R.GF(t, n, BindingFlags.Public | BindingFlags.Instance);
        if (f != null) return f.GetValue(o);
        foreach (var f2 in R.GFs(t, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
            if (f2.Name.Contains(n)) return f2.GetValue(o);
        return null;
    }

    private static bool SetProp(object o, string n, object v)
    {
        if (o == null) return false;
        var t = o.GetType();
        var p = t.GetProperty(n);
        if (p != null && p.CanWrite) { try { p.SetValue(o, v); return true; } catch { } }
        var f = t.GetField(n);
        if (f != null) { try { f.SetValue(o, v); return true; } catch { } }
        return false;
    }

    public static void Apply(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            // ── #3 藏身处：秒建 / 秒造 ──
            // ★★★ v2.6.12【P0 落点修正】旧实现写 `HideoutConfig.OverrideBuildTimeSeconds / OverrideCraftTimeSeconds`，
            //   而这两个字段的**唯一读者**是（全库 grep 实证，除定义外只有这一处）
            //     `PostDbLoadService.PerformPostDbLoadActions()`
            //        → `AdjustHideoutCraftTimes(hideoutConfig.OverrideCraftTimeSeconds)`（:59 → :535-544）
            //        → `AdjustHideoutBuildTimes(hideoutConfig.OverrideBuildTimeSeconds)`（:60 → :550-563）
            //   （_forensic_tmp/ilspy_full/SPTarkov.Server.Core.Services.Server/PostDbLoadService.cs）
            //   它由 **GameCallbacks（TypePriority=200000）→ GameController.Load() → PerformPostDbLoadActions()**
            //   触发（GameController.cs:402），而本 mod 是 [Injectable] 默认 **TypePriority = int.MaxValue**
            //   ⇒ 永远排在它之后 ⇒ 旧写法是**空转**，重启也一样（每次启动都是「SPT 先 Math.Min 烘进表、我们后写」）。
            //   实测铁证：本机 `SPT_Data/configs/hideout.json` 里 `overrideBuildTimeSeconds` 始终是 **-1**
            //   （= 不覆盖），而活动 `instant_hideout_build` 声明 `hideoutBuildSeconds: 0` ⇒ 秒建从来没生效过。
            //   正解 = 直接改**已加载的 HideoutTable**（SPT 自己就是往这张表里烘）：
            //     · 建造：`HideoutTable.Areas[].Stages[].ConstructionTime`（Stage.cs:45，double?；
            //             HideoutArea.Stages 是 Dictionary&lt;string, Stage&gt;，HideoutArea.cs:164-165）
            //         ← 读点 `HideoutController.cs:86`：请求 `/client/hideout/area/upgrade` 时**实时**读，
            //           写进 `CompleteTime = now + ConstructionTime`；另外 `DataCallbacks.cs:89` 直接把
            //           `hideoutTable.Areas` 流给客户端 ⇒ 运行期改它**立即生效**（不是启动期烘一次）。
            //     · 制作：`HideoutTable.Production.Recipes[].ProductionTime`（HideoutProduction.cs:60，double?）
            //         ← 读点 `HideoutHelper.cs:629-644`（GetAdjustedCraftTimeWithSkills，开工时实时读）
            //           + `HideoutController.cs:681`（StartSingleProduction）。
            //   语义与 SPT 保持一致：SPT 是 `Math.Min(原值, overrideSeconds)`（:542 / :560）
            //   ⇒ 这里是**上限**不是下限：写 0 = 全部置 0（秒建/秒造），写 30 = 把超过 30 秒的压到 30 秒。
            //   ⚠ 与 SPT 自身一致的已知缺口（不是本 mod 引入、这里也不补）：`Production.ScavRecipes[].ProductionTime`
            //     是**独立字段**（读点 HideoutController.cs:486），**不在** override 的覆盖范围内 —— SPT 自己也不改它。
            //   ⚠ 幂等：已在阈值内的条目不登记还原闭包（否则每轮进图都堆一批"还原成同值"的闭包）。
            if (ev.HideoutBuildSeconds.HasValue || ev.HideoutCraftSeconds.HasValue)
            {
                if (HideoutTable == null) logger?.Warning("[EventRotator][藏身处] 拿不到 HideoutTable，秒建/秒造未生效");
                else
                {
                    if (ev.HideoutBuildSeconds.HasValue)
                    {
                        double want = ev.HideoutBuildSeconds.Value;
                        int changed = 0, already = 0, bad = 0, areas = 0, stages = 0;
                        if (Prop(HideoutTable, "Areas") is IEnumerable areaList)
                            foreach (var area in areaList)
                            {
                                if (area == null) continue;
                                areas++;
                                if (Prop(area, "Stages") is not IDictionary stageMap) continue;
                                foreach (DictionaryEntry se in stageMap)
                                {
                                    stages++;
                                    ClampCeiling(se.Value, "ConstructionTime", want, ref changed, ref already, ref bad);
                                }
                            }
                        Info($"藏身处**建造**时间 ≤ {want:0.##} 秒：改了 {changed} 个 Stage.ConstructionTime"
                             + $"（已在阈值内 {already}、异常 {bad}；扫过 {areas} 个区域 / {stages} 个阶段）"
                             + (changed == 0 && bad == 0 ? "  ⚠ 一个都没改到 ⇒ 落点可能又错了，请查 Areas/Stages 结构" : "")
                             + "  ← 新落点 HideoutTable.Areas[].Stages[].ConstructionTime（读点 HideoutController.cs:86，运行期实时读）");
                    }
                    if (ev.HideoutCraftSeconds.HasValue)
                    {
                        double want = ev.HideoutCraftSeconds.Value;
                        int changed = 0, already = 0, bad = 0, recipes = 0;
                        var prod = Prop(HideoutTable, "Production");
                        if (Prop(prod, "Recipes") is IEnumerable recList)
                            foreach (var r in recList)
                            {
                                if (r == null) continue;
                                recipes++;
                                ClampCeiling(r, "ProductionTime", want, ref changed, ref already, ref bad);
                            }
                        Info($"藏身处**制作**时间 ≤ {want:0.##} 秒：改了 {changed} 个 Recipe.ProductionTime"
                             + $"（已在阈值内 {already}、异常 {bad}；扫过 {recipes} 个配方）"
                             + (changed == 0 && bad == 0 ? "  ⚠ 一个都没改到 ⇒ 落点可能又错了" : "")
                             + "  ← 新落点 HideoutTable.Production.Recipes[].ProductionTime（读点 HideoutHelper.cs:629-644，开工时实时读）");
                    }
                }
            }

            // ── #4 商人：价格倍率 / 刷新间隔 / FIR ──
            // ★ v2.6.12：全局价格倍率改成"直接改已加载的 TradersTable"⇒ **不再需要 TraderConfig**，
            //   所以从下面那个 `TraderConfig == null` 守卫里拆出来单独处理（旧代码把两者绑在一起）。
            if (ev.TraderPriceMultiplier.HasValue) ApplyGlobalTraderPrice(ev, logger);

            if (ev.TraderUpdateSeconds.HasValue || ev.TraderPurchasesFir.HasValue)
            {
                if (TraderConfig == null) logger?.Warning("[EventRotator][商人] 拿不到 TraderConfig");
                else
                {
                    if (ev.TraderUpdateSeconds.HasValue)
                    {
                        // ★★ v0.1.3 修正二（实测类型）：`TraderConfig.UpdateTime` 是 **List**（不是 Dictionary），
                        //   每项形如 { _name, traderId, seconds: { min, max } }。只改 UpdateTimeDefault 对现有
                        //   11 个商人无效（GetTraderUpdateSeconds 先查这张表）。
                        int changed = 0, traders = 0;
                        if (Prop(TraderConfig, "UpdateTime") is IEnumerable list)
                        {
                            foreach (var one in list)
                            {
                                if (one == null) continue;
                                traders++;
                                var sec = Prop(one, "Seconds") ?? Prop(one, "seconds");
                                if (sec == null) continue;
                                foreach (var f in new[] { "Min", "Max" })
                                {
                                    var oldV = Prop(sec, f);
                                    if (oldV == null) continue;
                                    int oldI; try { oldI = Convert.ToInt32(oldV); } catch { continue; }
                                    var capSec = sec; var capF = f; var capOld = oldI;
                                    AddRestore?.Invoke(() => { try { SetProp(capSec, capF, capOld); } catch { } });
                                    SetProp(sec, f, (int)ev.TraderUpdateSeconds.Value);
                                    changed++;
                                }
                            }
                        }
                        var oldD2 = Prop(TraderConfig, "UpdateTimeDefault");
                        var capT = TraderConfig; var capOldD = oldD2;
                        AddRestore?.Invoke(() => { try { SetProp(capT, "UpdateTimeDefault", capOldD); } catch { } });
                        SetProp(TraderConfig, "UpdateTimeDefault", (int)ev.TraderUpdateSeconds.Value);
                        Info($"商人刷新间隔 → {(int)ev.TraderUpdateSeconds.Value} 秒："
                             + $"改了 UpdateTime 列表里 {traders} 个商人的 {changed} 个字段 + UpdateTimeDefault"
                             + (traders == 0 ? "  ⚠ UpdateTime 列表没读到，可能只改了 default（对现有商人无效）" : "")
                             + "  ⚠ 读点在 TraderHelper.GetTraderUpdateSeconds（运行时实时读，这条**能生效**）");
                    }
                    if (ev.TraderPurchasesFir.HasValue)
                    {
                        var old = Prop(TraderConfig, "PurchasesAreFoundInRaid");
                        bool oldB; try { oldB = Convert.ToBoolean(old); } catch { oldB = false; }
                        var cap = TraderConfig;
                        AddRestore?.Invoke(() => { try { SetProp(cap, "PurchasesAreFoundInRaid", oldB); } catch { } });
                        SetProp(TraderConfig, "PurchasesAreFoundInRaid", ev.TraderPurchasesFir.Value);
                        Info($"商人购买算 FIR: {oldB} → {ev.TraderPurchasesFir.Value}（可还原）");
                    }
                }
            }

            // ── #2 bot 预设批次 / 上限 ──
            if ((ev.BotPresetBatch != null && ev.BotPresetBatch.Count > 0) || ev.BotMaxCap.HasValue)
            {
                if (BotConfig == null) logger?.Warning("[EventRotator][bot预设] 拿不到 BotConfig");
                else
                {
                    if (ev.BotPresetBatch != null && ev.BotPresetBatch.Count > 0)
                    {
                        var pb = Prop(BotConfig, "PresetBatch") as IDictionary;
                        if (pb == null) logger?.Warning("[EventRotator][bot预设] PresetBatch 不是字典");
                        else foreach (var kv in ev.BotPresetBatch)
                            {
                                object key = null;
                                foreach (var k in pb.Keys)
                                    if (string.Equals(k?.ToString(), kv.Key, StringComparison.OrdinalIgnoreCase)) { key = k; break; }
                                if (key == null) { logger?.Warning($"[EventRotator][bot预设] 没有角色 '{kv.Key}'"); continue; }
                                int old; try { old = Convert.ToInt32(pb[key]); } catch { continue; }
                                var capPb = pb; var capKey = key; var capOld = old;
                                AddRestore?.Invoke(() => { try { capPb[capKey] = capOld; } catch { } });
                                pb[key] = kv.Value;
                                Info($"bot 预设批次数 PresetBatch['{kv.Key}']: {old} → {kv.Value}（可还原）");
                            }
                    }
                    if (ev.BotMaxCap.HasValue)
                    {
                        // ★★ v0.1.3 修正（实测）：`BotController.GetBotCap(location)` 是
                        //   `MaxBotCap.TryGetValue(地图名) ?? MaxBotCap["default"]` ——
                        //   而 configs/bot.json 的 maxBotCap **已经给 12 张主图各自配了条目**，
                        //   所以旧代码"只改 default"对这 12 张图**完全无效**。
                        //   现在改为：**改所有 key**（default + 每张图），确保真的生效。
                        var mc = Prop(BotConfig, "MaxBotCap") as IDictionary;
                        if (mc == null) logger?.Warning("[EventRotator][bot上限] MaxBotCap 不是字典");
                        else
                        {
                            int n = 0;
                            foreach (var k in new List<object>(mc.Keys.Cast<object>()))
                            {
                                int old; try { old = Convert.ToInt32(mc[k]); } catch { continue; }
                                var capMc = mc; var capKey = k; var capOld = old;
                                AddRestore?.Invoke(() => { try { capMc[capKey] = capOld; } catch { } });
                                mc[k] = (int)ev.BotMaxCap.Value;
                                n++;
                            }
                            Info($"bot 上限 MaxBotCap：改了 {n} 个条目（default + 各图）→ {(int)ev.BotMaxCap.Value}（可还原）");
                        }
                    }
                }
            }
        }
        catch (Exception ex) { logger?.Error($"[EventRotator][藏身处/商人] 失败: {ex.Message}"); }
    }

    /// <summary>
    /// ★★★ v2.6.12【P0 落点修正】全局商人价格倍率 —— **直接改已加载的 TradersTable**。
    ///
    /// 旧实现写 `TraderConfig.TraderPriceMultiplier`：它的**唯一读者**是
    /// `TraderController.Load():47-49` → `AdjustTraderItemPrices()`（TraderController.cs:62-76），
    /// 由 **TraderCallbacks（TypePriority = 700000）** 触发；而本 mod 是 [Injectable] 默认
    /// **TypePriority = int.MaxValue** ⇒ 永远排在它之后
    /// ⇒ 运行期改价**完全无效，重启也无效**（每次启动都是「SPT 先按当时的 1.0 跳过、我们后写」）。
    /// 实测铁证：本机 `SPT_Data/configs/trader.json` 里 `traderPriceMultiplier` 始终是 **1**，
    /// 而 `unlocked_traders` / `unlocked_2023_08` / `reward_trader_sale` 三个活动都声明了 0.5（五折）。
    ///
    /// 新落点 = 照 `AdjustTraderItemPrices` 干的事，直接改 `Trader.Assort.BarterScheme`：
    ///   · 它是 `Dictionary&lt;MongoId, List&lt;List&lt;BarterScheme&gt;&gt;&gt;`（TraderAssort.cs:43-44），
    ///     与磁盘上的 `database/traders/&lt;id&gt;/assort.json` 的 `barter_scheme` 同名直连
    ///     （`[JsonPropertyName("barter_scheme")]`）⇒ DB 加载时就已经填好，不是"按需懒加载"。
    ///   · 只改**货币**条目 —— 判定直接调 SPT 自己的 `PaymentHelper.IsMoneyTpl`（TraderController.cs:70；
    ///     PaymentHelper.cs:27-38 = 卢布/美元/欧元/GP + `inventoryConfig.customMoneyTpls`），
    ///     保证"哪些算价格"与 SPT 逐字一致（**易物条目绝不动**）。
    ///   · 算价用 SPT 的原式 `Count += Math.Round(Count × 倍率, 2)`（TraderController.cs:72）；
    ///     倍率是**增量**：用户配的是"最终价倍率"（0.5 = 五折）⇒ 倍率 = 值 − 1。
    ///     （旧注释写的「只要不是 1.0 就会应用」是**错的** —— 那只是 SPT 在**启动期**的一次判断，
    ///      跟"运行期写它有没有人读"是两回事。）
    ///   · 跳过 `Count == null / 0`。
    ///   · 读点：客户端每次拉商人 assort 都从这张表取（`TraderAssortHelper` / `TraderController.GetAssort`）
    ///     ⇒ 运行期改它**立即生效**（重开商人界面即可）。另外整点换货走的
    ///     `TraderAssortHelper.ResetExpiredTrader` **只重置 `Assort.Items`**（TraderAssortHelper.cs:137），
    ///     不碰 `BarterScheme` ⇒ 换货不会把我们的价格改动冲掉。
    ///   · ⚠ 与 SPT 的唯一（本机 0 例）差异，记录以备将来：SPT 只缩放每个商品**第一个变体的第一条**报价
    ///     （`item.Value.FirstOrDefault()?.FirstOrDefault()`，TraderController.cs:66），这里按需求缩放
    ///     **所有变体**里的货币条目。实测本机 `database/traders/*/assort.json`：2854 个商品 / 2442 条货币报价，
    ///     与"只取第一条"**完全重合**（0 例货币报价落在变体 #0 之外、0 例商品有多个货币变体）⇒ 两者等价。
    /// </summary>
    private static void ApplyGlobalTraderPrice(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            if (TraderTable is not IDictionary traders)
            {
                logger?.Warning("[EventRotator][商人] 拿不到 TradersTable ⇒ 全局价格倍率未生效");
                return;
            }

            double wantFinal = ev.TraderPriceMultiplier.Value;
            double multiplier = wantFinal - 1.0;        // SPT 口径：这是**增量**（0 = 原价）
            int traderCount = 0, items = 0, entries = 0, skipped = 0, bad = 0, emptyAssort = 0, skippedTrader = 0;

            foreach (DictionaryEntry tkv in traders)
            {
                var trader = tkv.Value;
                if (trader == null) continue;
                // ★★ 与 SPT **逐字对齐**：`TraderController.Load()` 在调用 `AdjustTraderItemPrices` 之前
                //   对这两个商人 `continue`（TraderController.cs:37-46）——
                //     · LIGHTHOUSEKEEPER（638f541a29ffd1183d187f57，"caretaker"）：:37-40 直接跳过；
                //     · FENCE（579dc571d53a0658a154fbec）：:41-46 改走
                //       `fenceBaseAssortGenerator.GenerateFenceBaseAssorts()` + `fenceService.GenerateFenceAssorts()`
                //       （它的报价是**动态生成**的，不是 assort.json 那一份）。
                //   ⇒ 为了"把我们这个字段变成 SPT 那个字段的运行期等价物"，这里必须同样跳过；
                //     否则本 mod 的 ×0.5 会连 Fence/caretaker 一起打折，与 `configs/trader.json` 的
                //     同名字段行为**不一致**（那才是保真度的基准）。
                var tid = tkv.Key?.ToString() ?? "";
                if (tid.Equals("638f541a29ffd1183d187f57", StringComparison.OrdinalIgnoreCase)   // Traders.LIGHTHOUSEKEEPER
                 || tid.Equals("579dc571d53a0658a154fbec", StringComparison.OrdinalIgnoreCase))  // Traders.FENCE
                { skippedTrader++; continue; }
                var schemes = Prop(Prop(trader, "Assort"), "BarterScheme") as IDictionary;
                if (schemes == null) continue;
                if (schemes.Count == 0) { emptyAssort++; continue; }   // 空的也要计数，便于证伪"懒加载"
                traderCount++;

                foreach (DictionaryEntry item in schemes)
                {
                    if (item.Value is not IEnumerable outer) continue;
                    bool touchedItem = false;
                    foreach (var one in outer)
                    {
                        if (one is not IEnumerable inner) continue;
                        foreach (var bs in inner)
                        {
                            if (bs == null) continue;
                            // ★ CLR 名 ≠ JSON 名：BarterScheme 的 JSON 名是 `_tpl`，C# 属性叫 **Template**
                            //   （BarterScheme.cs:31-32）。取错名字会永远 null ⇒ 0 条命中（本项目在
                            //   TraderAssortDimension 上真踩过这个坑，日志 `tpl= ()` 铁证）。
                            var tplObj = Prop(bs, "Template") ?? Prop(bs, "Tpl");
                            if (tplObj == null) continue;
                            if (!IsMoney(tplObj.ToString())) continue;     // 只改货币条目，易物不动

                            var cntObj = Prop(bs, "Count");
                            if (cntObj == null) { skipped++; continue; }   // ★ 跳过 null
                            double cnt;
                            try { cnt = Convert.ToDouble(cntObj); } catch { skipped++; continue; }
                            if (cnt <= 0) { skipped++; continue; }         // ★ 跳过 0（0 价条目没有"打折"语义）

                            double newCnt = cnt + Math.Round(cnt * multiplier, 2);   // ← SPT 原式
                            if (newCnt < 0) newCnt = 0;
                            if (Math.Abs(newCnt - cnt) < 0.000001) continue;         // 无变化 ⇒ 不登记

                            var p = bs.GetType().GetProperty("Count", Any);
                            if (p == null || !p.CanWrite) { bad++; continue; }

                            var capB = bs; var capOld = cnt;
                            AddRestore?.Invoke(() =>
                            {
                                try
                                {
                                    var pp = capB.GetType().GetProperty("Count", Any);
                                    if (pp != null && pp.CanWrite)
                                        pp.SetValue(capB, Convert.ChangeType(capOld,
                                            Nullable.GetUnderlyingType(pp.PropertyType) ?? pp.PropertyType));
                                }
                                catch { }
                            });
                            p.SetValue(bs, Convert.ChangeType(newCnt,
                                Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType));

                            // ★ 写后回读自证（本项目的老毛病就是"打了日志却没生效"）
                            double back = Convert.ToDouble(p.GetValue(bs));
                            if (Math.Abs(back - newCnt) < 0.000001) { entries++; touchedItem = true; }
                            else bad++;
                        }
                    }
                    if (touchedItem) items++;
                }
            }

            int pct = (int)Math.Round((wantFinal - 1.0) * 100);
            Info($"商人价格 ×{wantFinal:0.##}（{(pct >= 0 ? "+" : "")}{pct}%）：直接改了 {traderCount} 个商人的 "
                 + $"{items} 个商品 / {entries} 条**货币报价**的 Count（跳过 {skipped} 条非货币或 0 价、"
                 + $"失败 {bad} 条、空 assort 商人 {emptyAssort} 个；原值已全部登记还原）");
            Info($"  · 按 SPT 的取舍跳过 {skippedTrader} 个特殊商人（Fence 579dc571… / caretaker 638f541a… —— "
                 + "TraderController.Load():37-46 对它们 `continue`，报价另行动态生成）");
            Info("  ← 新落点 TradersTable[t].Assort.BarterScheme[item][变体][报价].Count（读点 TraderAssortHelper，运行期实时读）；"
                 + "算式与 SPT TraderController.AdjustTraderItemPrices(TraderController.cs:62-76) 逐字一致，"
                 + "只动货币（PaymentHelper.IsMoneyTpl 口径）");
            if (entries == 0)
                Info("  ⚠ 一条都没改到 ⇒ 要么 assort 仍为空、要么货币判定/属性名口径不对 —— 请按上面计数定位，别当成「已生效」");
        }
        catch (Exception ex) { logger?.Warning($"[EventRotator][商人] 全局价格倍率失败: {ex.Message}"); }
    }

    /// <summary>货币判定 —— 直接调 SPT 的 PaymentHelper（与 `TraderController.AdjustTraderItemPrices` 同一实例/口径）。
    /// 兜底：万一 PaymentHelper 没注入成功，用本 mod 已知的 4 个货币 tpl（Money.cs:28-36）。</summary>
    private static bool IsMoney(string tpl)
    {
        if (string.IsNullOrWhiteSpace(tpl)) return false;
        try
        {
            if (PaymentHelper != null)
                return PaymentHelper.IsMoneyTpl(new SPTarkov.Server.Core.Models.Common.MongoId(tpl));
        }
        catch { }
        return FallbackMoney.Contains(tpl);
    }

    /// <summary>
    /// ★★★ v2.6.12【P0】把某对象上的某个时间字段压到上限（**与 SPT 的 `Math.Min(原值, override)` 同口径**，
    /// 见 PostDbLoadService.cs:542 / :560），并同时做三件事：登记还原闭包、**写后回读自证**、分类计数。
    /// 幂等：`新值 == 原值` 时**不登记还原闭包**（否则每轮进图都会堆一批"还原成同值"的闭包）。
    /// 类型转换走 `Nullable.GetUnderlyingType`（`ConstructionTime`/`ProductionTime` 都是 `double?`，
    /// 直接 `SetValue(int)` 会抛 ArgumentException 被 catch 吞掉 ⇒ 表现成"打了日志却没生效"）。
    /// </summary>
    private static bool ClampCeiling(object target, string field, double want,
                                     ref int changed, ref int already, ref int bad)
    {
        try
        {
            if (target == null) { bad++; return false; }
            var p = target.GetType().GetProperty(field, Any);
            if (p == null || !p.CanWrite) { bad++; return false; }
            double oldD;
            try { oldD = Convert.ToDouble(p.GetValue(target)); } catch { bad++; return false; }
            double newD = Math.Min(oldD, want);
            if (Math.Abs(newD - oldD) < 0.000001) { already++; return false; }   // 已在阈值内 ⇒ 不动

            var capT = target; var capP = p; var capOld = oldD;
            AddRestore?.Invoke(() => { try { capP.SetValue(capT, capOld); } catch { } });
            p.SetValue(target, Convert.ChangeType(newD, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType));

            double back = Convert.ToDouble(p.GetValue(target));                  // ★ 回读自证
            if (Math.Abs(back - newD) < 0.000001) { changed++; return true; }
            bad++; return false;
        }
        catch { bad++; return false; }
    }
}
