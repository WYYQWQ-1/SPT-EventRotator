using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★ task-34 ⑧-B：**按 (地图 + tpl) 调松散战利品权重/概率**。
/// 用途：官方 `softcore` 第 5 条「实验室彩卡刷新率」——
///   `locations/laboratory/looseLoot.json` 里黄卡 / 紫卡各自有**独立刷点**，
///   本体概率极低（黄卡 7.86e-4 / 1.43e-4 / …；紫卡 4.29e-4 / 5.00e-4 / …）。
///
/// ── 落点（已实证）────────────────────────────────────────────────────────────
///   `LocationTable` → 每个 `Location.LooseLoot : LazyLoad&lt;LooseLoot&gt;`（取 `.Value` 强制加载）
///     → `LooseLoot.Spawnpoints : List&lt;Spawnpoint&gt;`
///       · `Spawnpoint.Probability : double?`         ← **刷点整体出现概率**
///       · `Spawnpoint.Template.Id : string`          ← 模板名（如 `loot_alarm_pult (4) [uuid]`）
///       · `Spawnpoint.Template.Items[]._tpl : MongoId` ← 该刷点里装的物品
///       · `Spawnpoint.ItemDistribution[].RelativeProbability : double?` ← 多物品时的相对权重
///   本 mod 已有同类落点实证：`ExtraDimensions.ApplyChristmasLootBoost`（走 `GetDictionary()` +
///   `LooseLoot.Value.Spawnpoints` 改 `Probability`）——本维度复用同一套读法，保证一致。
///
/// ── 语义（很重要，别改错）────────────────────────────────────────────────────
///   · `probability` 是"**这个刷点整体会不会生成**"；彩卡的刷点里**只装那一张卡**
///     （`Items` 只有 1 个元素、`itemDistribution` 只有 1 条）⇒ **改 `probability` 就是改这张卡的刷新率**。
///   · 当某刷点 `itemDistribution` 有**多条**时（多物品共享刷点），单改 `probability` 不等于改单物品权重，
///     所以提供 `WeightMult`：**只对该 tpl 对应的那几条 `relativeProbability` 按倍率缩放**。
///   · 两种模式都给：`ProbabilityMult`（改刷点概率）+ `WeightMult`（改该 tpl 的相对权重）。
///     只写哪个就只改哪个；两个都写就都改（互不影响）。
///
/// ── 幂等 / 还原 / 降级 ────────────────────────────────────────────────────────
///   · 幂等：`probability` 是**赋值**（非追加）⇒ 天然幂等；`relativeProbability` 是倍率缩放，
///     重复套用会**累积**，故按 `(map|tpl|field)` 记 `_applied` 集合，同一组只缩放一次。
///   · 还原：每个被改字段登记 `SetProp(old)` 闭包进 `_restoreActions`（Plugin 侧逆序 LIFO）。
///   · 降级：地图/刷点/字段找不到 ⇒ warning + continue，绝不抛。
///   · 自证：改完**回读**并打印新值。
/// </summary>
internal static class LooseLootWeightDimension
{
    public static object LocationTable;
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    /// <summary>已缩放过的 (map|tpl|field)，防倍率重复累积（幂等）。</summary>
    private static readonly HashSet<string> _applied = new(StringComparer.OrdinalIgnoreCase);

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][战利品权重] " + m); } catch { } }
    private static void Warn(string m) { try { Log?.Invoke("[EventRotator][战利品权重] ⚠ " + m); } catch { } }

    /// <summary>供 Plugin 在"还原+套用"开头调用：清空幂等表（还原后可以重新套用）。</summary>
    public static void ResetApplied() { _applied.Clear(); }

    /// <summary>
    /// 应用 `looseLootWeightSet`。
    /// 写法：<c>{ "laboratory": { "5c1d0d6d86f7744bb2683e1f": { "probabilityMult": 60, "weightMult": 4 } } }</c>
    /// </summary>
    public static void Apply(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        if (ev?.LooseLootWeightSet == null || ev.LooseLootWeightSet.Count == 0) return;
        if (LocationTable == null) { Warn("拿不到 LocationTable，本维度跳过"); return; }
        try
        {
            var dict = R.LocationDict(LocationTable);
            if (dict == null) { Warn("LocationTable 取不到地图字典，本维度跳过"); return; }

            int totalHit = 0, totalMiss = 0;
            foreach (var mapKv in ev.LooseLootWeightSet)
            {
                var loc = FindLocation(dict, mapKv.Key);
                if (loc == null) { Warn($"找不到地图 '{mapKv.Key}'，跳过"); totalMiss++; continue; }

                var loose = R.Prop(loc, "LooseLoot");
                if (loose == null) { Warn($"地图 '{mapKv.Key}' 没有 LooseLoot，跳过"); totalMiss++; continue; }
                var value = R.Prop(loose, "Value") ?? loose;      // LazyLoad<T> 强制加载
                var spawns = R.Prop(value, "Spawnpoints") as IEnumerable;
                if (spawns == null) { Warn($"地图 '{mapKv.Key}' 的 LooseLoot 没有 Spawnpoints，跳过"); totalMiss++; continue; }

                foreach (var tplKv in mapKv.Value)
                {
                    var tpl = tplKv.Key;
                    var opt = tplKv.Value;
                    if (opt == null || string.IsNullOrWhiteSpace(tpl)) continue;

                    int hit = 0;
                    foreach (var sp in spawns)
                    {
                        if (sp == null) continue;
                        // 该刷点里有没有这个 tpl？走 Template.Items[]._tpl（JSON 名 `_tpl`）
                        int idx = FindTplIndex(sp, tpl);
                        if (idx < 0) continue;
                        hit++;

                        // ① 刷点整体概率
                        if (opt.ProbabilityMult.HasValue && Math.Abs(opt.ProbabilityMult.Value - 1.0) > 1e-9)
                        {
                            Scale(sp, "Probability", opt.ProbabilityMult.Value,
                                  $"{mapKv.Key}|{tpl}|probability", cap => cap);
                        }

                        // ② 该 tpl 在 itemDistribution 里的相对权重
                        if (opt.WeightMult.HasValue && Math.Abs(opt.WeightMult.Value - 1.0) > 1e-9)
                        {
                            var dist = R.Prop(sp, "ItemDistribution") as IList;
                            if (dist != null)
                            {
                                foreach (var de in dist)
                                {
                                    if (de == null) continue;
                                    // composedKey.key 与 Template.Items[idx].composedKey 对应
                                    if (!DistEntryMatches(de, sp, idx)) continue;
                                    Scale(de, "RelativeProbability", opt.WeightMult.Value,
                                          $"{mapKv.Key}|{tpl}|weight", cap => cap);
                                }
                            }
                        }
                    }

                    if (hit == 0) { Warn($"地图 '{mapKv.Key}' 的 looseLoot 里找不到 tpl {tpl}（该刷点可能不在 looseLoot，而在 staticLoot）"); totalMiss++; }
                    else
                    {
                        totalHit += hit;
                        var cur = FirstProbability(spawns, tpl);
                        Info($"★{mapKv.Key} 的 {tpl}：命中 {hit} 个刷点"
                           + (opt.ProbabilityMult.HasValue ? $"，probability ×{opt.ProbabilityMult.Value}" : "")
                           + (opt.WeightMult.HasValue ? $"，relativeProbability ×{opt.WeightMult.Value}" : "")
                           + (cur.HasValue ? $"（回读 probability 现 = {cur.Value:R} ✔）" : "")
                           + "（可还原）");
                    }
                }
            }
            Info($"★松散战利品权重：命中 {totalHit} 处、未找到 {totalMiss} 处（落点 Spawnpoint.Probability / ItemDistribution[].RelativeProbability）");
        }
        catch (Exception ex) { Warn("失败（已降级，不影响服务端）：" + ex.Message); }
    }

    /// <summary>在 spawnpoint 的 Template.Items 里找 tpl，返回下标（找不到 -1）。</summary>
    private static int FindTplIndex(object sp, string tpl)
    {
        try
        {
            var tplObj = R.Prop(sp, "Template");
            var items = R.Prop(tplObj, "Items") as IList;
            if (items == null) return -1;
            for (int i = 0; i < items.Count; i++)
            {
                var t = R.Prop(items[i], "Template")?.ToString() ?? R.Prop(items[i], "_tpl")?.ToString();
                if (string.Equals(t, tpl, StringComparison.OrdinalIgnoreCase)) return i;
            }
        }
        catch { }
        return -1;
    }

    /// <summary>composedKey 比对：优先用 key 字符串与目标 item 的 composedKey 相等；回落到 index。</summary>
    private static bool DistEntryMatches(object de, object sp, int idx)
    {
        try
        {
            var ck = R.Prop(de, "ComposedKey") ?? R.Prop(de, "composedKey");
            var key = R.Prop(ck, "Key") ?? R.Prop(ck, "key");
            var tplObj = R.Prop(sp, "Template");
            var items = R.Prop(tplObj, "Items") as IList;
            if (key != null && items != null && idx >= 0 && idx < items.Count)
            {
                var ick = R.Prop(items[idx], "ComposedKey") ?? R.Prop(items[idx], "composedKey");
                var ikey = R.Prop(ick, "Key") ?? R.Prop(ick, "key");
                if (ikey != null) return string.Equals(key.ToString(), ikey.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            // 单物品刷点：唯一一条 distribution 一定对应它
            var dist = R.Prop(sp, "ItemDistribution") as IList;
            return dist != null && dist.Count == 1;
        }
        catch { return false; }
    }

    /// <summary>按倍率缩放一个数值字段（幂等：同一 key 只缩放一次），并登记还原。</summary>
    private static void Scale(object obj, string field, double mult, string idemKey, Func<object, object> _)
    {
        try
        {
            if (_applied.Contains(idemKey)) return;
            var cur = R.Prop(obj, field);
            if (cur == null) return;
            double old;
            try { old = Convert.ToDouble(cur); } catch { return; }
            double nv = old * mult;
            // probability 上限 1.0（它是概率，不能 >1）
            if (string.Equals(field, "Probability", StringComparison.OrdinalIgnoreCase)) nv = Math.Min(1.0, nv);
            if (nv < 0) nv = 0;

            var capObj = obj;
            AddRestore?.Invoke(() => { try { R.SetProp(capObj, field, old); } catch { } });
            if (R.SetProp(obj, field, nv)) _applied.Add(idemKey);
        }
        catch { }
    }

    private static double? FirstProbability(IEnumerable spawns, string tpl)
    {
        try
        {
            foreach (var sp in spawns)
            {
                if (sp == null) continue;
                if (FindTplIndex(sp, tpl) < 0) continue;
                var p = R.Prop(sp, "Probability");
                if (p != null) return Convert.ToDouble(p);
            }
        }
        catch { }
        return null;
    }

    private static object FindLocation(IDictionary dict, string mapKey)
    {
        try
        {
            // 精确 / 不敏感
            foreach (var k in dict.Keys)
                if (string.Equals(k?.ToString(), mapKey, StringComparison.OrdinalIgnoreCase)) return dict[k];
        }
        catch { }
        return null;
    }
}
