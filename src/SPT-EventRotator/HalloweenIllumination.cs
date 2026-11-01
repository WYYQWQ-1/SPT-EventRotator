using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace EventRotator;

/// <summary>
/// ★ 2026-10-30【藏身处万圣节照明】独立维度（与僵尸无关）。
///
/// <para><b>现象</b>：官方万圣节的"藏身处装饰 + 光照"只由客户端按
/// `globals.config.EventType` 里的 <c>Halloween</c> / <c>HalloweenIllumination</c> 显示
/// （`SPT_Data/database/hideout` 里没有任何 Halloween 数据）；原来这段 EventType 补写只写在
/// <see cref="SeasonalZombieDimension"/> 的"原生僵尸"路径里 ⇒ `halloween_2022` /
/// `halloween_2023` / `halloween_nozombie` 这类**不带僵尸**的万圣节活动都不亮。</para>
///
/// <para><b>本类</b>：任何"万圣节家族"活动生效时，把 <c>globals.config.EventType</c> 补成
/// 含 `Halloween` + `HalloweenIllumination`（去掉 `None`）；快照原列表、活动关闭逐字还原；幂等。
/// 是 EventType 的**唯一写入口**（僵尸维度已改为调用本类，避免重复快照/重复还原）。</para>
/// </summary>
internal static class HalloweenIllumination
{
    private const string Tag = "[万圣节]";

    public static object GlobalTable;
    public static Action<string> Info;
    public static Action<string> Warn;
    public static Action<Action> AddRestore;

    private static readonly object _gate = new();
    private static IList _list;
    private static List<object> _orig;
    private static bool _applied;

    /// <summary>
    /// 是否"万圣节家族"活动。
    /// 真实数据（本机 events.json）：`halloween` / `halloween_2022` / `halloween_2023` /
    /// `halloween_nozombie` 的 <c>seasonalEvent</c> 全部是 <c>"Halloween"</c>；
    /// 判定 = `SeasonalEvent` 含 "Halloween"（大小写不敏感）**或** id 以 `halloween` 开头（双保险）。
    /// </summary>
    public static bool IsHalloweenFamily(EventDef ev)
    {
        if (ev == null) return false;
        try
        {
            if (!string.IsNullOrEmpty(ev.SeasonalEvent)
                && ev.SeasonalEvent.IndexOf("Halloween", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrEmpty(ev.Id)
                && ev.Id.StartsWith("halloween", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>主套用循环逐活动调用（万圣节家族才动作）。</summary>
    public static void Apply(EventDef ev)
    {
        if (!IsHalloweenFamily(ev)) return;
        try
        {
            lock (_gate) { if (_applied) return; }

            var cfgObj = Prop(GlobalTable, "Configuration");
            var etProp = cfgObj?.GetType().GetProperty("EventType",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var list = etProp?.GetValue(cfgObj) as IList;
            if (list == null)
            {
                Warn?.Invoke($"{Tag} ⚠ 拿不到 globals.config.EventType ⇒ 藏身处万圣节照明可能不生效");
                return;
            }

            var etType = list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : null;
            if (etType == null || !etType.IsEnum)
                etType = RuntimeTypes.Find("SPTarkov.Server.Core.Models.Enums.EventType");
            if (etType == null || !etType.IsEnum)
            {
                Warn?.Invoke($"{Tag} ⚠ 找不到 EventType 枚举 ⇒ 跳过藏身处照明");
                return;
            }

            object E(string n) => Enum.Parse(etType, n, true);
            var none = E("None"); var hw = E("Halloween"); var ill = E("HalloweenIllumination");
            bool has(object v) { foreach (var x in list) if (Equals(x, v)) return true; return false; }

            var before = list.Cast<object>().ToList();
            int changed = 0;
            if (has(none)) { list.Remove(none); changed++; }
            if (!has(ill)) { list.Add(ill); changed++; }
            if (!has(hw)) { list.Add(hw); changed++; }

            lock (_gate)
            {
                _list = list;
                _orig = before;
                _applied = true;
            }

            if (changed > 0)
            {
                Info?.Invoke($"{Tag} 已启用藏身处万圣节照明（globals.config.EventType += HalloweenIllumination，"
                           + $"原 [{string.Join(", ", before)}]）");
                AddRestore?.Invoke(() => Restore());
            }
            else
            {
                Info?.Invoke($"{Tag} 藏身处万圣节照明已开启（EventType=[{string.Join(", ", list.Cast<object>())}]，幂等跳过）");
            }
        }
        catch (Exception ex)
        {
            Warn?.Invoke($"{Tag} ⚠ 开启藏身处万圣节照明失败（已忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>活动关闭：把 EventType 逐项写回原列表。</summary>
    public static void Restore()
    {
        IList list; List<object> orig;
        lock (_gate)
        {
            if (!_applied || _list == null || _orig == null) { _applied = false; return; }
            list = _list; orig = _orig;
            _applied = false; _list = null; _orig = null;
        }
        try
        {
            list.Clear();
            foreach (var v in orig) list.Add(v);
            Info?.Invoke($"{Tag} 已还原 globals.config.EventType（{string.Join(", ", orig)}）");
        }
        catch (Exception ex)
        {
            Warn?.Invoke($"{Tag} ⚠ 还原 EventType 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>清幂等标记（还原动作由 `_restoreActions` 负责）。</summary>
    public static void ResetApplied()
    {
        lock (_gate) _applied = false;
    }

    /// <summary>客户端请求 globals 时确保 EventType 仍含 Halloween（被别处重置时补回，幂等）。</summary>
    public static void EnsureStillSet()
    {
        try
        {
            IList list;
            lock (_gate)
            {
                if (!_applied) return;
                list = _list;
            }
            if (list == null) return;
            var etType = list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : null;
            if (etType == null || !etType.IsEnum) return;
            var hw = Enum.Parse(etType, "Halloween", true);
            var ill = Enum.Parse(etType, "HalloweenIllumination", true);
            bool has(object v) { foreach (var x in list) if (Equals(x, v)) return true; return false; }
            if (!has(hw)) list.Add(hw);
            if (!has(ill)) list.Add(ill);
        }
        catch { }
    }

    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        try
        {
            return o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                    ?.GetValue(o);
        }
        catch { return null; }
    }
}
