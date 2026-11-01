using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★★★ v0.1.5 ② 首领时刻表（官方 #79「Bosses &amp; Rogues Wander」）。
///
/// 官方原文（文本表格，非图片）：
///   MSK 8:00–12:00 → Reserve ｜ 12:00–13:00 → 无 ｜ 13:00–17:00 → Customs ｜ 17:00–21:00 → Shoreline
///   21:00–22:00 → 无 ｜ 22:00–2:00 → Interchange ｜ 2:00–8:00 → Woods
/// 「哪张图有谁」= 紧跟其后的 Boss locations：五张图同一组（Glukhar / Killa+Tagilla / Sanitar / Shturman / Reshala）。
/// 本维度按**系统时钟**换算到官方时区，取当前行，把该行地图加上那组首领（可还原、进图幂等）。
/// </summary>
internal static class TimetableDimension
{
    public static ISptLogger<EventRotatorServer> Log;

    /// <summary>时区偏移（相对 UTC 小时）。官方表头六列，取 MSK 为基准即可覆盖。</summary>
    private static readonly Dictionary<string, int> TzOffset = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MSK"] = 3, ["UTC"] = 0, ["EDT"] = -4, ["CEST"] = 2, ["KST"] = 9, ["BST"] = 1,
    };

    /// <summary>取当前时区小时（0-23）。</summary>
    public static int CurrentHour(string tz)
    {
        var off = TzOffset.TryGetValue(tz ?? "MSK", out var o) ? o : 3;
        return (int)((DateTime.UtcNow.AddHours(off)).Hour);
    }

    /// <summary>当前时段对应的地图 key（无则 null）。跨零点行用 from>to 表达。</summary>
    public static string CurrentMap(BossTimetableDef tt, out int hour)
    {
        hour = CurrentHour(tt?.Tz ?? "MSK");
        return MapAt(tt, hour);
    }

    /// <summary>
    /// ★★★ v2.6.4【纯函数】给定时区小时取该时段地图（无则 null）。
    /// 把"查表"从"读时钟"里拆出来 ⇒ 自检可以**逐小时全表验证** 8 行班表
    /// （含 12-13 / 21-22 两个官方「无」时段与所有整点边界），
    /// 不必等到那些钟点、也不必反射改时钟。
    /// 语义与 <see cref="CurrentMap"/> 完全一致（后者就是本函数 + 当前小时）。
    /// </summary>
    public static string MapAt(BossTimetableDef tt, int hour)
    {
        if (tt?.Rows == null) return null;
        foreach (var r in tt.Rows)
        {
            if (r == null || string.IsNullOrEmpty(r.Map)) continue;
            // 跨零点行：from > to（如 22→2）用"或"判定
            bool hit = r.From <= r.To ? (hour >= r.From && hour < r.To) : (hour >= r.From || hour < r.To);
            if (hit) return r.Map;
        }
        return null;
    }
}
