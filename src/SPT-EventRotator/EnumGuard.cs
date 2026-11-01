using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// 【安全网】往地图数据里写"枚举字符串"之前，先确认这个值在 SPT 的枚举里合法。
///
/// 血的教训：我把 hostility 的 *PlayerBehaviour 写成 "Enemies"，
/// 而客户端枚举 EFT.EWarnBehaviour 的合法值是
///   Default / Neutral / Warn / AlwaysEnemies / AlwaysFriends / ChancedEnemies
/// → 客户端反序列化整张地图数据失败 → 直接弹错误屏进不了图：
///   `Error converting value "Enemies" to type 'EWarnBehaviour'`
///
/// 这里用 SPT 服务端同名的枚举（序列化成同一个字符串）做校验；值不合法就**不写**并告警。
/// </summary>
public static class EnumGuard
{
    private static ISptLogger<EventRotatorServer> _log;
    private static readonly Dictionary<string, HashSet<string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static void Init(ISptLogger<EventRotatorServer> log) => _log = log;

    /// <summary>取 SPT 里某枚举类型的全部合法名字（找不到返回 null）。</summary>
    private static HashSet<string> Names(string enumTypeName)
    {
        if (_cache.TryGetValue(enumTypeName, out var c)) return c;
        HashSet<string> names = null;
        try
        {
            var t = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(x => x != null && x.IsEnum && x.Name == enumTypeName);
            if (t != null) names = new HashSet<string>(Enum.GetNames(t), StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        _cache[enumTypeName] = names;
        return names;
    }

    /// <summary>校验一个值是否是该枚举的合法成员；不合法返回 false 并告警（调用方就不要写）。</summary>
    public static bool IsValid(string enumTypeName, string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var names = Names(enumTypeName);
        if (names == null) return true;                 // 找不到类型 → 不阻止（保守放行）
        if (names.Contains(value)) return true;
        _log?.Error($"[EventRotator] ⚠ 拒绝写入非法枚举值 {enumTypeName}=\"{value}\"（合法值：{string.Join("/", names)}）—— 否则客户端会进不了图！");
        return false;
    }
}
