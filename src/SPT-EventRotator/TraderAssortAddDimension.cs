using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★ task-34 ⑧-C：**往商人 assort 追加一条易物**（官方 `softcore` 第 5 条「LL2 狗牌易物」）。
///
/// ── 落点（已实证）────────────────────────────────────────────────────────────
///   `TraderTable`（`Dictionary&lt;MongoId, Trader&gt;`，由 Plugin 注入）→ `Trader.Assort : TraderAssort`
///   `TraderAssort` 三键（`Models.Eft.Common.Tables/TraderAssort.cs`）：
///     · `items`              → `List&lt;Item&gt;`                     (`TraderAssort.cs:28-29`)
///     · `barter_scheme`      → `Dictionary&lt;MongoId, List&lt;List&lt;BarterScheme&gt;&gt;&gt;` (`:43-44`)
///     · `loyal_level_items`  → `Dictionary&lt;MongoId, int&gt;`         (`:58-59`)
///   `BarterScheme`（`Models.Eft.Common.Tables/BarterScheme.cs`）：
///     · `count`  → `double?`  Count               (`:14-15`)
///     · `_tpl`   → **`MongoId` Template**  ★ JSON 名 ≠ CLR 名      (`:29-30`)
///     · `level`  → `int?` Level                   (`:74-75`)
///     · `side`   → `DogtagExchangeSide?` Side     (`:89-91`)
///   SPT 读法实证（`Controllers/TraderController.cs:64-66` 逐字）：
///     `foreach (var item in trader.Assort.BarterScheme)`
///     `    BarterScheme barterScheme = item.Value.FirstOrDefault()?.FirstOrDefault();`
///     ⇒ **value 是 `List&lt;List&lt;BarterScheme&gt;&gt;`；外层每条 = 一种"支付方案"，内层 = 该方案要交的多种物品**。
///       （与 `FenceBaseAssortGenerator.cs:74-81` 的构造方式一致。）
///   狗牌先例（实测 12 条）：Prapor 1 / Therapist 3 / Skier 2 / Peacekeeper 3 / Ragman 3；
///     例 Therapist `686e34716c2a18ed6b0eb466` ← `[[{count:80,_tpl:"59f32c3b…",level:15,side:"Any"}]]`，loyal=3。
///
/// ── ⚠ 为什么不用已有的 `BarterDimension.Apply` ──────────────────────────────────
///   它在本次实现前存在两处**硬故障**（已读源码确认，本文件**不修改**它，只在报告里指出）：
///   ① `BarterDimension.cs:94` `schemes[newId] = scheme;` —— `newId` 是 **string**，而键类型是 **MongoId**
///      ⇒ 抛 `ArgumentException: The value "…" is not of type "…MongoId"`，被 `catch` 吞掉 ⇒ 整组静默失败。
///      （本项目 `ExtraDimensions.cs:73-80` 已经踩过同一个坑并留了注释。）
///   ② `BarterDimension.CloneScheme` 返回的是**外层集合**（`List&lt;List&lt;BarterScheme&gt;&gt;`）而不是
///      `List&lt;List&lt;BarterScheme&gt;&gt;` 该有的**元素**；即使①修好，写进字典的 value 类型也不对。
///   ⇒ 本维度改为**自己按 JSON 构造正确类型**（`R.FromJson`），并显式做 MongoId 键转换（`R.DictSet`）。
///
/// ── 幂等 / 还原 / 降级 ────────────────────────────────────────────────────────
///   · 幂等：追加前扫 `items[]`，若已存在**同一 (reward tpl + cost tpl)** 的条目 ⇒ 跳过。
///   · 还原：登记闭包，把该 `_id` 从 items / barter_scheme / loyal_level_items **三处一起摘掉**。
///   · 降级：商人/assort/三键任一缺失 ⇒ warning + continue，绝不抛。
///   · 自证：追加后回读三处，确认 id 确实都在。
///
/// ── ★★★ v2.6.74【限购语义 · 全部反编译实证（SPT 4.1.6 / `_forensic_tmp/ilspy_full`）】──────────
///   字段（`Models/Eft/Common/Tables/Upd.cs:300-329`，**无 `[JsonPropertyName]` ⇒ JSON 名 = CLR 名**）：
///     `StackObjectsCount`(double?) / `UnlimitedCount`(bool?) /
///     `BuyRestrictionMax`(int?) / `BuyRestrictionCurrent`(int?)
///
///   ① **存量** = `StackObjectsCount`（`UnlimitedCount` **服务端完全不读**，只影响客户端显示：
///      全库仅 `InventoryHelper.cs:127-133` 把它清成 null 以便放进玩家背包）
///      · 扣：`TradeHelper.cs:97-107`
///          `if (item.Upd.StackObjectsCount < buyCount) throw new Exception("…would exceed the remaining stock left…")`
///          `item.Upd.StackObjectsCount -= buyCount;`
///        且写的对象是 `traderHelper.GetTraderAssortsByTraderId(id).Items` =
///        **`TraderTable` 里的活表**（`TraderHelper.cs:123-130`）。
///      · ★★ **补货不恢复存量**（这就是"买完必须重启服务端"的根因）：
///          `TraderController.cs:99-103` `Update()` → `traderAssortHelper.ResetExpiredTrader(trader2)`
///          → `TraderAssortHelper.cs:135-139` `trader.Assort.Items = GetPristineTraderAssorts(trader.Base.Id)`
///          → `:158-161` `cloner.Clone(traderHelper.GetTraderAssortsByTraderId(traderId).Items)`
///        —— "pristine" 取的是**同一张活表**，即把「已被扣空的表」克隆回自己 ⇒ 存量永远停在 0，
///        只有 `TraderController.Load()`（= 服务端启动，`TraderAssortHelper` 重新从库读/重新计算时才由 DB 重载）
///        才会回到初始值。
///      ⇒ 结论：**要让"卖不完"，只能把存量写大**（本体做法 = 9999999），或写 `UnlimitedCount` 但服务端不认它。
///
///   ② **每次补货限购** = `BuyRestrictionMax` + `BuyRestrictionCurrent`
///      · 生效判据：`ItemExtensions.cs:207-215 HasBuyRestrictions()`
///          `return (itemToCheck.Upd?.BuyRestrictionCurrent).HasValue && (itemToCheck.Upd?.BuyRestrictionMax).HasValue;`
///        ⇒ **两个字段必须同时存在**；只写 Max 不写 Current 等于没限购。
///      · 校验：`TradeHelper.cs:216-226 CheckPurchaseIsWithinTraderItemLimit`
///          `limit = traderHelper.GetAccountTypeAdjustedTraderPurchaseLimit(Upd.BuyRestrictionMax, GameVersion)`
///          `if (profile.TraderPurchases[traderId][assortId].PurchaseCount + count > limit) throw …`
///        · 计数累加：`TraderHelper.cs:421-445 AddTraderPurchasesToPlayerProfile`（写 `profile.TraderPurchases`）
///        · `GetAccountTypeAdjustedTraderPurchaseLimit`（`TraderHelper.cs:463-470`）= **EoD/Unheard ×1.2 向下取整**
///      · **重置时机（无需重启）**：
///          `TraderController.cs:99-103` 补货时 `traderPurchasePersisterService.ResetTraderPurchasesStoredInProfile(mongoId2)`
///          → `TraderPurchasePersisterService.cs:58-68` 把该商人在**所有** profile 的 `TraderPurchases[traderId]`
///          换成空字典 ⇒ 限购计数清零。补货时机 = `TraderAssortHelper.cs:147-151
///          TraderAssortsHaveExpired`：`traderTable[id].Base.NextResupply <= now`，
///          而 `NextResupply` 由 `TraderController.cs:52` = `GetNextUpdateTimestamp`（`configs/trader.json
///          → updateTime[<trader>].seconds`，本机 min/max 实值 2000~15000 秒；缺失时 `updateTimeDefault`
///          = 3600 秒；`tradersResetFromServerStart=true`）。
///      · 另有 `TraderPurchasePersisterService.cs:74-102 RemoveStalePurchasesFromProfiles`
///        （`PurchaseTimestamp + updateTime.seconds 随机值 < now` ⇒ 删），唯一调用点
///        `TraderController.cs:51`（仅 `Load()` 时跑一次）⇒ 属于兜底，不是主重置路径。
///      · 每次取 assort（`TraderAssortHelper.cs:34-79 GetAssort`）都会先
///        `ResetBuyRestrictionCurrentValue`（`:85-94`，把 hideout 根条目的 `BuyRestrictionCurrent` 置 0）
///        再按 `profile.TraderPurchases` 逐条回填 ⇒ 我们写的 `BuyRestrictionCurrent: 0` 只是**种子值**，
///        每个玩家看到的会是自己的实际计数。
///
///   ③ 本维度现在的写法（= **本体对"无限存量 + 限购"的标准形态**）：
///      `{"UnlimitedCount":true, "StackObjectsCount":9999999, "BuyRestrictionMax":N, "BuyRestrictionCurrent":0}`
///      · N = `maxPerRestock`（**>0 才写，≤0 = 一个都不写 ⇒ `HasBuyRestrictions()` 恒 false = 真不限购**）
///      · `rewardCount` 从此**不再参与**库存（旧实现把它当库存写进 `StackObjectsCount`，这是"买 N 件就卖空"的根因）；
///        玩家实际拿到几件由客户端请求的 count 决定（`TradeHelper.cs:131-140` 会把交付物的
///        `StackObjectsCount` 重写成 `min(StackMaxSize, 请求数)`）。
///      · ★ **本机实测统计（2026-10-24，`SPT_Data/database/traders/*/assort.json` 9 个商人）**：
///        2854 个 hideout 根商品里，**2655 个**就是上面这串（`UnlimitedCount=true` +
///        `StackObjectsCount=9999999` + 带 `BuyRestrictionMax`），另有 165 个是
///        `UnlimitedCount=true` + stock=2880000/2885000（弹药箱类）+ BuyRestrictionMax，
///        剩下 ~34 个才是不带 `UnlimitedCount` 的"有限存量"。
///        且其中 **412 条是「非货币易物」也带 `BuyRestrictionMax`**（例 Prapor `564ca9df4bdc2d35148b4569`
///        ← 7× `590c346786f77423e50ed342`，`UnlimitedCount=true`/stock=9999999/`BuyRestrictionMax=7`）
///        ⇒ **本体对"易物"同样用这套字段限购**，不是只对现金报价生效 ✔
/// </summary>
internal static class TraderAssortAddDimension
{
    public static object TraderTable;
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    /// <summary>
    /// 本轮是否已由「取消购买上限」维度（`unlocked_traders` 的 `removePurchaseLimits`）全局清过限购。
    /// <para>由 <see cref="ExtraDimensions.ApplyPurchaseLimits"/> 在成功清理后置 true，由 `Plugin.Restore()` 复位。
    /// 置 true 时本维度追加的条目**不写** `BuyRestrictionMax/Current`（= 与"官方全取消"口径一致）。</para>
    /// </summary>
    public static bool LimitsGloballyRemoved;

