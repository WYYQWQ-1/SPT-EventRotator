using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★ 天气季节维度（v2.6.0）—— 用户要的"春夏秋冬"。
///
/// ⚠️ 关键区分（之前搞混了）：
///   · **天气季节**（春夏秋冬 / 下雪 / 落叶）→ `WeatherConfig.OverrideSeason` : Nullable&lt;Season&gt;
///       Season 枚举：SUMMER / AUTUMN / WINTER / SPRING（+ AUTUMN_LATE / SPRING_EARLY / STORM）
///       配置落点：`configs/weather.json → "overrideSeason"`（默认 null）
///       实证：`SeasonalEventService.GetActiveWeatherSeason()` 就是读 `WeatherConfig.OverrideSeason`
///   · **活动季节**（万圣节/圣诞/新年/愚人节）→ `SeasonalEventType` + `SeasonalEventService.ForceSeasonalEvent`
///       这是**活动系统**，跟天气无关（由活动自带的 seasonalEvent 字段驱动）
///
/// 本维度只管**天气季节**。活动季节仍由活动的 `seasonalEvent` 字段驱动。
/// </summary>
internal static class WeatherSeasonDimension
{
    public static object WeatherConfig;
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    /// <summary>轮换顺序（用户要的 4 个）。</summary>
    public static readonly List<string> Order = new() { "SPRING", "SUMMER", "AUTUMN", "WINTER" };

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][天气季节] " + m); } catch { } }

    private static object Prop(object o, string n)
    {
        if (o == null) return null;
        var t = o.GetType();
        var p = t.GetProperty(n);
        if (p != null) return p.GetValue(o);
        var f = t.GetField(n);
        if (f != null) return f.GetValue(o);
        return null;
    }

    /// <summary>把字符串解析成 SPT 的 Season 枚举值（找不到返回 null）。</summary>
    public static object ParseSeason(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            var t = Type.GetType("SPTarkov.Server.Core.Models.Enums.Season, SPTarkov.Server.Core")
                 ?? AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("SPTarkov.Server.Core.Models.Enums.Season"))
                        .FirstOrDefault(x => x != null);
            if (t == null) { Info("找不到 Season 枚举类型"); return null; }
            return Enum.Parse(t, name.Trim().ToUpperInvariant(), true);
        }
        catch (Exception ex) { Info($"解析季节 '{name}' 失败: {ex.Message}"); return null; }
    }

    /// <summary>当前档位对应的季节（按天数轮换）。</summary>
    public static string CurrentRotated(int slot)
    {
        // ★ v1.6.0【S6 修】以前这里用**硬编码**的 SPRING/SUMMER/AUTUMN/WINTER 自己算一遍，
        //   而活动季节用的是可配置的 SeasonRotation.Order ⇒ 用户改 order 后
        //   "天气=某季、活动季节=另一季"（两套真值源）。
        //   现在统一走 SeasonRotation 这一个函数。
        try
        {
            var s = SeasonRotation.CurrentSeason();
            if (!string.IsNullOrEmpty(s)) return s;
        }
        catch { }
        if (Order.Count == 0) return null;
        var i = ((slot % Order.Count) + Order.Count) % Order.Count;
        return Order[i];
    }

    /// <summary>套用天气季节。season = null 表示还原成"不覆盖"。</summary>
    /// <summary>
    /// ★ v0.1.5 立即把天气季节写成指定值（幂等；供"进图重刷"用）。
    /// 与 SPT 好友命令 `itsonlysnowalan`（ForceSnowMessageHandler → `weatherConfig.OverrideSeason = Season.WINTER`）同一开关。
    /// </summary>
    public static void ForceNow(string season, string reason)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(season)) return;
            var prop = WeatherConfig?.GetType().GetProperty("OverrideSeason");
            if (prop == null || !prop.CanWrite) return;
            var pt = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (!pt.IsEnum) return;
            var val = Enum.Parse(pt, season, true);
            var cur = prop.GetValue(WeatherConfig);
            if (cur != null && string.Equals(cur.ToString(), season, StringComparison.OrdinalIgnoreCase)) return;   // 已是目标值 ⇒ 不重复写
            prop.SetValue(WeatherConfig, val);
            Log?.Invoke($"[EventRotator][天气季节] ★进图重刷 → {season.ToUpperInvariant()}（{reason}）");
        }
        catch (Exception ex) { Log?.Invoke($"[EventRotator][天气季节] 进图重刷失败: {ex.Message}"); }
    }

    public static void Apply(string seasonName, string why, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            if (WeatherConfig == null) { logger?.Warning("[EventRotator][天气季节] 拿不到 WeatherConfig"); return; }
            var prop = WeatherConfig.GetType().GetProperty("OverrideSeason");
            if (prop == null || !prop.CanWrite) { logger?.Warning("[EventRotator][天气季节] OverrideSeason 不可写"); return; }

            object old = prop.GetValue(WeatherConfig);
            var cap = WeatherConfig; var capOld = old;
            AddRestore?.Invoke(() => { try { prop.SetValue(cap, capOld); } catch { } });

            if (string.IsNullOrWhiteSpace(seasonName))
            {
                prop.SetValue(WeatherConfig, null);
                Info($"天气季节 → 不覆盖（还原默认）（{why}，可还原）");
                return;
            }
            var val = ParseSeason(seasonName);
            if (val == null) { Info($"跳过：无法解析季节 '{seasonName}'"); return; }
            prop.SetValue(WeatherConfig, val);
            Info($"★天气季节 → {seasonName}（{why}，原值={old ?? "null"}，可还原）");
        }
        catch (Exception ex) { logger?.Error($"[EventRotator][天气季节] 失败: {ex.Message}"); }
    }
}
