using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★★ v0.1.5 新增维度：**手册价格（handbook price）改写**。
///
/// 为什么需要它：
///   · 官方「Dogtag Sales Event」(2022-12-19) 原文：「**Dogtag sales price has been increased to ₽2,646 per level**」
///     —— 官方给了**确切数值**，而落点既不是弹匣也不是弹药，而是**物品的手册价**；
///   · 官方「RAM Shortage」(2025-12-15) 第 ① 条「RAM sticks have their **value increased**」同理；
///   · 旧实现（ram_shortage）用的是 `traderPriceMultiplier: 1.35` —— 那是**所有商人的所有商品**一起涨价 ✗ 落点错。
///
/// 落点（反编译实证）：`HandbookHelper.HandbookPriceCache` 是 **protected** 属性，背后是私有字段
/// `_handbookPriceCache`，其 `Items.ById`（`Dictionary&lt;MongoId, double&gt;`）就是价格表，由
/// `HydrateHandbookCache()` 从 **`TemplateTable.Handbook.Items[].Price`** 初始化。
/// ⇒ 本维度同时改两处：① `TemplateTable.Handbook.Items[].Price`（数据源）；
///    ② 若 `_handbookPriceCache` 已水合，直接改其中的 `Items.ById[tpl]`（否则运行期读到的还是旧价）。
///
/// events.json 用法：
/// <code>
/// "handbookPriceSet":        { "59f32bb586f774757e1e8442": 2646 }   // 直接设为指定价格
/// "handbookPriceMultiplier": { "57347baf24597738002c6178": 1.5 }    // 按倍率缩放现价
/// </code>
/// 全部可还原。
/// </summary>
internal static class PriceDimension
{
    public static object TemplateTable;      // SPT TemplateTable（由 Plugin 注入）
    public static object HandbookHelper;     // SPT HandbookHelper（用于同步其私有价格缓存；可为 null）
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    private const BindingFlags Any =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][价格] " + m); } catch { } }

    /// <summary>★ v1.0.1【性能·第六轮】本文件 17 处 Prop 调用；PropertyInfo 走 R.GP 缓存。</summary>
    private static object Prop(object o, string n) =>
        o == null ? null : R.GP(o.GetType(), n, Any)?.GetValue(o);

    private static bool SetProp(object o, string n, object v)
    {
        var p = o?.GetType().GetProperty(n, Any);
        if (p == null || !p.CanWrite) return false;
        try
        {
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            p.SetValue(o, Convert.ChangeType(v, t));
            return true;
        }
        catch { return false; }
    }

    /// <summary>把价格写进 TemplateTable.Handbook 与（若已水合）HandbookHelper 的私有缓存。</summary>
    private static bool WritePrice(string tpl, double price, out double oldPrice)
    {
        oldPrice = 0;
        bool found = false;

        // ① 数据源：TemplateTable.Handbook.Items[]
        try
        {
            var handbook = Prop(TemplateTable, "Handbook");
            var items = Prop(handbook, "Items") as IEnumerable;
            if (items != null)
                foreach (var hi in items)
                {
                    if (hi == null) continue;
                    var id = (Prop(hi, "Id") ?? "").ToString();
                    if (!string.Equals(id, tpl, StringComparison.OrdinalIgnoreCase)) continue;
                    var cur = Prop(hi, "Price");
                    oldPrice = cur == null ? 0 : Convert.ToDouble(cur);
                    if (SetProp(hi, "Price", price)) found = true;
                    break;
                }
        }
        catch (Exception ex) { Info("写 Handbook 失败: " + ex.Message); }

        // ② 运行期缓存：HandbookHelper._handbookPriceCache.Items.ById
        try
        {
            var cache = Prop(HandbookHelper, "_handbookPriceCache");
            var byId = Prop(Prop(cache, "Items"), "ById") as IDictionary;
            if (byId != null)
            {
                foreach (var k in byId.Keys.Cast<object>().Select(x => x?.ToString() ?? "").ToList())
                {
                    if (!string.Equals(k, tpl, StringComparison.OrdinalIgnoreCase)) continue;
                    var cap = byId; var capKey = k; var capOld = byId[k];
                    AddRestore?.Invoke(() => { try { cap[capKey] = capOld; } catch { } });
                    byId[k] = price;
                    found = true;
                    break;
                }
            }
        }
        catch { }

        return found;
    }

    public static void Apply(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            if (ev == null) return;
            bool hasSet = ev.HandbookPriceSet != null && ev.HandbookPriceSet.Count > 0;
            bool hasMul = ev.HandbookPriceMultiplier != null && ev.HandbookPriceMultiplier.Count > 0;
            if (!hasSet && !hasMul) return;
            if (TemplateTable == null) { logger?.Warning("[EventRotator][价格] 拿不到 TemplateTable，未生效"); return; }

            int ok = 0, miss = 0;
            var missList = new List<string>();

            if (hasSet)
                foreach (var kv in ev.HandbookPriceSet)
                {
                    if (!WritePrice(kv.Key, kv.Value, out var old)) { miss++; missList.Add(kv.Key); continue; }
                    // Handbook 数据源的还原（缓存部分已在 WritePrice 内登记）
                    var tplCap = kv.Key; var oldCap = old;
                    AddRestore?.Invoke(() =>
                    {
                        try
                        {
                            var handbook = Prop(TemplateTable, "Handbook");
                            var items = Prop(handbook, "Items") as IEnumerable;
                            if (items == null) return;
                            foreach (var hi in items)
                            {
                                if (hi == null) continue;
                                if (string.Equals((Prop(hi, "Id") ?? "").ToString(), tplCap, StringComparison.OrdinalIgnoreCase))
                                { SetProp(hi, "Price", oldCap); break; }
                            }
                        }
                        catch { }
                    });
                    ok++;
                }

            if (hasMul)
                foreach (var kv in ev.HandbookPriceMultiplier)
                {
                    if (!WritePrice(kv.Key, 0, out var cur) || cur <= 0) { miss++; missList.Add(kv.Key); continue; }
                    double want = Math.Round(cur * kv.Value);
                    if (Math.Abs(want - cur) < 0.0001) continue;
                    WritePrice(kv.Key, want, out _);
                    var tplCap = kv.Key; var oldCap = cur;
                    AddRestore?.Invoke(() =>
                    {
                        try
                        {
                            var handbook = Prop(TemplateTable, "Handbook");
                            var items = Prop(handbook, "Items") as IEnumerable;
                            if (items == null) return;
                            foreach (var hi in items)
                            {
                                if (hi == null) continue;
                                if (string.Equals((Prop(hi, "Id") ?? "").ToString(), tplCap, StringComparison.OrdinalIgnoreCase))
                                { SetProp(hi, "Price", oldCap); break; }
                            }
                        }
                        catch { }
                    });
                    ok++;
                }

            Info($"手册价改写：成功 {ok} 项"
                 + (miss > 0 ? $"，{miss} 项未在 handbook 里找到（{string.Join("、", missList)}）" : "")
                 + "（同时写入 TemplateTable.Handbook 与 HandbookHelper 运行期缓存，可还原）");
        }
        catch (Exception ex) { logger?.Warning($"[EventRotator][价格] 失败: {ex.Message}"); }
    }
}