    /// <summary>
    /// ★ v2.6.74：注入条目的**库存**（= `upd.StackObjectsCount`）。
    /// <para>为什么不是 9999：这是 **EFT 本体自己的取值**。实测本体 `database/traders/*/assort.json`
    /// 里凡是「无限存量 + 限购」的商品一律写 <c>9999999</c>（例 `54cb50c76803fa8b248b4571/assort.json`
    /// 的 <c>686e34606c2a18ed6b0eabe7</c>：<c>{"UnlimitedCount":true,"StackObjectsCount":9999999,
    /// "BuyRestrictionMax":10,"BuyRestrictionCurrent":0}</c>）。照抄本体取值 ⇒ 客户端展示/服务端校验
    /// 都走同一条已验证过的路径。</para>
    /// <para>它只是「存量」而不是「一次给几件」：玩家实际拿到几件由客户端请求的
    /// <c>buyRequestData.Count</c> 决定，`TradeHelper.BuyItem` 会把交付物的
    /// <c>StackObjectsCount</c> 重写成 <c>min(StackMaxSize, Count)</c>（`TradeHelper.cs:131-140`）
    /// ⇒ 改这里**不会**让玩家一次拿到 9999999 件。</para>
    /// </summary>
    private const int UnlimitedStock = 9999999;

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][商人易物+] " + m); } catch { } }
    private static void Warn(string m) { try { Log?.Invoke("[EventRotator][商人易物+] ⚠ " + m); } catch { } }

    /// <summary>
    /// 应用 `traderAssortAdd`。
    /// 写法：<c>{ "Therapist": [ { "reward": "&lt;要买的东西&gt;", "cost": "&lt;狗牌 tpl&gt;", "costCount": 20, "loyalty": 2 } ] }</c>
    /// </summary>
    public static void Apply(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        if (ev?.TraderAssortAdd == null || ev.TraderAssortAdd.Count == 0) return;
        if (TraderTable == null) { Warn("拿不到 TraderTable，本维度跳过"); return; }
        try
        {
            var traders = R.Prop(TraderTable, "Traders") as IDictionary ?? TraderTable as IDictionary;
            if (traders == null) { Warn("TraderTable 不是字典，本维度跳过（结构变了？）"); return; }

            int added = 0, skipped = 0, missing = 0;
            foreach (var kv in ev.TraderAssortAdd)
            {
                var key = kv.Key;
                var adds = kv.Value;
                if (adds == null || adds.Count == 0) continue;

                var hit = FindTrader(traders, key);
                if (hit.Tr == null) { Warn($"找不到商人 '{key}'，跳过"); missing++; continue; }
                // 日志用**解析出来的**称呼（昵称优先），避免 key 是 id 时日志里只有一串十六进制
                var resolved = string.IsNullOrWhiteSpace(hit.Nick) ? key : hit.Nick;

                var assort = R.Prop(hit.Tr, "Assort");
                if (assort == null) { Warn($"商人 '{resolved}' 没有 Assort，跳过"); missing++; continue; }
                var items = R.Prop(assort, "Items") as IList;
                var schemes = R.Prop(assort, "BarterScheme") as IDictionary;
                var loyal = R.Prop(assort, "LoyalLevelItems") as IDictionary;
                if (items == null || schemes == null)
                {
                    Warn($"商人 '{resolved}' 的 Assort 缺 Items 或 BarterScheme，跳过");
                    missing++; continue;
                }

                foreach (var ad in adds)
                {
                    if (ad == null || string.IsNullOrWhiteSpace(ad.Reward) || string.IsNullOrWhiteSpace(ad.Cost)) continue;

                    // ── 幂等：同 (reward, cost) 已存在 ⇒ 跳过 ──
                    if (AlreadyHas(items, schemes, ad.Reward, ad.Cost)) { skipped++; continue; }

                    var newId = NewAssortId(items, schemes);
                    if (newId == null) { Warn("生成 assort id 失败，跳过"); missing++; continue; }

                    // ① items[]：一条挂在 hideout 根下的商品
                    // ★★★ v2.6.74【限购模式 · 修「买完要重启服务端才能再买」】
                    //   旧写法：`UnlimitedCount:false` + `StackObjectsCount = rewardCount(默认 1)`
                    //     ⇒ 库存只有 1，买完 `StackObjectsCount` 变 0，而 SPT 的 **补货并不恢复库存**
                    //       （实证：`TraderAssortHelper.ResetExpiredTrader` → `GetPristineTraderAssorts`
                    //        读的是 `traderHelper.GetTraderAssortsByTraderId(id)` = **同一张活表**，
                    //        而购买时 `TradeHelper.BuyItem` 扣的正是这张活表的 `StackObjectsCount`
                    //        ⇒ 补货把「已被扣空的表」克隆回自己 ⇒ 库存永远停在 0，只有重启重新读库才恢复）。
                    //   新写法 = **EFT 原生「无限存量 + 每次补货限购」**，逐字对齐本体 assort.json 实测样例
                    //     `{"UnlimitedCount":true,"StackObjectsCount":9999999,"BuyRestrictionMax":N,"BuyRestrictionCurrent":0}`
                    //     （例：`database/traders/54cb50c76803fa8b248b4571/assort.json` 里 951 条中大量为此形态）。
                    //   语义分工（每条都有 file:line 实证，见类注释「限购语义」一节）：
                    //     · `StackObjectsCount` = **库存**（`TradeHelper.cs:100-105` 检查 + 扣除；永久消耗）
                    //     · `BuyRestrictionMax` + `BuyRestrictionCurrent` = **每次补货的购买上限**
                    //       （`ItemExtensions.cs:207-215 HasBuyRestrictions` 要求**两个都存在**才生效；
                    //        `TradeHelper.cs:216-226 CheckPurchaseIsWithinTraderItemLimit` 用
                    //        `profile.TraderPurchases[trader][assort].PurchaseCount` 比对；
                    //        该计数由 `TraderController.Update` 在补货时清空 ⇒ **按补货周期自动重置，无需重启**）
                    //   ⇒ `MaxPerRestock <= 0` = 真正不限购（两个字段都不写 ⇒ `HasBuyRestrictions()` 恒 false）。
                    //   ★ 与「取消购买上限」维度的协作：`unlocked_traders`（官方 "All item purchase limits are removed"）
                    //     的 `ExtraDimensions.ApplyPurchaseLimits` 在**本次活动循环的更早位置**（Plugin.cs:1941）
                    //     把所有**当时已存在**的 assort 条目的 `BuyRestrictionMax` 置 null；而本维度在 :2082 才追加
                    //     ⇒ 同一轮里追加的条目**赶不上**那次清理。所以这里读那个维度留下的标志位：
                    //     本轮已声明「取消购买上限」⇒ 我们追加的条目也**不写限购**（否则"官方说全取消、我们的还在限"）。
                    var itemElem = R.ElemType(items);
                    var restriction = (ad.MaxPerRestock > 0 && !LimitsGloballyRemoved)
                        ? ",\"BuyRestrictionMax\":" + ad.MaxPerRestock + ",\"BuyRestrictionCurrent\":0"
                        : "";
                    var itemJson = "{"
                        + "\"_id\":\"" + newId + "\","
                        + "\"_tpl\":\"" + ad.Reward + "\","
                        + "\"parentId\":\"hideout\",\"slotId\":\"hideout\","
                        + "\"upd\":{\"UnlimitedCount\":true,"
                        + "\"StackObjectsCount\":" + UnlimitedStock + restriction + "}}";
                    var item = R.FromJson(itemElem, itemJson, Warn);
                    if (item == null) { Warn($"构造商品条目失败（{ad.Reward}），跳过"); missing++; continue; }

                    // ② barter_scheme[newId] = List<List<BarterScheme>> —— 一种方案，方案内一种物品
                    var schemeValType = R.DictValueType(schemes);                       // List<List<BarterScheme>>
                    var schemeElemType = schemeValType?.GetGenericArguments().FirstOrDefault();  // List<BarterScheme>
                    var barterType = schemeElemType?.GetGenericArguments().FirstOrDefault();     // BarterScheme
                    if (schemeValType == null || schemeElemType == null || barterType == null)
                    {
                        Warn("推不出 BarterScheme 集合类型（SPT 结构变了？），跳过"); missing++; continue;
                    }

                    var sideJson = string.IsNullOrWhiteSpace(ad.Side) ? "" : ",\"side\":\"" + ad.Side + "\"";
                    var barterJson = "{\"count\":" + Math.Max(1, ad.CostCount) + ","
                                   + "\"_tpl\":\"" + ad.Cost + "\","
                                   + "\"onlyFunctional\":false,\"level\":" + Math.Max(1, ad.CostLevel) + sideJson + "}";
                    var barter = R.FromJson(barterType, barterJson, Warn);
                    if (barter == null) { Warn($"构造 BarterScheme 失败（cost {ad.Cost}），跳过"); missing++; continue; }

                    // 组装 List<List<BarterScheme>>
                    object innerList = null, outerList = null;
                    try
                    {
                        innerList = Activator.CreateInstance(schemeElemType);
                        ((IList)innerList).Add(barter);
                        outerList = Activator.CreateInstance(schemeValType);
                        ((IList)outerList).Add(innerList);
                    }
                    catch (Exception ex) { Warn($"组装 barter_scheme 失败：{ex.Message}"); missing++; continue; }

                    // ③ 写入三处（★ 键必须转成 MongoId，否则抛 ArgumentException）
                    items.Add(item);
                    if (!R.DictSet(schemes, newId, outerList))
                    {
                        items.Remove(item);
                        Warn($"写入 barter_scheme 失败（{newId}）—— 已回滚 items，跳过"); missing++; continue;
                    }
                    if (loyal != null) R.DictSet(loyal, newId, Math.Max(1, ad.Loyalty));

                    var capItems = items; var capSchemes = schemes; var capLoyal = loyal; var capId = newId;
                    AddRestore?.Invoke(() =>
                    {
                        try
                        {
                            foreach (var it in capItems.Cast<object>().ToList())
                            {
                                var id2 = R.Prop(it, "Id")?.ToString();
                                if (string.Equals(id2, capId, StringComparison.OrdinalIgnoreCase)) { capItems.Remove(it); break; }
                            }
                            R.DictRemove(capSchemes, capId);
                            if (capLoyal != null) R.DictRemove(capLoyal, capId);
                        }
                        catch { }
                    });
                    added++;

                    // ── 回读自证（三处都要在 + 限购/库存字段真的写进去了）──
                    bool inItems = items.Cast<object>().Any(x =>
                        string.Equals(R.Prop(x, "Id")?.ToString(), newId, StringComparison.OrdinalIgnoreCase));
                    bool inScheme = R.DictHas(schemes, newId);
                    bool inLoyal = loyal == null || R.DictHas(loyal, newId);
                    // ★ v2.6.74：回读 upd 里三个限购相关字段，避免"写了但 SPT 没读到"（JSON 名写错的经典坑）
                    string updProof = "(upd 回读失败)";
                    try
                    {
                        var justAdded = items.Cast<object>().FirstOrDefault(x =>
                            string.Equals(R.Prop(x, "Id")?.ToString(), newId, StringComparison.OrdinalIgnoreCase));
                        var upd2 = R.Prop(justAdded, "Upd");
                        if (upd2 != null)
                            updProof = $"stock={R.Prop(upd2, "StackObjectsCount")}"
                                     + $"/unlimited={R.Prop(upd2, "UnlimitedCount")}"
                                     + $"/max={R.Prop(upd2, "BuyRestrictionMax")?.ToString() ?? "null(不限购)"}"
                                     + $"/cur={R.Prop(upd2, "BuyRestrictionCurrent")?.ToString() ?? "null"}";
                    }
                    catch { }
                    Info($"★{resolved}：LL{ad.Loyalty} 新增易物 {ad.CostCount}× {ad.Cost}"
                       + (string.IsNullOrWhiteSpace(ad.Side) ? "" : $"（{ad.Side} 专属）")
                       + $" → {ad.Reward}（**限购模式**：每次补货 {(ad.MaxPerRestock > 0 ? "可买 " + ad.MaxPerRestock + " 件" : "不限")}"
                       + $"，库存 {UnlimitedStock} 不会卖到 0）"
                       + $"（三键回读 items={inItems} scheme={inScheme} loyal={inLoyal} {updProof}"
                       + ((inItems && inScheme && inLoyal) ? " ✔" : " —— ⚠ 有缺失！") + "，可还原）");
                }
            }
            Info($"★商人 assort 追加：新增 {added} 条、幂等跳过 {skipped} 条、未找到 {missing} 条");
        }
        catch (Exception ex) { Warn("失败（已降级，不影响服务端）：" + ex.Message); }
    }

    /// <summary>幂等判据：items 里存在同 reward tpl、且其 barter_scheme 的第一项 cost tpl 相同。</summary>
    private static bool AlreadyHas(IList items, IDictionary schemes, string reward, string cost)
    {
        try
        {
            foreach (var it in items)
            {
                var id = R.Prop(it, "Id")?.ToString();
                var tpl = R.Prop(it, "Template")?.ToString() ?? R.Prop(it, "_tpl")?.ToString();
                if (string.IsNullOrEmpty(id) || !string.Equals(tpl, reward, StringComparison.OrdinalIgnoreCase)) continue;
                var val = R.DictGet(schemes, id) as IEnumerable;
                var firstScheme = val?.Cast<object>().FirstOrDefault() as IEnumerable;
                var firstBarter = firstScheme?.Cast<object>().FirstOrDefault();
                var c = R.Prop(firstBarter, "Template")?.ToString() ?? R.Prop(firstBarter, "_tpl")?.ToString();
                if (string.Equals(c, cost, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    private static readonly Random _rnd = new();

    /// <summary>生成 24 位十六进制 assort id（避开已存在的）。</summary>
    private static string NewAssortId(IList items, IDictionary schemes)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var it in items)
            {
                var id = R.Prop(it, "Id")?.ToString();
                if (!string.IsNullOrEmpty(id)) used.Add(id);
            }
            foreach (var k in schemes.Keys) if (k != null) used.Add(k.ToString());
        }
        catch { }
        for (int i = 0; i < 128; i++)
        {
            var s = string.Concat(Enumerable.Range(0, 24).Select(_ => "0123456789abcdef"[_rnd.Next(16)]));
            if (!used.Contains(s)) return s;
        }
        return null;
    }

    /// <summary>按 id 或昵称找商人（与 TraderAssortDimension.Traders() 同一套读法）。</summary>
    /// <remarks>
    /// ★ 匹配顺序（**精确优先**，避免近似昵称互相抢）：
    ///   ① 昵称**精确**相等 → ② id 精确相等 → ③ 昵称**包含**（仅当①②都没命中时才用）。
    ///   旧 `BarterDimension` 只做 id 精确匹配，所以 `traderBarterAdd` 的键一直是 id；
    ///   新字段 `traderAssortAdd` 也允许写昵称（更可读）。两种键都能命中。
    /// </remarks>
    private static (string Id, string Nick, object Tr) FindTrader(IDictionary table, string key)
    {
        (string Id, string Nick, object Tr) idHit = (null, null, null);
        (string Id, string Nick, object Tr) subHit = (null, null, null);

        foreach (DictionaryEntry kv in table)
        {
            var tr = kv.Value;
            if (tr == null) continue;
            var id = kv.Key?.ToString() ?? "";
            var baseObj = R.Prop(tr, "Base");
            var nick = (R.Prop(baseObj, "Nickname") ?? R.Prop(baseObj, "Name") ?? "").ToString();

            // ① 昵称精确 —— 命中即返回
            if (!string.IsNullOrEmpty(nick) && string.Equals(nick, key, StringComparison.OrdinalIgnoreCase))
                return (id, nick, tr);
            // ② id 精确（先记下，扫完整表再返回，保证①优先）
            if (idHit.Tr == null && string.Equals(id, key, StringComparison.OrdinalIgnoreCase))
                idHit = (id, nick, tr);
            // ③ 昵称包含（兜底）
            if (subHit.Tr == null && !string.IsNullOrEmpty(nick)
                && nick.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                subHit = (id, nick, tr);
        }
        if (idHit.Tr != null) return idHit;
        return subHit;
    }
}
