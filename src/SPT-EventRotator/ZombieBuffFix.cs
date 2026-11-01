using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace EventRotator;

/// <summary>
/// ★ 2026-10-30【僵尸咬人反而回血】实机定位后的修复：
/// `SPT_Data/database/globals.json → config.Health.Effects.Stimulator.Buffs.BuffsZombieInfection` 里
/// 除了"被感染"标记 `<c>BuffType=ZombieInfection</c>` 之外，官方还塞了三条**增益**：
/// <code>
/// SkillRate  (SkillName=Vitality, Value=20)   ← 活力 +20
/// HealthRate (Value=50)                       ← 生命恢复 +50（叠加后盖过僵尸伤害）
/// EnergyRate (Value=50)                       ← 能量恢复 +50
/// </code>
/// 每次被咬 Chance=1 ⇒ 必中且可叠加 ⇒ "僵尸咬不掉血"。
///
/// <para><b>本类只做一件事</b>：僵尸活动生效期间，把 `BuffsZombieInfection` 里这三类**增益**移除，
/// 保留 `ZombieInfection` 标记；其它 buff（`config.Health.Effects.ZombieInfection` 的 Dehydration /
/// HearingDebuff 等）**一个都不动**。快照原表、活动关闭逐字还原；幂等；失败只 warn。</para>
/// </summary>
internal static class ZombieBuffFix
{
    private const string Tag = "[感染者]";
    private const string DictKey = "BuffsZombieInfection";

    /// <summary>要移除的增益 BuffType（保留 ZombieInfection 标记）。</summary>
    private static readonly HashSet<string> RemoveTypes = new(StringComparer.OrdinalIgnoreCase)
    { "SkillRate", "HealthRate", "EnergyRate" };

    public static Action<string> Info;
    public static Action<string> Warn;

    private static readonly object _gate = new();
    private static IList _list;                 // 指向原表里的 List<Buff>（原地改）
    private static List<object> _orig;          // 原表逐项快照（对象引用）
    private static bool _applied;

    /// <summary>活动套用时调用（仅僵尸活动生效路径会走到这里）。</summary>
    public static void Apply(object globalTable, Action<Action> addRestore)
    {
        lock (_gate) { if (_applied) return; }
        try
        {
            var health = Prop(Prop(Prop(Prop(globalTable, "Configuration"), "Health"), "Effects"), "Stimulator");
            var dict = Prop(health, "Buffs") as IDictionary;
            if (dict == null) { Warn?.Invoke($"{Tag} ⚠ 拿不到 config.Health.Effects.Stimulator.Buffs ⇒ 跳过僵尸增益 buff 修复"); return; }

            var key = FindKey(dict, DictKey);
            if (key == null) { Warn?.Invoke($"{Tag} ⚠ Buffs 里找不到 {DictKey} ⇒ 跳过"); return; }

            var list = dict[key] as IList;
            if (list == null)
            {
                // IEnumerable<Buff> 但非 IList：物化成 List<Buff> 再写回（元素类型 = 原实例类型）
                var cur = dict[key] as IEnumerable;
                var elem = cur?.Cast<object>().FirstOrDefault()?.GetType();
                if (elem == null) { Warn?.Invoke($"{Tag} ⚠ {DictKey} 为空/不可物化 ⇒ 跳过"); return; }
                var made = R.NewListOf(elem) as IList;
                if (made == null) { Warn?.Invoke($"{Tag} ⚠ {DictKey} 造 List<{elem.Name}> 失败 ⇒ 跳过"); return; }
                foreach (var o in cur) made.Add(o);
                dict[key] = made;
                list = made;
            }

            var snapshot = list.Cast<object>().ToList();
            var removed = new List<string>();
            var kept = new List<object>();
            foreach (var b in snapshot)
            {
                if (b == null) continue;
                var bt = Prop(b, "BuffType") as string ?? "";
                if (RemoveTypes.Contains(bt))
                {
                    var sk = Prop(b, "SkillName") as string;
                    double v = Convert.ToDouble(Prop(b, "Value") ?? 0d, CultureInfo.InvariantCulture);
                    var label = string.Equals(bt, "SkillRate", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(sk) ? sk : bt;
                    removed.Add($"{label}{(v >= 0 ? "+" : "")}{Fmt(v)}");
                    continue;
                }
                kept.Add(b);
            }

            if (removed.Count == 0)
            {
                lock (_gate) { _list = list; _orig = snapshot; _applied = true; }
                Info?.Invoke($"{Tag} 僵尸咬人的增益 buff 已不存在（{DictKey} 共 {list.Count} 条，无需改动）");
                addRestore?.Invoke(() => Restore());
                return;
            }

            lock (_gate) { _list = list; _orig = snapshot; _applied = true; }
            try
            {
                list.Clear();
                foreach (var b in kept) list.Add(b);
            }
            catch (Exception ex)
            {
                lock (_gate) { _applied = false; _list = null; _orig = null; }
                Warn?.Invoke($"{Tag} ⚠ 移除僵尸增益 buff 写表失败（已回滚）：{ex.Message}");
                return;
            }

            Info?.Invoke($"{Tag} 已禁用僵尸咬人的增益 buff {removed.Count} 条（{string.Join(" / ", removed)}），保留 ZombieInfection 标记");
            addRestore?.Invoke(() => Restore());
        }
        catch (Exception ex)
        {
            Warn?.Invoke($"{Tag} ⚠ 僵尸增益 buff 修复失败（已忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>活动关闭：把原表逐字写回（对象引用原样）。</summary>
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
            foreach (var b in orig) list.Add(b);
            Info?.Invoke($"{Tag} 已还原僵尸咬人的增益 buff（{DictKey} 逐字回原表 {orig.Count} 条）");
        }
        catch (Exception ex)
        {
            Warn?.Invoke($"{Tag} ⚠ 还原僵尸增益 buff 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>清幂等标记（还原动作由 `_restoreActions` 负责）。</summary>
    public static void ResetApplied()
    {
        lock (_gate) { _applied = false; }
    }

    // ── 反射小工具 ──────────────────────────────────────────────────────────

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

    private static object FindKey(IDictionary d, string key)
    {
        try
        {
            foreach (var k in d.Keys)
                if (string.Equals(k?.ToString(), key, StringComparison.OrdinalIgnoreCase)) return k;
        }
        catch { }
        return null;
    }

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
