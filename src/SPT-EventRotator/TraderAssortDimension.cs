using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★★ v0.1.5 新增维度：**商人 assort（按商人粒度的价格折扣）**。
///
/// 为什么需要它（审计实证）：
///   · 官方这类活动的真实机制是**特定某个商人**打折/改易物配方，例如
///     「AA-12 由 Skier 折扣出售」/「Prapor、Peacekeeper 折扣卖下挂榴弹」/「Mechanic 用食品易物热成像」；
///   · 而本 mod 过去只有两个手段：全局 `TraderConfig.TraderPriceMultiplier`（**所有商人**）
///     或 `unlockTraders`（把**所有商人**解锁到最高）—— 都是"副作用过大"的近似；
///   · `loyaltyLevels[].buy_price_coef` **不是**落点：它经 `TraderHelper.GetHighestSellToTraderPrice`
///     只影响"玩家**卖给**商人"的价（`percentOfValue(100 - coef, templatePrice)`），与"商人卖给你"无关。
///
/// 真正落点：该商人 `Trader.Assort.BarterScheme`（`Dictionary&lt;itemId, List&lt;List&lt;BarterScheme&gt;&gt;&gt;`）
/// 里**货币条目**的 `Count`。把它按倍率缩放，就是"这个商人的报价整体打折/涨价"，
/// 而且**只影响这一个商人**，并可通过 `AddRestore` 完整还原。
///
/// events.json 用法：
/// <code>
/// "traderPriceMultiplierPerTrader": { "Skier": 0.7, "Prapor": 0.75, "Peacekeeper": 0.75 }
/// </code>
/// 键可以是**商人昵称**（Prapor / Therapist / Skier / Peacekeeper / Mechanic / Ragman / Jaeger / Fence…）
/// 或**商人 id**（MongoId）。
/// </summary>
internal static class TraderAssortDimension
{
    /// <summary>SPT TraderTable：Dictionary&lt;MongoId, Trader&gt;（由 Plugin 注入）。</summary>
    public static object TraderTable;
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][商人] " + m); } catch { } }

    private const BindingFlags Any =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

    // 货币 tpl（卢布 / 美元 / 欧元）—— 只有这些才是"价格"，其它是易物物品
    private static readonly HashSet<string> Money = new(StringComparer.OrdinalIgnoreCase)
    {
        "5449016a4bdc2d6f028b456f",   // roubles
        "5696686a4bdc2da3298b456a",   // dollars
        "569668774bdc2da2298b4568",   // euros
    };

    /// <summary>
    /// ★ SPT 的商人 assort 是**懒加载**的（首次被请求时才 build）⇒ 服务器刚启动时 BarterScheme 可能是空的。
    /// 这里把"还没能应用"的折扣挂起，由 <see cref="ReapplyPending"/> 在战局生成时补一次。
    /// </summary>
    private static readonly Dictionary<string, double> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _diagDone = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>assort 就绪后补应用挂起的折扣（幂等：应用成功即从挂起表移除）。</summary>
    public static void ReapplyPending(ISptLogger<EventRotatorServer> logger = null)
    {
        if (Pending.Count == 0) return;
        try
        {
            var all = Traders();
            foreach (var kv in Pending.ToList())
            {
                var tr = all.FirstOrDefault(t => string.Equals(t.Id, kv.Key, StringComparison.OrdinalIgnoreCase)).Tr;
                if (tr == null) { Pending.Remove(kv.Key); continue; }
                var schemes = Prop(Prop(tr, "Assort"), "BarterScheme") as IDictionary;
                if (schemes == null || schemes.Count == 0) continue;      // 还没就绪，下次再来
                var fake = new EventDef { TraderPriceMultiplierPerTrader = new() { [kv.Key] = kv.Value } };
                Pending.Remove(kv.Key);
                Apply(fake, logger);
            }
        }
        catch { }
    }

    // ★ v1.0.1【性能·第五轮】原来每次 `GetProperty(name, Any)` 都做一次裸反射。
    //   本文件有 19 处 `Prop(` 调用，且 `Apply` 要遍历 453~763 条 scheme ⇒ 每进图数千次反射。
    //   只缓存 `PropertyInfo`（**不回落字段**，与原实现逐字等价；`GetValue` 异常照旧向上抛）。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), PropertyInfo> _piCache = new();

    private static object Prop(object o, string name) =>
        o == null ? null
                  : _piCache.GetOrAdd((o.GetType(), name),
                        static k => k.Item1.GetProperty(k.Item2, Any))?.GetValue(o);

    /// <summary>把 TraderTable 展平成 (id, 昵称, Trader) 列表。</summary>
    private static List<(string Id, string Nick, object Tr)> Traders()
    {
        var outp = new List<(string, string, object)>();
        if (TraderTable is not IDictionary d) return outp;
        foreach (DictionaryEntry kv in d)
        {
            var tr = kv.Value;
            if (tr == null) continue;
            var nick = (Prop(Prop(tr, "Base"), "Nickname") ?? Prop(Prop(tr, "Base"), "Name") ?? "").ToString();
            outp.Add((kv.Key?.ToString() ?? "", nick ?? "", tr));
        }
        return outp;
    }

    public static void Apply(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            if (ev?.TraderPriceMultiplierPerTrader == null || ev.TraderPriceMultiplierPerTrader.Count == 0) return;
            if (TraderTable == null) { logger?.Warning("[EventRotator][商人] 拿不到 TraderTable，按商人折扣未生效"); return; }

            var all = Traders();
            foreach (var kv in ev.TraderPriceMultiplierPerTrader)
            {
                var want = (kv.Key ?? "").Trim();
                var factor = kv.Value;
                if (want.Length == 0 || factor <= 0) continue;

                var hit = all.FirstOrDefault(t => string.Equals(t.Id, want, StringComparison.OrdinalIgnoreCase));
                if (hit.Tr == null)
                    hit = all.FirstOrDefault(t => string.Equals(t.Nick, want, StringComparison.OrdinalIgnoreCase));
                if (hit.Tr == null)
                {
                    logger?.Warning($"[EventRotator][商人] 找不到商人 '{want}'（昵称或 id），跳过");
                    continue;
                }

                var assort = Prop(hit.Tr, "Assort");
                var schemes = assort == null ? null : Prop(assort, "BarterScheme") as IDictionary;
                if (schemes == null) { logger?.Warning($"[EventRotator][商人] {hit.Nick} 没有 BarterScheme"); continue; }
                if (schemes.Count == 0)
                {
                    // 诊断：SPT 的 assort 是**懒加载**的（首次被请求时才 build）⇒ 服务器刚起时可能还没填。
                    // 此时先把"待应用"的折扣记下来，等 assort 就绪后再补（见 Pending 机制）。
                    Pending[hit.Id] = factor;
                    logger?.Warning($"[EventRotator][商人] {hit.Nick} 的 BarterScheme 目前为空（SPT assort 懒加载）"
                                  + $" → 折扣 ×{factor} 已挂起，等 assort 就绪后再应用");
                    continue;
                }

                int items = 0, entries = 0;
                // 诊断（只打一次）：把该商人 assort 的结构与样本打出来，便于定位"为什么改了 0 条"
                if (!_diagDone.Contains(hit.Id))
                {
                    _diagDone.Add(hit.Id);
                    try
                    {
                        var sample = new List<string>();
                        int shown = 0;
                        foreach (DictionaryEntry it0 in schemes)
                        {
                            var v0 = it0.Value;
                            sample.Add($"item={it0.Key} valueType={v0?.GetType().Name}");
                            if (v0 is IEnumerable o0)
                                foreach (var one0 in o0)
                                {
                                    sample.Add($"  scheme={one0?.GetType().Name}");
                                    if (one0 is IEnumerable i0)
                                        foreach (var bs0 in i0)
                                        {
                                            var tpl0 = Prop(bs0, "Template") ?? Prop(bs0, "Tpl");
                                            var c0 = Prop(bs0, "Count");
                                            sample.Add($"    tpl={tpl0} ({tpl0?.GetType().Name}) count={c0} ({c0?.GetType().Name})");
                                        }
                                }
                            if (++shown >= 2) break;
                        }
                        // ★ v1.0.1【性能】结构诊断**降级为 verbose**（实测：这些行占启动 ~11 ms；
                        //   而 sample 的构建（下面那段反射 + 字符串拼接）**本身就是开销**）
                        //   ⚠ 只降"诊断"类；`applied 'xxx'` / `loaded N events` 等关键日志**保持 Info** ✔
                        if (EventEngine.VerboseLogging)
                            logger?.Info($"[EventRotator][商人] 结构诊断 {hit.Nick}（schemes={schemes.Count}）: " + string.Join(" | ", sample.Take(10)));
                    }
                    catch (Exception dex) { if (EventEngine.VerboseLogging) logger?.Info($"[EventRotator][商人] 结构诊断失败: {dex.Message}"); }
                }
                foreach (DictionaryEntry item in schemes)
                {
                    if (item.Value is not IEnumerable outer) continue;
                    bool touched = false;
                    foreach (var one in outer)
                    {
                        if (one is not IEnumerable inner) continue;
                        foreach (var bs in inner)
                        {
                            if (bs == null) continue;
                            // ★ 注意 CLR 名 ≠ JSON 名：BarterScheme 的 JSON 名是 `_tpl`，而 C# 属性叫 **Template**
                            //   （旧代码取 "Tpl" ⇒ 永远 null ⇒ 0 条命中，实测日志 `tpl= ()` 铁证）
                            var tplObj = Prop(bs, "Template") ?? Prop(bs, "Tpl");
                            var tpl = (tplObj ?? "").ToString();
                            if (!Money.Contains(tpl)) continue;               // 只缩放"价格"，不动易物物品
                            var cntObj = Prop(bs, "Count");
                            double cnt;
                            try { cnt = Convert.ToDouble(cntObj); } catch { continue; }
                            if (cnt <= 0) continue;

                            double want2 = Math.Max(1, Math.Round(cnt * factor));
                            if (Math.Abs(want2 - cnt) < 0.0001) continue;
                            var cap = bs; var capOld = cnt;
                            AddRestore?.Invoke(() =>
                            {
                                try
                                {
                                    var p = cap.GetType().GetProperty("Count", Any);
                                    if (p != null && p.CanWrite) p.SetValue(cap, Convert.ChangeType(capOld, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType));
                                }
                                catch { }
                            });
                            try
                            {
                                var p = bs.GetType().GetProperty("Count", Any);
                                if (p != null && p.CanWrite)
                                {
                                    p.SetValue(bs, Convert.ChangeType(want2, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType));
                                    entries++; touched = true;
                                }
                            }
                            catch { }
                        }
                    }
                    if (touched) items++;
                }
                Info($"{hit.Nick}（{hit.Id}）整体报价 ×{factor:0.##}：改了 {items} 个商品的 {entries} 条货币报价（可还原）");
            }
        }
        catch (Exception ex) { logger?.Warning($"[EventRotator][商人] 按商人折扣失败: {ex.Message}"); }
    }

    private static string GetTpl(object item) =>
        (Prop(item, "Template") ?? Prop(item, "_tpl") ?? "").ToString();

    private static string GetId(object item) =>
        (Prop(item, "Id") ?? Prop(item, "_id") ?? "").ToString();

    private static string GetParent(object item)
    {
        var v = Prop(item, "ParentId") ?? Prop(item, "parentId");
        return v?.ToString() ?? "";
    }

    /// <summary>
    /// ★★ v0.1.5 新增：把指定 tpl 的商品从商人 assort 里**下架**（含其子件与报价条目），并登记还原。
    /// 键可用商人 id、昵称，或 `"*"` 表示所有商人。
    ///
    /// ★ 实测关键（踩过一次）：SPT 4.1.6 的 `TraderAssort.Items` 里，商品是挂在**名为 "hideout" 的根容器**下的
    ///   **子件**（父 id 就是那个 hideout 根），而报价表 `BarterScheme` 是**按子件 id** 存的。
    ///   所以必须按"命中 tpl 的条目（含子件）+ 该条目自己的 scheme 键"来删 ——
    ///   只找"根商品"会一个都找不到（实测 Prapor 命中 14 条、**根=0 子件=14**）。
    ///   为了不改动"武器/容器报价里的配件"，只删**父为 hideout 根**的条目与根条目本身，其它命中项跳过并计数。
    /// </summary>
    public static void RemoveFromAssort(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            if (ev?.TraderAssortRemove == null || ev.TraderAssortRemove.Count == 0) return;
            var all = Traders();
            int removedTotal = 0, skipped = 0;
            var removedTpls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in ev.TraderAssortRemove)
            {
                var key = kv.Key ?? "";
                var tpls = new HashSet<string>(kv.Value ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                if (tpls.Count == 0) continue;
                var targets = key == "*" ? all
                    : all.Where(x => string.Equals(x.Id, key, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(x.Nick, key, StringComparison.OrdinalIgnoreCase)).ToList();
                if (targets.Count == 0) { Info($"下架：找不到商人 {key}"); continue; }

                foreach (var hit in targets)
                {
                    var assort = Prop(hit.Tr, "Assort");
                    var items = Prop(assort, "Items") as System.Collections.IList;
                    var schemes = Prop(assort, "BarterScheme") as IDictionary;
                    if (items == null || items.Count == 0) continue;

                    // 找 hideout 根（SPT 把商品挂在这种合成根下）
                    var hideoutRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var it in items)
                        if (it != null && string.IsNullOrEmpty(GetParent(it)))
                            hideoutRoots.Add(GetId(it));

                    // 命中项：根自身 或 父为 hideout 根
                    var victims = new List<object>();
                    foreach (var it in items)
                    {
                        if (it == null || !tpls.Contains(GetTpl(it))) continue;
                        var par = GetParent(it);
                        // ★ 实测：SPT 把商人商品挂在 parentId 为**字面量 "hideout"** 的哨兵下（不是某个根 id）
                        if (par.Length == 0 || hideoutRoots.Contains(par)
                            || par.Equals("hideout", StringComparison.OrdinalIgnoreCase)) victims.Add(it);
                        else skipped++;
                    }
                    if (victims.Count == 0) continue;

                    foreach (var victim in victims)
                    {
                        var vid = GetId(victim);
                        // 该条目的子孙（一般没有，保险）
                        var sub = new List<object> { victim };
                        var frontier = new List<string> { vid };
                        while (frontier.Count > 0)
                        {
                            var next = new List<string>();
                            foreach (var it in items)
                            {
                                if (it == null) continue;
                                var p2 = GetParent(it);
                                if (p2.Length > 0 && frontier.Contains(p2, StringComparer.OrdinalIgnoreCase))
                                { sub.Add(it); next.Add(GetId(it)); }
                            }
                            frontier = next;
                        }
                        var snapshot = sub.ToList();
                        object schemeVal = null; var hasScheme = false;
                        if (schemes != null && schemes.Contains(vid)) { schemeVal = schemes[vid]; hasScheme = true; }
                        var capItems = items; var capSchemes = schemes; var capSnap = snapshot;
                        var capId = vid; var capSchemeVal = schemeVal; var capHas = hasScheme;
                        var idx = items.IndexOf(victim);
                        foreach (var s2 in snapshot) items.Remove(s2);
                        if (hasScheme) schemes.Remove(vid);
                        AddRestore?.Invoke(() =>
                        {
                            try
                            {
                                var at = Math.Min(Math.Max(idx, 0), capItems.Count);
                                foreach (var s3 in capSnap) { if (!capItems.Contains(s3)) capItems.Insert(at, s3); }
                                if (capHas && capSchemes != null && !capSchemes.Contains(capId))
                                    capSchemes[capId] = capSchemeVal;
                            }
                            catch { }
                        });
                        removedTotal++;
                        removedTpls.Add(GetTpl(victim));
                    }
                }
            }
            Info($"★按官方清单下架：共移除 {removedTotal} 个报价（覆盖 {removedTpls.Count} 种 tpl，可还原）"
                 + (skipped > 0 ? $"；另有 {skipped} 条命中项属于「武器/容器报价内的配件」故未动" : ""));
        }
        catch (Exception ex) { logger?.Warning($"[EventRotator][商人] 下架失败: {ex.Message}"); }
    }

    /// <summary>
    /// ★★ v0.1.5：**关掉现金报价**（官方 June 2022 删档前 Day 4「Barter Only」：除 Fence 外商人只收易物）。
    /// 做法：对每个商人的 `BarterScheme`，凡"第一个易物项的货币是卢布/美元/欧元"的条目**整条下架**
    /// （物品条目 + 报价条目一起删，可还原）。
    /// </summary>
    public static void DisableCashOffers(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            if (ev == null || !ev.TraderCashOffersDisabled) return;
            var all = Traders();
            int removed = 0;
            foreach (var hit in all)
            {
                // 官方原文：except for Fence
                if (string.Equals(hit.Nick, "Fence", StringComparison.OrdinalIgnoreCase)) continue;
                var assort = Prop(hit.Tr, "Assort");
                var items = Prop(assort, "Items") as System.Collections.IList;
                var schemes = Prop(assort, "BarterScheme") as IDictionary;
                if (items == null || schemes == null || schemes.Count == 0) continue;

                var killIds = new List<string>();
                foreach (DictionaryEntry entry in schemes)
                {
                    var key = entry.Key?.ToString() ?? "";
                    var isCash = false;
                    try
                    {
                        var outer = entry.Value as System.Collections.IEnumerable;
                        foreach (var inner in outer ?? System.Linq.Enumerable.Empty<object>())
                        {
                            var innerList = inner as System.Collections.IEnumerable;
                            if (innerList == null) continue;
                            foreach (var bs in innerList)
                            {
                                var tpl = (Prop(bs, "Template") ?? Prop(bs, "Tpl") ?? "").ToString();
                                if (Money.Contains(tpl)) isCash = true;
                            }
                            break;   // 只看第一组（SPT 的报价结构：List<List<BarterScheme>>）
                        }
                    }
                    catch { }
                    if (isCash) killIds.Add(key);
                }
                if (killIds.Count == 0) continue;

                foreach (var kid in killIds)
                {
                    object victim = null;
                    foreach (var it in items) if (it != null && string.Equals(GetId(it), kid, StringComparison.OrdinalIgnoreCase)) { victim = it; break; }
                    if (victim == null) { schemes.Remove(kid); continue; }
                    var capItems = items; var capSchemes = schemes; var capVictim = victim;
                    var capVal = schemes[kid]; var idx = items.IndexOf(victim);
                    items.Remove(victim); schemes.Remove(kid);
                    AddRestore?.Invoke(() =>
                    {
                        try
                        {
                            var at = Math.Min(Math.Max(idx, 0), capItems.Count);
                            if (!capItems.Contains(capVictim)) capItems.Insert(at, capVictim);
                            if (!capSchemes.Contains(kid)) capSchemes[kid] = capVal;
                        }
                        catch { }
                    });
                    removed++;
                }
            }
            Info($"★关闭现金报价（官方 Barter Only：除 Fence 外只收易物）：共下架 {removed} 条报价（可还原）");
        }
        catch (Exception ex) { logger?.Warning($"[EventRotator][商人] 关闭现金报价失败: {ex.Message}"); }
    }
}
