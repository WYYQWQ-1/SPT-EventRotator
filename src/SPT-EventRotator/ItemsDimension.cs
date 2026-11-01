using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★ v0.1.3 items 维度（物品改动）—— 补上"缺口 1"（原本 4 个活动完全没有生效手段）：
///   · <c>shotgun_shells</c> 霰弹活动 → 霰弹弹药伤害/穿甲
///   · <c>melee_event</c> 近战活动   → 近战武器挥砍/突刺伤害
///   · <c>stim_event</c> 针剂活动    → 针剂 Buff 数值
///   · <c>obdolbos_roulette</c> 奥布多洛斯轮盘 → Obdolbos 针剂 Buff
///
/// 落点（实机数据库实证，`SPT_Data/database/templates/items.json`，4673 个物品）：
///   · 弹药：<c>_props.Damage</c> / <c>PenetrationPower</c> / <c>ArmorDamage</c>
///     例：<c>560d5e524bdc2d25448b4571 patron_12x70_buckshot</c> Damage=39
///     （离线干跑实测：12/70、20/70 霰弹共 33 条、近战武器 24 把命中，见 tools/dryrun）
///   · 近战：<c>_props.knifeHitSlashDam</c> / <c>_props.knifeHitStabDam</c>
///     例：<c>5bffe7930db834001b734a39 weapon_crash_axe</c> 31 / 52
///   · 针剂数值在 <c>globals.json → config.Health.Effects.Stimulator.Buffs.&lt;名&gt;[].Value</c>
///     例：<c>Buffs_Obdolbos[].Value = 10</c>
///
/// events.json 用法（三类选择器）：
/// <code>
/// "items": {
///   "shotgunAmmo":        { "damageMultiplier": 2.0, "penetrationMultiplier": 1.5 },
///   "melee":              { "slashMultiplier": 2.0, "stabMultiplier": 2.0 },
///   "stim:Buffs_Obdolbos":{ "valueMultiplier": 2.0, "durationMultiplier": 1.5 }
/// }
/// </code>
/// 全部可还原（Restore 时恢复原值）。
/// </summary>
internal static class ItemsDimension
{
    public static object TemplateTable;     // SPT TemplateTable（Items: Dictionary<MongoId, TemplateItem>）
    public static object GlobalTable;       // SPT GlobalTable（config.Health.Effects.Stimulator.Buffs）
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][物品] " + m); } catch { } }

    /// <summary>
    /// ★ 大小写不敏感取成员 —— **必须**：
    ///   JSON 里是 <c>knifeHitSlashDam</c>，而 SPT 的 C# 属性名是 <c>KnifeHitSlashDam</c>（PascalCase）。
    ///   用默认的 <c>GetProperty(name)</c> 会静默拿不到 → 近战维度看起来"跑了"其实一个都没改（实测踩过）。
    /// </summary>
    private const BindingFlags AnyMember = BindingFlags.Instance | BindingFlags.Public
                                         | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

    private static object Prop(object o, string n)
        => R.PropWithFlags(o, n, AnyMember);

    // ══════════ ★★★ v1.0.1【性能·第七轮】物品 props 成员缓存 ══════════
    //   本文件 9 处 `ScaleField`/`ScaleNested` 每次都要对**每个物品**做
    //       `Prop(it, "Properties") ?? Prop(it, "_props")`
    //   ⇒ 2 次字典查找 × 4300 物品 × 9 次 ≈ **39k 次/局**。
    //   物品类型在进程内只有少数几种 ⇒ 把两个成员按 Type 缓存，用 GetValue 直取。
    //   ⚠ 语义逐字保留：先取 `Properties` 的**值**，值为 null 才回落 `_props` 的**值**（不是成员回落）。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (PropertyInfo, FieldInfo, PropertyInfo, FieldInfo)> _propsMember
        = new();

    private static object PropsOf(object it)
    {
        if (it == null) return null;
        var t = it.GetType();
        if (!_propsMember.TryGetValue(t, out var m))
        {
            m = (R.GP(t, "Properties", AnyMember), R.GF(t, "Properties", AnyMember),
                 R.GP(t, "_props", AnyMember), R.GF(t, "_props", AnyMember));
            _propsMember[t] = m;
        }
        try { var v1 = m.Item1 != null ? m.Item1.GetValue(it) : m.Item2?.GetValue(it); if (v1 != null) return v1; } catch { }
        try { var v2 = m.Item3 != null ? m.Item3.GetValue(it) : m.Item4?.GetValue(it); if (v2 != null) return v2; } catch { }
        return null;
    }

    private static string ItemName(object templateItem) => (Prop(templateItem, "Name") ?? Prop(templateItem, "_name") ?? "").ToString();

    private static string ParentOf(object templateItem) => (Prop(templateItem, "Parent") ?? Prop(templateItem, "_parent") ?? "").ToString();

    /// <summary>每个物品的某个属性值。</summary>
    /// <summary>
    /// ★ v2.6.74【F2+】**以 `BuffType` 表达"负面"的类型**（它们 `Value = 0`，`Value<0` 判据选不中）。
    /// 本机 0.16.9 `globals…Stimulator.Buffs`（51 条）穷举：`HandsTremor` 13 条 · `QuantumTunnelling` 3 条
    /// ⇒ **SJ6 / Propital 的负面就是这两个**；其它负面类型（Contusion/Fracture/HeavyBleeding/…）出现在
    /// 别的 buff（伏特加/食物/低温）里 ⇒ 本活动只处理点名过的 buff，故不纳入；要用在此加一行即可。
    /// </summary>
    private static readonly HashSet<string> _negativeStimBuffTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "HandsTremor", "QuantumTunnelling"
    };

    /// <summary>★ v2.6.74【F2+】负面条目判据：`Value < 0` **或** 负面 `BuffType`。</summary>
    private static bool IsNegativeStimEntry(object one)
    {
        try
        {
            var v = GetNum(one, "Value");
            if (!double.IsNaN(v) && v < 0) return true;
            // ★ v2.6.76【独立复核 🟡-4】改用本文件的 Prop（含 NonPublic + 字段兜底，见 :53-60）
            var bt = Prop(one, "BuffType")?.ToString();
            return !string.IsNullOrEmpty(bt) && _negativeStimBuffTypes.Contains(bt);
        }
        catch { return false; }
    }

    private static double GetNum(object props, string name)
    {
        var v = Prop(props, name);
        try { return v == null ? double.NaN : Convert.ToDouble(v); } catch { return double.NaN; }
    }

    private static bool SetNum(object props, string name, double value, string why)
    {
        try
        {
            var t = props.GetType();
            var p = R.GP(t, name, AnyMember);   // ★ v1.0.1【性能】
            object old = null; Type target = null;
            if (p != null && p.CanWrite) { old = p.GetValue(props); target = p.PropertyType; }
            else
            {
                p = null;
                var f = R.GF(t, name, AnyMember);   // ★ v1.0.1【性能】
                if (f == null) { Info($"'{name}' 不可写，跳过"); return false; }
                old = f.GetValue(props); target = f.FieldType;
            }
            double oldV = 0; try { oldV = old == null ? 0 : Convert.ToDouble(old); } catch { }
            if (Math.Abs(oldV - value) < 0.000001) return false;

            var capO = props; var capName = name; var capOld = old;
            AddRestore?.Invoke(() =>
            {
                try
                {
                    var tt = capO.GetType();
                    var pp = R.GP(tt, capName, AnyMember);   // ★ v1.0.1【性能】
                    if (pp != null && pp.CanWrite) { pp.SetValue(capO, Convert.ChangeType(capOld, Nullable.GetUnderlyingType(pp.PropertyType) ?? pp.PropertyType)); return; }
                    var ff = R.GF(tt, capName, AnyMember);   // ★ v1.0.1【性能】
                    if (ff != null) ff.SetValue(capO, Convert.ChangeType(capOld, Nullable.GetUnderlyingType(ff.FieldType) ?? ff.FieldType));
                }
                catch { }
            });

            var u = Nullable.GetUnderlyingType(target) ?? target;
            if (p != null) p.SetValue(props, Convert.ChangeType(value, u));
            else t.GetField(name, AnyMember)?.SetValue(props, Convert.ChangeType(value, u));
            _ = why;
            return true;
        }
        catch (Exception ex) { Info($"改 {name} 失败: {ex.Message}"); return false; }
    }

    /// <summary>遍历全部物品，命中 predicate 的按 scale 调整 props 里的字段。</summary>
    private static int ScaleField(Func<object, object, bool> match, string field, double mult, out int scanned)
    {
        int n = 0; scanned = 0;
        var items = Prop(TemplateTable, "Items") as IDictionary;
        if (items == null) { Info("诊断：拿不到 TemplateTable.Items"); return 0; }
        foreach (DictionaryEntry e in items)
        {
            var it = e.Value; if (it == null) continue;
            var props = PropsOf(it);   // ★ v1.0.1【性能】成员按 Type 缓存
            if (props == null) continue;
            scanned++;
            try { if (!match(it, props)) continue; } catch { continue; }
            var old = GetNum(props, field);
            if (double.IsNaN(old) || Math.Abs(old) < 0.000001) continue;
            if (SetNum(props, field, old * mult, "items")) n++;
        }
        return n;
    }

    /// <summary>
    /// 嵌套两层缩放：`props.&lt;propName&gt;.&lt;key&gt;.Value *= mult`
    /// 用途：食物/饮料的 `_props.effects_health.Hydration.value`（C# 侧 `EffectsHealth → Hydration → Value`）。
    /// 字典形态与对象形态都兼容；找不到就返回 0（日志会显示"改了 0 个"，便于发现落点不对）。
    /// </summary>
    private static int ScaleNested(string propName, string key, double mult)
    {
        int n = 0;
        var items = Prop(TemplateTable, "Items") as IDictionary;
        if (items == null) return 0;
        foreach (DictionaryEntry e in items)
        {
            var props = PropsOf(e.Value);   // ★ v1.0.1【性能】成员按 Type 缓存
            if (props == null) continue;
            var bag = Prop(props, propName);
            if (bag == null) continue;

            object entry = null;
            if (bag is IDictionary d)
            {
                foreach (DictionaryEntry kv in d)
                    if (string.Equals(kv.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase)) { entry = kv.Value; break; }
            }
            if (entry == null)
            {
                var bt = bag.GetType();
                var pi = bt.GetProperty(key, AnyMember);
                if (pi != null) entry = pi.GetValue(bag);
                else { var fi = bt.GetField(key, AnyMember); if (fi != null) entry = fi.GetValue(bag); }
            }
            if (entry == null) continue;

            var old = GetNum(entry, "Value");
            if (double.IsNaN(old) || Math.Abs(old) < 0.000001) continue;   // 0 和负值不动（牛奶的 -65 会被放大成更大的负面）
            if (SetNum(entry, "Value", old * mult, "items")) n++;
        }
        return n;
    }

    public static void Apply(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            if (ev.Items == null || ev.Items.Count == 0) return;
            if (TemplateTable == null) { logger?.Warning("[EventRotator][物品] 拿不到 TemplateTable"); return; }

            foreach (var kv in ev.Items)
            {
                var sel = (kv.Key ?? "").Trim();
                var cfg = kv.Value;
                if (cfg == null || cfg.Count == 0) continue;

                double Get(string k)
                {
                    if (!cfg.TryGetValue(k, out var v)) return double.NaN;
                    return v <= 0 ? double.NaN : v;
                }

                // ── ⓪ 枪械：故障率 / 耐久消耗 / 过热（★ v0.1.5 新增，为 2022 万圣节「枪械故障率提升」）──
                //   落点 `_props.MalfunctionChance`（实测 198 把枪有此字段）/ `DurabilityBurnModificator` / `HeatFactor`
                if (sel.Equals("weapons", StringComparison.OrdinalIgnoreCase))
                {
                    int scanned;
                    var malf = Get("malfunctionMultiplier");
                    if (!double.IsNaN(malf))
                    {
                        var n = ScaleField((it, props) => !double.IsNaN(GetNum(props, "MalfunctionChance")),
                                           "MalfunctionChance", malf, out scanned);
                        Info($"枪械【故障率】×{malf}：改了 {n} 把（扫了 {scanned} 个物品，可还原）");
                    }
                    var burn = Get("durabilityBurnMultiplier");
                    if (!double.IsNaN(burn))
                    {
                        var n = ScaleField((it, props) => !double.IsNaN(GetNum(props, "DurabilityBurnModificator")),
                                           "DurabilityBurnModificator", burn, out scanned);
                        Info($"枪械【耐久消耗】×{burn}：改了 {n} 把（可还原）");
                    }
                    var heat = Get("heatFactorMultiplier");
                    if (!double.IsNaN(heat))
                    {
                        var n = ScaleField((it, props) => !double.IsNaN(GetNum(props, "HeatFactor")),
                                           "HeatFactor", heat, out scanned);
                        Info($"枪械【过热】×{heat}：改了 {n} 把（可还原）");
                    }
                    continue;   // ★ 只跳出本选择器分支（在 if 块内！）
                }

                // ── ⓪b 食物/饮料：补充量（★ v0.1.5 新增，为 2022 万圣节「食物增加属性」）──
                //   落点 `_props.effects_health.<Hydration|Energy>.value`（C# 侧 EffectsHealth → Hydration/Energy → Value）
                if (sel.Equals("food", StringComparison.OrdinalIgnoreCase))
                {
                    var hy = Get("hydrationMultiplier");
                    var en = Get("energyMultiplier");
                    int nH = double.IsNaN(hy) ? 0 : ScaleNested("EffectsHealth", "Hydration", hy);
                    int nE = double.IsNaN(en) ? 0 : ScaleNested("EffectsHealth", "Energy", en);
                    if (!double.IsNaN(hy) || !double.IsNaN(en))
                        Info($"食物补充量：水分 ×{hy} 改了 {nH} 个 / 能量 ×{en} 改了 {nE} 个（可还原）");
                    continue;   // ★ 只跳出本选择器分支（在 if 块内！）
                }

                // ── ① 霰弹弹药 / 所有弹药：damageMultiplier ──
                if (sel.Equals("shotgunAmmo", StringComparison.OrdinalIgnoreCase)
                    || sel.Equals("ammo", StringComparison.OrdinalIgnoreCase))
                {
                    bool shotgunOnly = sel.Equals("shotgunAmmo", StringComparison.OrdinalIgnoreCase);
                    Func<object, object, bool> match = (it, props) =>
                    {
                        if (GetNum(props, "Damage") is double d && double.IsNaN(d)) return false;
                        if (!shotgunOnly) return true;
                        // 霰弹判定：口径含 12g/12x70/20g/23x75，或名字以 patron_12x70 / patron_20x70 开头
                        var cal = (Prop(props, "Caliber") ?? "").ToString();
                        var nm = ItemName(it);
                        return cal.IndexOf("12g", StringComparison.OrdinalIgnoreCase) >= 0
                            || cal.IndexOf("12/70", StringComparison.OrdinalIgnoreCase) >= 0
                            || cal.IndexOf("20g", StringComparison.OrdinalIgnoreCase) >= 0
                            || cal.IndexOf("23x75", StringComparison.OrdinalIgnoreCase) >= 0
                            || nm.StartsWith("patron_12x70", StringComparison.OrdinalIgnoreCase)
                            || nm.StartsWith("patron_20x70", StringComparison.OrdinalIgnoreCase);
                    };

                    int scanned;
                    var dm = Get("damageMultiplier");
                    if (!double.IsNaN(dm))
                    {
                        var n = ScaleField(match, "Damage", dm, out scanned);
                        Info($"{(shotgunOnly ? "霰弹" : "全部")}弹药伤害 ×{dm}：改了 {n} 个（扫了 {scanned} 个物品，可还原）");
                    }
                    var pm = Get("penetrationMultiplier");
                    if (!double.IsNaN(pm))
                    {
                        var n = ScaleField(match, "PenetrationPower", pm, out scanned);
                        Info($"{(shotgunOnly ? "霰弹" : "全部")}弹药穿甲 ×{pm}：改了 {n} 个（可还原）");
                    }
                    var am = Get("armorDamageMultiplier");
                    if (!double.IsNaN(am))
                    {
                        var n = ScaleField(match, "ArmorDamage", am, out scanned);
                        Info($"{(shotgunOnly ? "霰弹" : "全部")}弹药护甲伤害 ×{am}：改了 {n} 个（可还原）");
                    }
                    continue;
                }

                // ── ② 近战武器：knifeHitSlashDam / knifeHitStabDam ──
                if (sel.Equals("melee", StringComparison.OrdinalIgnoreCase))
                {
                    Func<object, object, bool> match = (it, props) =>
                        !double.IsNaN(GetNum(props, "knifeHitSlashDam")) || !double.IsNaN(GetNum(props, "knifeHitStabDam"));
                    int scanned;
                    var sm = Get("slashMultiplier");
                    if (!double.IsNaN(sm))
                    {
                        var n = ScaleField(match, "knifeHitSlashDam", sm, out scanned);
                        Info($"近战挥砍伤害 ×{sm}：改了 {n} 把（扫了 {scanned} 个物品，可还原）");
                    }
                    var tm = Get("stabMultiplier");
                    if (!double.IsNaN(tm))
                    {
                        var n = ScaleField(match, "knifeHitStabDam", tm, out scanned);
                        Info($"近战突刺伤害 ×{tm}：改了 {n} 把（可还原）");
                    }
                    continue;
                }

                // ── ③ 针剂 Buff：stim:<Buff 名>（globals → config.Health.Effects.Stimulator.Buffs）──
                //    ★ 支持 `stim:*` = 全部针剂 Buff（表里共 51 条，其中针剂 20+ 条；食物/饮品的 buff 也在同一张表里）
                if (sel.StartsWith("stim:", StringComparison.OrdinalIgnoreCase))
                {
                    var buffName = sel.Substring(5).Trim();
                    var vm = Get("valueMultiplier");
                    var dmm = Get("durationMultiplier");
                    var cm = Get("chanceMultiplier");
                    // ★ v0.1.5 新增：忠实还原官方「删掉负面 debuff」「正向 buff 提到固定值」
                    //   官方 Stim Event 2022-12-21 的真实内容是：① Obdolbos 的正 buff 全提到 +20；
                    //   ② 11 种针剂**删掉负面 debuff**。旧实现只会 ×倍数 —— 连负值一起放大，**效果与官方相反**。
                    bool rmNeg = cfg.TryGetValue("removeNegative", out var rn) && rn > 0;
                    double? setPos = cfg.TryGetValue("setPositiveValue", out var sp) && sp > 0 ? sp : (double?)null;
                    bool all = buffName == "*" || buffName.Length == 0;
                    // ★ v0.1.5 新增：按**技能名**设值（官方 Hardcore Mode 2023-07-07 明写
                    //   「Buffs: Endurance (+50) / Strength (+50)」与「1800s Duration」）
                    //   用法：`"setSkill_Endurance": 50, "setSkill_Strength": 50, "setDuration": 1800`
                    var skillSets = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kvS in cfg)
                        if (kvS.Key.StartsWith("setskill_", StringComparison.OrdinalIgnoreCase) && kvS.Value > 0)
                            skillSets[kvS.Key.Substring("setskill_".Length)] = kvS.Value;
                    double setDur = cfg.TryGetValue("setDuration", out var sd) && sd > 0 ? sd : double.NaN;
                    var n = ScaleStimBuff(all ? null : buffName, vm, dmm, cm, rmNeg, setPos, skillSets, setDur, out var touched);
                    if (n == 0) logger?.Warning($"[EventRotator][物品] 针剂 Buff '{buffName}' 没找到或无数值可改");
                    else
                    {
                        var acts = new List<string>();
                        if (!double.IsNaN(vm)) acts.Add($"数值×{vm}");
                        if (!double.IsNaN(dmm)) acts.Add($"时长×{dmm}");
                        if (!double.IsNaN(cm)) acts.Add($"概率×{cm}");
                        if (rmNeg) acts.Add("负面归零");
                        if (setPos.HasValue) acts.Add($"正向设为{setPos}");
                        if (skillSets.Count > 0) acts.Add("技能设值[" + string.Join(",", skillSets.Select(kv2 => kv2.Key + "=" + kv2.Value)) + "]");
                        if (!double.IsNaN(setDur)) acts.Add($"时长设为{setDur}");
                        Info($"针剂 Buff {(all ? "（全部）" : buffName)}：改了 {n} 条、涉及 {touched} 个 Buff"
                             + $"（{(acts.Count > 0 ? string.Join(" / ", acts) : "无改动项")}，可还原）");
                    }
                    continue;
                }

                logger?.Warning($"[EventRotator][物品] 未知选择器 '{sel}'（支持 shotgunAmmo / ammo / melee / stim:<Buff名> / weapons / food）");
            }
        }
        catch (Exception ex) { logger?.Error($"[EventRotator][items] 失败: {ex.Message}"); }
    }

    /// <summary>
    /// 改 <c>globals → config.Health.Effects.Stimulator.Buffs.&lt;名&gt;</c> 数组里每条 buff 的 Value/Duration/Chance。
    /// <paramref name="buffName"/> 为 null = 全部 Buff 条目（<c>stim:*</c>）。
    /// </summary>
    private static int ScaleStimBuff(string buffName, double valueMult, double durationMult, double chanceMult,
                                     bool removeNegative, double? setPositiveValue,
                                     Dictionary<string, double> skillSets, double setDuration, out int touched)
    {
        int n = 0; touched = 0;
        try
        {
            var cfg = Prop(GlobalTable, "Configuration");
            var health = Prop(cfg, "Health");
            var effects = Prop(health, "Effects");
            var stim = Prop(effects, "Stimulator");
            var buffs = Prop(stim, "Buffs") as IDictionary;
            if (buffs == null) { Info("诊断：拿不到 config.Health.Effects.Stimulator.Buffs"); return 0; }

            var targets = new List<object>();
            if (buffName == null)
            {
                foreach (var k in buffs.Keys) targets.Add(buffs[k]);
            }
            else
            {
                foreach (var k in buffs.Keys)
                    if (string.Equals(k?.ToString(), buffName, StringComparison.OrdinalIgnoreCase)) { targets.Add(buffs[k]); break; }
                if (targets.Count == 0)
                {
                    var some = string.Join(",", buffs.Keys.Cast<object>().Select(x => x?.ToString()).Take(8));
                    Info($"诊断：Buffs 里没有 '{buffName}'（现有示例：{some}…）");
                    return 0;
                }
            }

            foreach (var listObj in targets)
            {
                if (!(listObj is IEnumerable list)) continue;
                var _toRemove = new List<object>();   // ★ v2.6.74【F2+】本列表待移除的负面条目
                int per = 0;
                foreach (var one in list)
                {
                    if (one == null) continue;
                    if (!double.IsNaN(valueMult)) { var v = GetNum(one, "Value"); if (!double.IsNaN(v) && SetNum(one, "Value", v * valueMult, "stim")) per++; }
                    if (!double.IsNaN(durationMult)) { var v = GetNum(one, "Duration"); if (!double.IsNaN(v) && SetNum(one, "Duration", v * durationMult, "stim")) per++; }
                    if (!double.IsNaN(chanceMult)) { var v = GetNum(one, "Chance"); if (!double.IsNaN(v) && SetNum(one, "Chance", Math.Min(1.0, v * chanceMult), "stim")) per++; }

                    // ★ v2.6.74【F2+ 批准】负面 debuff 的处理**推迟到本列表遍历之后**（改成"移除条目"）：
                    //   这里先收集（`foreach` 中不能直接移除 ⇒ 会抛枚举器异常）
                    if (removeNegative && IsNegativeStimEntry(one)) _toRemove.Add(one);
                    // ★ 官方 Hardcore Mode：按技能名设值 + 设时长
                    if (skillSets != null && skillSets.Count > 0)
                    {
                        var sn = one.GetType().GetProperty("SkillName",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(one)?.ToString() ?? "";
                        if (sn.Length > 0 && skillSets.TryGetValue(sn, out var wantSkill)
                            && SetNum(one, "Value", wantSkill, "stim")) per++;
                    }
                    if (!double.IsNaN(setDuration))
                    {
                        var v = GetNum(one, "Duration");
                        if (!double.IsNaN(v) && Math.Abs(v - setDuration) > 0.0001 && SetNum(one, "Duration", setDuration, "stim")) per++;
                    }

                    // ★ 官方 Obdolbos：正向数值统一提到指定值
                    if (setPositiveValue.HasValue)
                    {
                        var v = GetNum(one, "Value");
                        if (!double.IsNaN(v) && v > 0 && Math.Abs(v - setPositiveValue.Value) > 0.0001
                            && SetNum(one, "Value", setPositiveValue.Value, "stim")) per++;
                    }
                }
                // ★★★ v2.6.74【F2+ 用户批准】**把负面 debuff 从列表里移除**（不是归零）：
                //   判据 = `Value < 0`（SJ1/Zagustin/Adrenaline/MULE… 的 EnergyRate/HydrationRate 负值）
                //        或 `BuffType ∈ {HandsTremor, QuantumTunnelling}`（**SJ6 / Propital 的负面**，Value=0）
                //   还原 = **按原下标插回**（同一对象引用 + 同一位置）⇒ 列表顺序逐字节恢复 ✔
                //   ⚠ 只作用于本活动点名过的 buff（`targets`）；列表不是 `IList` 时**退回旧的"归零"语义**（不会更糟）
                if (_toRemove.Count > 0)
                {
                    if (list is IList il)
                    {
                        // 按下标**升序**移除 ⇒ 登记顺序=升序 ⇒ LIFO 还原时按"从右到左"插回 ⇒ 顺序完整恢复 ✔
                        foreach (var victim in _toRemove)
                        {
                            int idx0 = il.IndexOf(victim);
                            if (idx0 < 0) continue;
                            var capList = il; var capObj = victim; var capIdx = idx0;
                            AddRestore?.Invoke(() => { try { if (!capList.Contains(capObj)) capList.Insert(capIdx, capObj); } catch { } });
                            il.RemoveAt(idx0);
                            per++;
                        }
                    }
                    else
                    {
                        foreach (var victim in _toRemove)
                        {
                            var v = GetNum(victim, "Value");
                            if (!double.IsNaN(v) && v < 0 && SetNum(victim, "Value", 0d, "stim")) per++;
                        }
                    }
                    _toRemove.Clear();
                }

                n += per;
                if (per > 0) touched++;
            }
        }
        catch (Exception ex) { Info("改针剂 Buff 失败: " + ex.Message); }
        return n;
    }

    /// <summary>把指定针剂物品指向的 Buff 名找出来（诊断/配数据用）。</summary>
    public static string StimBuffNameOf(string itemId)
    {
        try
        {
            var items = Prop(TemplateTable, "Items") as IDictionary;
            if (items == null) return null;
            foreach (DictionaryEntry e in items)
            {
                if (!string.Equals(e.Key?.ToString(), itemId, StringComparison.OrdinalIgnoreCase)) continue;
                var props = Prop(e.Value, "Properties");
                return (Prop(props, "StimulatorBuffs") ?? "").ToString();
            }
        }
        catch { }
        return null;
    }
}
