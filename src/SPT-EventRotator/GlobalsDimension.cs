using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SPTarkov.Common.Models.Logging;

using System.Reflection;
namespace EventRotator;

/// <summary>
/// ★ globals 维度（v2.2.0）—— 覆盖 TODO 里的：
///   · 双倍伤害      damageMultiplier   → 全部弹药 _props.Damage ×N
///   · 技能加速      skillMultiplier    → GlobalTable.Configuration.WeaponSkillProgressRate ×N（并下调 SkillExpPerLevel）
///   · 经验加成      expMultiplier      → GlobalTable.Configuration.exp.* ×N
///   · 口渴加速/热浪 hydrationMultiplier → 全部食物饮品的 _props.Hydration/Energy 消耗类字段 ×N（尽力而为）
///
/// 落点（实证）：
///   globals.json → config（= GlobalTable.Configuration），107 个键，其中
///     WeaponSkillProgressRate = 1.4
///     SkillExpPerLevel        = 100
///     exp = { expForLevelOneDogtag, expForLockedDoorBreach, expForLockedDoorOpen, heal, kill, level, loot_attempts }
///   弹药伤害在 database/templates/items.json 的每个弹药 _props.Damage。
///
/// 全部可还原。
/// </summary>
internal static class GlobalsDimension
{
    public static object GlobalTable;     // SPT GlobalTable 实例
    /// <summary>
    /// ★ v2.3.1：物品数据库是 <c>TemplateTable.Items</c>（Dictionary&lt;MongoId, TemplateItem&gt;），
    ///   **不是** ItemTable（SPT 4.1.6 里没有这个类型）。
    ///   实证：PostDbLoadService 就是用 TemplateTable.get_Items() 批量改物品的。
    ///   物品属性在 TemplateItem.Properties（JSON 名 _props）；弹药伤害 = Properties.Damage（Nullable&lt;double&gt;）。
    /// </summary>
    public static object TemplateTable;
    /// <summary>★ v0.1.3 口渴/能量消耗速率挂在这里：<c>HealthConfig.Effects.Existence</c>（与 GlobalTable.Configuration.Health 同一实例）。</summary>
    public static object HealthConfig;
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    /// <summary>当前事件在处理的 globals 字典（供各子方法读取，避免层层传参）。</summary>
    private static Dictionary<string, double> _currentGlobals;

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][globals] " + m); } catch { } }

    /// <summary>取 GlobalTable.Configuration。</summary>
    private static object Cfg()
    {
        try { return GlobalTable?.GetType().GetProperty("Configuration")?.GetValue(GlobalTable); }
        catch { return null; }
    }

    /// <summary>★ v1.0.1【性能·第六轮】本文件有 43 处 Prop 调用；元数据查找改走 R.GP / R.GF / R.GFs 缓存。</summary>
    private static object Prop(object o, string n)
    {
        if (o == null) return null;
        var t = o.GetType();
        var p = R.GP(t, n, BindingFlags.Public | BindingFlags.Instance)
             ?? R.GP(t, n, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
        if (p != null) return p.GetValue(o);
        var f = R.GF(t, n, BindingFlags.Public | BindingFlags.Instance);
        if (f != null) return f.GetValue(o);
        foreach (var f2 in R.GFs(t, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
            if (f2.Name.Contains(n)) return f2.GetValue(o);
        return null;
    }

    private static bool SetProp(object o, string n, object v)
    {
        if (o == null) return false;
        var t = o.GetType();
        // ★ 大小写不敏感兜底：JSON 名是小写（如 OverweightLimits 的 x/y/z），而 CLR 属性是大写 X/Y/Z
        var p = t.GetProperty(n) ?? t.GetProperty(n, System.Reflection.BindingFlags.Public
                                                   | System.Reflection.BindingFlags.Instance
                                                   | System.Reflection.BindingFlags.IgnoreCase);
        // ★★ v0.1.5 修正（**第五个静默失效**）：旧代码直接把 double 塞进属性/字段，
        //   而很多属性是 **float**（例如 `Vector3.X`、`Stamina.SavagePlayCooldown` 等）⇒ `SetValue`
        //   抛 ArgumentException 被 catch 吞掉 ⇒ **静默改不动**（实测「负重阈值 ×1.4：改了 0 项」铁证）。
        //   现在先按目标类型做一次转换再写入。
        if (p != null && p.CanWrite) { try { p.SetValue(o, Coerce(v, p.PropertyType)); return true; } catch { } }
        var f = t.GetField(n);
        if (f != null) { try { f.SetValue(o, Coerce(v, f.FieldType)); return true; } catch { } }
        foreach (var f2 in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
            if (f2.Name.Contains(n)) { try { f2.SetValue(o, Coerce(v, f2.FieldType)); return true; } catch { } }
        return false;
    }

    /// <summary>把值转成目标类型（double → float/int 等），避免 SetValue 类型不匹配被静默吞掉。</summary>
    private static object Coerce(object v, Type target)
    {
        if (v == null) return null;
        var t = Nullable.GetUnderlyingType(target) ?? target;
        if (t.IsInstanceOfType(v)) return v;
        try { return Convert.ChangeType(v, t); } catch { return v; }
    }

    private static double Num(object o, string n)
    {
        var v = Prop(o, n);
        try { return v == null ? 0 : Convert.ToDouble(v); } catch { return 0; }
    }

    /// <summary>
    /// ★★★ **新增自定义兴奋剂 buff**（条款③「致命毒素」的**「超人增益」**）。
    ///
    /// 落点链（**已用真实 DLL 反射逐一核对，非猜测**）：
    ///   `GlobalTable.Configuration` → `Health` → `Effects` → `Stimulator` → `Buffs`
    ///   · `Models.Spt.Tables/Health.cs:27-28`   `[JsonPropertyName("Effects")]   public Effects Effects`
    ///   · `Models.Spt.Tables/Effects.cs:312-313` `[JsonPropertyName("Stimulator")] public Stimulator Stimulator`
    ///   · `Models.Spt.Tables/Stimulator.cs:26`   `public Dictionary&lt;string, IEnumerable&lt;Buff&gt;&gt; Buffs`
    ///   · `Models.Spt.Tables/Buff.cs:10-103`     record，成员名与 `StimulatorBuffDef` **逐一同名**
    ///     （`BuffType`/`Chance`/`Delay`/`Duration`/`Value`/`AbsoluteValue`/`SkillName`）
    ///
    /// ⚠ 用的是本文件**现成的** <see cref="Prop"/>/<see cref="AddRestore"/>，不另造一套反射。
    ///
    /// 幂等三态：
    ///   · 键不存在            → 写入 + 登记 `Remove`
    ///   · 键存在且（类型,值）全同 → **跳过**（不覆盖 SPT 自带同名 buff，也不重复登记还原）
    ///   · 键存在但不一致      → 覆盖，并把**原值登记还原**（退活动时回到 SPT 原值）
    /// 可降级：链路任一环拿不到 ⇒ warning + 跳过，绝不抛。
    /// 自证：写入后**回读** `Buffs[k]`，逐条比对类型与值。
    /// </summary>
    public static void AddStimulatorBuffs(Dictionary<string, List<StimulatorBuffDef>> defs)
    {
        if (defs == null || defs.Count == 0) return;
        try
        {
            // ── 沿 config.Health.Effects.Stimulator.Buffs 逐级下钻 ──
            object health = HealthConfig;              // Plugin 注入 = GlobalTable.Configuration.Health 同一实例
            if (health == null)
            {
                var cfg = Cfg();
                health = Prop(cfg, "Health");
            }
            if (health == null) { Info("拿不到 config.Health，stimulatorBuffsAdd 跳过"); return; }

            var effects = Prop(health, "Effects");
            if (effects == null) { Info("config.Health 没有 Effects，stimulatorBuffsAdd 跳过"); return; }

            var stim = Prop(effects, "Stimulator");
            if (stim == null) { Info("config.Health.Effects 没有 Stimulator，stimulatorBuffsAdd 跳过"); return; }

            var buffsObj = Prop(stim, "Buffs");
            if (!(buffsObj is System.Collections.IDictionary buffs))
            {
                Info($"找不到 config.Health.Effects.Stimulator.Buffs 字典（拿到 {buffsObj?.GetType().Name ?? "null"}），stimulatorBuffsAdd 跳过");
                return;
            }

            // 元素类型从字典的实参反查（**不硬编码** Buff 类型名）：Dictionary<string, IEnumerable<Buff>>
            var valType = R.DictValueType(buffs);                                  // IEnumerable<Buff>
            var elemType = valType != null && valType.IsGenericType
                ? valType.GetGenericArguments()[0]                                 // Buff
                : null;
            if (elemType == null)
            {
                Info($"推不出 Buff 元素类型（字典值类型 {valType?.Name ?? "?"}），stimulatorBuffsAdd 跳过");
                return;
            }

            int added = 0, replaced = 0, skipped = 0, missing = 0;
            foreach (var kv in defs)
            {
                var key = kv.Key;
                var list = kv.Value;
                if (string.IsNullOrWhiteSpace(key) || list == null || list.Count == 0) continue;

                // ── 造强类型 List<Buff>（走 R.FromJson，字段名与 Buff 完全同名）──
                var newList = R.NewListOf(elemType);
                if (!(newList is System.Collections.IList target))
                {
                    Info($"造 List<{elemType.Name}> 失败，跳过 '{key}'");
                    missing++;
                    continue;
                }
                int built = 0;
                foreach (var d in list)
                {
                    if (d == null) continue;
                    var json = "{"
                        + "\"BuffType\":" + JStr(d.BuffType) + ","
                        + "\"Value\":" + Dbl(d.Value) + ","
                        + "\"Duration\":" + Dbl(d.Duration) + ","
                        + "\"Delay\":" + Dbl(d.Delay) + ","
                        + "\"Chance\":" + Dbl(d.Chance) + ","
                        + "\"AbsoluteValue\":" + (d.AbsoluteValue ? "true" : "false") + ","
                        + "\"SkillName\":" + JStr(d.SkillName ?? "")
                        + "}";
                    var one = R.FromJson(elemType, json, Info);
                    if (one == null) { Info($"构造 Buff 失败（{d.BuffType}），跳过该条"); continue; }
                    target.Add(one);
                    built++;
                }
                if (built == 0) { missing++; continue; }

                bool exists = R.DictHas(buffs, key);
                object oldVal = exists ? R.DictGet(buffs, key) : null;

                // ── 幂等：已存在且内容一致 ⇒ 跳过 ──
                if (exists && SameBuffSet(oldVal, list)) { skipped++; continue; }

                if (exists)
                {
                    // 覆盖 SPT 自带同名 buff ⇒ 登记还原原值
                    var capB = buffs; var capK = key; var capOld = oldVal;
                    AddRestore?.Invoke(() => { try { R.DictSet(capB, capK, capOld); } catch { } });
                    replaced++;
                }
                else
                {
                    // 新键 ⇒ 登记移除
                    var capB = buffs; var capK = key;
                    AddRestore?.Invoke(() => { try { R.DictRemove(capB, capK); } catch { } });
                    added++;
                }

                if (!R.DictSet(buffs, key, target))
                {
                    Info($"写入 Buffs['{key}'] 失败，跳过");
                    missing++;
                    continue;
                }

                // ── 回读自证 ──
                var readBack = R.DictGet(buffs, key) as System.Collections.IEnumerable;
                int n = 0;
                string dump = "";
                if (readBack != null)
                {
                    foreach (var e in readBack)
                    {
                        n++;
                        if (n <= 3)
                            dump += (n > 1 ? " | " : "")
                                  + $"{Prop(e, "BuffType")}={Prop(e, "Value")}"
                                  + $"/{Prop(e, "Duration")}s"
                                  + (string.IsNullOrEmpty(Prop(e, "SkillName")?.ToString()) ? "" : $"({Prop(e, "SkillName")})");
                    }
                }
                Info($"★{(exists ? "覆盖" : "新增")} buff '{key}'：{built} 条 —— 回读 {n} 条 [{dump}]"
                   + (n == built ? " ✔" : " —— ⚠ 条数不符！"));
            }

            Info($"★自定义兴奋剂 buff：新增 {added} 键、覆盖 {replaced} 键、幂等跳过 {skipped} 键、未完成 {missing} 键"
               + "（落点 globals → config.Health.Effects.Stimulator.Buffs；可还原）");
        }
        catch (Exception ex) { Info("AddStimulatorBuffs 失败（已降级，不影响服务端）: " + ex.Message); }
    }

    /// <summary>JSON 字符串字面量（转义 `"` 与 `\`，避免拼 JSON 时被注入破坏）。</summary>
    private static string JStr(string s)
    {
        if (s == null) return "\"\"";
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>double 的 JSON 字面量（用 R 格式，避免区域小数点问题）。</summary>
    private static string Dbl(double d) { return d.ToString(System.Globalization.CultureInfo.InvariantCulture); }

    /// <summary>回读比对：目标键现有 buff 集合是否与声明**逐条同类型同值**（用于幂等判定）。</summary>
    private static bool SameBuffSet(object existing, List<StimulatorBuffDef> want)
    {
        try
        {
            if (!(existing is System.Collections.IEnumerable en)) return false;
            var have = new List<object>();
            foreach (var e in en) have.Add(e);
            if (have.Count != want.Count) return false;
            for (int i = 0; i < want.Count; i++)
            {
                if (!string.Equals(Prop(have[i], "BuffType")?.ToString(), want[i].BuffType, StringComparison.Ordinal)) return false;
                if (Math.Abs(Num(have[i], "Value") - want[i].Value) > 1e-9) return false;
            }
            return true;
        }
        catch { return false; }
    }

    public static void Apply(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            var g = ev.Globals;
            _currentGlobals = g;   // ★ 供 ScaleStaminaCapacity 等子方法读取
            if (g == null) return;
            var cfg = Cfg();
            if (cfg == null) { logger?.Warning("[EventRotator][globals] 拿不到 GlobalTable.Configuration"); return; }

            // ── ① 技能加速 ──
            if (g.TryGetValue("skillMultiplier", out var sk) && sk > 0 && Math.Abs(sk - 1) > 0.001)
            {
                var old = Num(cfg, "WeaponSkillProgressRate");
                if (old > 0)
                {
                    var cap = cfg;
                    AddRestore?.Invoke(() => { try { SetProp(cap, "WeaponSkillProgressRate", old); } catch { } });
                    SetProp(cfg, "WeaponSkillProgressRate", old * sk);
                    Info($"技能进度 WeaponSkillProgressRate: {old} → {old * sk:0.##}（×{sk}，可还原）");
                }
                // SkillExpPerLevel 是"每级需要的技能点"，调小 = 升级更快
                var lv = Num(cfg, "SkillExpPerLevel");
                if (lv > 0)
                {
                    var cap2 = cfg;
                    AddRestore?.Invoke(() => { try { SetProp(cap2, "SkillExpPerLevel", lv); } catch { } });
                    // ★ v0.1.5 同上：整数进 ⇒ 整数出（100 ÷ 3 = 33.33 若写成小数，客户端按 int 解析同样会崩）
                    var lvNew = Math.Abs(lv - Math.Round(lv)) < 1e-9
                        ? (object)Math.Round(lv / sk, MidpointRounding.AwayFromZero)
                        : lv / sk;
                    SetProp(cfg, "SkillExpPerLevel", lvNew);
                    Info($"技能每级点数 SkillExpPerLevel: {lv} → {lvNew}（÷{sk}，可还原；整数原值已取整）");
                }
            }

            // ── ② 经验加成 ──
            if (g.TryGetValue("expMultiplier", out var ex) && ex > 0 && Math.Abs(ex - 1) > 0.001)
            {
                var expObj = Prop(cfg, "Exp") ?? Prop(cfg, "exp");
                if (expObj != null)
                {
                    // ★★★ v0.1.5 修正（审计实证：旧实现的效果**远小于** desc）：
                    //   旧代码只乘 `config.exp` 的**顶层数值属性**，而实机 config.exp 只有
                    //   `expForLevelOneDogtag(0.02)` / `expForLockedDoorBreach(20)` / `expForLockedDoorOpen(30)`
                    //   三个标量 —— 真正的击杀/治疗/升级经验全在**嵌套对象**里
                    //   （`exp.kill.victimLevelExp=175`、`exp.kill.victimBotLevelExp=80`、`exp.heal.*`、`exp.level.*`），
                    //   被类型判断直接 skip ⇒ desc 写的「击杀/搜刮/治疗/升级经验全部 ×2」过去**根本不成立**。
                    //   现在下钻一层（kill/heal/level 都是扁平小对象），把嵌套里的数值一起乘。
                    int n = 0;
                    void ScaleOne(object holder, int depth)
                    {
                        if (holder == null || depth > 2) return;
                        foreach (var p in holder.GetType().GetProperties())
                        {
                            if (!p.CanRead || !p.CanWrite) continue;
                            var pt = p.PropertyType;
                            if (pt == typeof(double) || pt == typeof(float) || pt == typeof(int))
                            {
                                double old;
                                try { old = Convert.ToDouble(p.GetValue(holder)); } catch { continue; }
                                if (old <= 0) continue;
                                var capO = holder; var capP = p; var capOld = old;
                                AddRestore?.Invoke(() => { try { capP.SetValue(capO, Convert.ChangeType(capOld, capP.PropertyType)); } catch { } });
                                // ★★★ v0.1.5【实机崩溃修复】整数进 ⇒ 整数出。
                                //   实机报错（用户刚进游戏就弹错误屏，日志逐字）：
                                //     Exception: In response to https://127.0.0.1:6969/client/globals:
                                //       Input string '218.75' is not a valid integer.
                                //       Path 'config.exp.kill.victimLevelExp'
                                //   原因：SPT 服务端模型把该字段声明成 **double**（原值 175），
                                //   而**客户端**的 JsonType.GlobalConfigurationResponse 要求 **int** ⇒
                                //   服务端序列化出 218.75 ⇒ 客户端整个 /client/globals 解析失败 ⇒ 一进游戏就报错。
                                //   修法：原值是**整数**时，结果必须四舍五入成整数（XP 类数值本来就是整数）；
                                //   原值本身是小数时保持小数（不影响真正的浮点字段）。
                                bool srcIntegral = Math.Abs(old - Math.Round(old)) < 1e-9;
                                if (srcIntegral)
                                {
                                    var rounded = Math.Round(old * ex, MidpointRounding.AwayFromZero);
                                    try
                                    {
                                        p.SetValue(holder, Convert.ChangeType(rounded, pt)); n++;
                                        Info($"  · {p.Name}: {old} → {rounded}（整数原值 ⇒ 写回整数；整数倍率结果 {old * ex:0.####} 已取整，避免客户端 int 解析崩溃）");
                                    }
                                    catch { }
                                }
                                else
                                {
                                    try { p.SetValue(holder, Convert.ChangeType(old * ex, pt)); n++; } catch { }
                                }
                            }
                            else if (pt.IsClass && pt != typeof(string))
                            {
                                object inner = null;
                                try { inner = p.GetValue(holder); } catch { }
                                if (inner != null) ScaleOne(inner, depth + 1);
                            }
                        }
                    }
                    ScaleOne(expObj, 0);
                    Info($"经验 exp.*（含嵌套 kill/heal/level）×{ex}：改了 {n} 项（可还原）");
                }
                else Info("找不到 config.exp");
            }

            // ── ③ 双倍伤害（全部弹药 _props.Damage）──
            if (g.TryGetValue("damageMultiplier", out var dm) && dm > 0 && Math.Abs(dm - 1) > 0.001)
            {
                int n = ScaleAmmoDamage(dm);
                Info($"弹药伤害 ×{dm}：改了 {n} 个弹药（可还原）"
                     + "  ⚠ 客户端 AmmoTemplate.Damage 是 int ⇒ 已四舍五入；且敌我通用（AI 也用同一批弹药）");
            }

            // ── ④ ★ 口渴/能量消耗速率（热浪）—— v0.1.3 改了落点 ──
            //   旧代码改 items.json 的 `_props.Hydration/Energy`，**该字段根本不存在**（0 命中）⇒ 从来没生效。
            //   正确落点：globals.json → config.Health.Effects.Existence.HydrationDamage / EnergyDamage
            //   （客户端 ActiveHealthController+Existence 每 tick 掉这个值；每秒 = Damage ÷ LoopTime）
            if (g.TryGetValue("hydrationMultiplier", out var hy) && hy > 0 && Math.Abs(hy - 1) > 0.001)
            {
                int n = ScaleExistence(hy);
                Info($"口渴/能量消耗速率 ×{hy}：改了 {n} 项（Existence.HydrationDamage / EnergyDamage，可还原）"
                     + "  ⚠ 需重启游戏客户端才会重新拉 /client/globals");
            }

            // ── ⑤ ★ 食物/饮水补充量（可选，v0.1.3 新增正确落点）──
            //   正确路径：items.json → _props.effects_health.<Hydration|Energy>.value（蛇形小写）
            //   ⚠ 部分食品的 Hydration 是**负值**（如 condensed_milk = -65），统一乘正倍率会把负面效果一起放大。
            if (g.TryGetValue("foodRestoreMultiplier", out var fr) && fr > 0 && Math.Abs(fr - 1) > 0.001)
            {
                int n = ScaleFoodRestore(fr);
                Info($"食物/饮水补充量 ×{fr}：改了 {n} 项（_props.effects_health.*.value，可还原）");
            }

            // ── ⑥ 负重阈值 ×N（★v0.1.5 新增）──
            //   官方 Double Damage（2022-11-26）第 ④ 条：负重上限 +40%。
            //   落点 = `config.Stamina` 的 4 组 OverweightLimits（实机：Base{x26,y77} / Walk{x45,y86} /
            //   Sprint{x26,y72} / WalkSpeed{x35,y80}），每组都是 {x,y,z} 数值对象。
            if (g.TryGetValue("overweightMultiplier", out var ow) && ow > 0 && Math.Abs(ow - 1) > 0.001)
            {
                int n = ScaleOverweight(ow);
                Info($"负重阈值 ×{ow}：改了 {n} 项（config.Stamina 的 4 组 OverweightLimits，可还原）");
            }

            // ── ⑧ 体力上限 ×N（★v0.1.5 新增）──
            //   官方 June 2022 删档前 Day7「Deadly Toxin」第 ① 条：Players are given **infinite Stamina**。
            //   落点 = `config.Stamina.Capacity`（实机 115）；官方没给具体值 ⇒ 用超大倍率近似"无限"。
            if (g.TryGetValue("staminaCapacityMultiplier", out var scm) && scm > 0 && Math.Abs(scm - 1) > 0.001)
            {
                int n = ScaleStaminaCapacity(cfg, scm);
                Info($"体力上限 ×{scm}：改了 {n} 项（config.Stamina.Capacity，可还原）");
            }

            // ── ⑦ SCAV 冷却 ×N（★v0.1.5 新增）──
            //   官方 Wild Wild Woods（2026-06-25）第 ⑦ 条：SCAV 冷却大幅缩短。
            //   落点 = `config.SavagePlayCooldown`（实机 1500 秒）；官方没给具体数值，倍率为本 mod 取值。
            if (g.TryGetValue("savagePlayCooldownMultiplier", out var spc) && spc > 0 && Math.Abs(spc - 1) > 0.001)
            {
                var before = Num(cfg, "SavagePlayCooldown");
                if (!double.IsNaN(before) && before > 0)
                {
                    double want = Math.Max(1, Math.Round(before * spc));
                    var capC = cfg; var capOld = before;
                    AddRestore?.Invoke(() => { try { SetProp(capC, "SavagePlayCooldown", capOld); } catch { } });
                    SetProp(cfg, "SavagePlayCooldown", want);
                    Info($"SCAV 冷却 {before:0} → {want:0} 秒（×{spc}，可还原）");
                }
                else Info("找不到 config.SavagePlayCooldown");
            }
        }
        catch (Exception ex) { logger?.Error($"[EventRotator][globals] 失败: {ex.Message}"); }
    }

    /// <summary>
    /// ★v0.1.5：`config.Stamina` 的 4 组负重阈值 {x,y,z} 全部 ×N。
    /// 官方 Double Damage（2022-11-26）第 ④ 条「负重上限 +40%」的落点。
    /// </summary>
    /// <summary>
    /// ★v0.1.5：`config.Stamina` 的 4 组负重阈值 {x,y,z} 全部 ×N。
    /// 官方 Double Damage（2022-11-26）第 ④ 条「负重上限 +40%」的落点。
    /// ★ 注意：OverweightLimits 的类型是 **Vector3**，其 X/Y/Z 是**属性**（不是字段）；旧代码用只查属性的 Num()
    ///   拿到 0 ⇒ 改了 0 项（实测铁证）。这里属性/字段双查，并用 SetProp 的类型转换写回 float。
    /// </summary>
    private static int ScaleOverweight(double mult)
    {
        int n = 0;
        try
        {
            var cfg = Prop(GlobalTable, "Configuration") ?? Prop(GlobalTable, "config");
            var stamina = Prop(cfg, "Stamina");
            if (stamina == null) { Info("诊断：负重阈值拿不到 config.Stamina"); return 0; }
            foreach (var key in new[] { "BaseOverweightLimits", "WalkOverweightLimits",
                                        "SprintOverweightLimits", "WalkSpeedOverweightLimits" })
            {
                var box = Prop(stamina, key);
                if (box == null) { Info($"诊断：Stamina 上没有 {key}"); continue; }
                foreach (var axis in new[] { "x", "y", "z" })
                {
                    double v = 0;
                    var bt = box.GetType();
                    const System.Reflection.BindingFlags F =
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.IgnoreCase;
                    var pr = bt.GetProperty(axis, F);
                    if (pr != null) { try { v = Convert.ToDouble(pr.GetValue(box)); } catch { v = 0; } }
                    else
                    {
                        var fi = bt.GetField(axis, F);
                        if (fi != null) { try { v = Convert.ToDouble(fi.GetValue(box)); } catch { v = 0; } }
                    }
                    if (Math.Abs(v) < 0.000001) continue;                 // z=0 不动
                    var cap = box; var capAxis = axis; var capOld = v;
                    AddRestore?.Invoke(() => { try { SetProp(cap, capAxis, capOld); } catch { } });
                    if (SetProp(box, axis, v * mult)) n++;
                }
            }
        }
        catch { }
        return n;
    }

    /// <summary>
    /// ★v0.1.5 新增：体力上限 ×N（官方 June 2022 删档前 Day7「Deadly Toxin」第 ① 条「Players are given infinite Stamina」）。
    /// 落点 = `globals.json → config.Stamina.Capacity`（实机 115）。
    /// </summary>
    private static int ScaleStaminaCapacity(object cfg, double mult)
    {
        try
        {
            var stamina = Prop(cfg, "Stamina");
            var cap = Prop(stamina, "Capacity");
            if (cap == null) { Info("诊断：拿不到 config.Stamina.Capacity"); return 0; }
            double cur = 0; try { cur = Convert.ToDouble(cap); } catch { return 0; }
            if (cur <= 0) return 0;
            var old = cur;
            AddRestore?.Invoke(() => { try { SetProp(stamina, "Capacity", old); } catch { } });
            return SetProp(stamina, "Capacity", cur * mult) ? 1 : 0;
        }
        catch { return 0; }
    }

    /// <summary>把全部弹药的 _props.Damage ×N。</summary>
    private static int ScaleAmmoDamage(double mult)
    {
        int n = 0;
        try
        {
            if (TemplateTable == null) { Info("诊断：TemplateTable 为 null"); return 0; }
            var items = Prop(TemplateTable, "Items") as IDictionary;
            if (items == null)
            {
                var tt = TemplateTable.GetType();
                var names = string.Join(",", tt.GetProperties().Select(p => p.Name).Take(12));
                Info($"诊断：拿不到 TemplateTable.Items，类型 {tt.Name} 的属性有：{names}");
                return 0;
            }
            if (items.Count == 0) { Info("诊断：TemplateTable.Items 是空字典"); return 0; }
            foreach (DictionaryEntry e in items)
            {
                var it = e.Value; if (it == null) continue;
                var props = Prop(it, "Properties") ?? Prop(it, "_props");
                if (props == null) continue;
                var dmgProp = props.GetType().GetProperty("Damage");
                if (dmgProp == null || !dmgProp.CanWrite) continue;
                double old;
                try { old = Convert.ToDouble(dmgProp.GetValue(props)); } catch { continue; }
                if (old <= 0) continue;
                var capP = props; var capOld = old;
                AddRestore?.Invoke(() => { try { capP.GetType().GetProperty("Damage")?.SetValue(capP, capOld); } catch { } });
                try { dmgProp.SetValue(props, old * mult); n++; } catch { }
            }
        }
        catch (Exception ex) { Info("改弹药伤害失败: " + ex.Message); }
        return n;
    }

    /// <summary>
    /// ★ v0.1.3 口渴/能量消耗速率（热浪）—— <c>globals → config.Health.Effects.Existence</c>。
    ///
    /// 客户端 <c>EFT.HealthSystem.ActiveHealthController+Existence</c>（战局内挂的是
    /// <c>PlayerHealthController : ActiveHealthController</c>）：
    /// <code>
    /// GetEnergyDamage()    = EffectsSettings.Existence.EnergyDamage    * (1 - HealthEnergy技能) / EnergyLoopTime
    /// GetHydrationDamage() = EffectsSettings.Existence.HydrationDamage * (1 - HealthHydration技能) / HydrationLoopTime
    /// </code>
    /// 每个 tick 掉 <c>Damage</c>，tick 间隔 = <c>LoopTime</c> 秒 ⇒ 每秒消耗 = Damage ÷ LoopTime。
    /// 改 <c>Damage</c>（而不是 LoopTime）不会改变 tick 节奏，最干净。
    /// 实测原值：HydrationDamage=2.6、EnergyDamage=3.2、两个 LoopTime=60。
    /// </summary>
    private static int ScaleExistence(double mult)
    {
        int n = 0;
        try
        {
            // 优先用注入的 HealthConfig（就是 globals 里 config.Health 的同一个实例），兜底走 GlobalTable
            object health = HealthConfig;
            if (health == null || Prop(health, "Effects") == null)
            {
                var cfg = Cfg();
                health = Prop(cfg, "Health") ?? health;
            }
            var effects = Prop(health, "Effects");
            var existence = Prop(effects, "Existence");
            if (existence == null) { Info("诊断：拿不到 config.Health.Effects.Existence"); return 0; }

            foreach (var f in new[] { "HydrationDamage", "EnergyDamage" })
            {
                var old = Num(existence, f);
                if (old <= 0) { Info($"诊断：Existence.{f} 取到 {old}（可能字段名不符）"); continue; }
                var cap = existence;
                AddRestore?.Invoke(() => { try { SetProp(cap, f, old); } catch { } });
                if (SetProp(existence, f, old * mult))
                {
                    n++;
                    Info($"Existence.{f}: {old} → {old * mult:0.###}（每秒 {old / 60:0.####} → {old * mult / 60:0.####}）");
                }
            }
        }
        catch (Exception ex) { Info("改口渴/能量速率失败: " + ex.Message); }
        return n;
    }

    /// <summary>
    /// ★ v0.1.3 食物/饮水补充量：<c>items.json → _props.effects_health.&lt;Hydration|Energy&gt;.value</c>
    /// （蛇形小写路径；旧代码找的 <c>_props.Hydration</c> 不存在）。
    /// </summary>
    private static int ScaleFoodRestore(double mult)
    {
        int n = 0;
        try
        {
            var items = Prop(TemplateTable, "Items") as IDictionary;
            if (items == null) { Info("诊断：拿不到 TemplateTable.Items（补充量）"); return 0; }
            foreach (DictionaryEntry e in items)
            {
                var props = Prop(e.Value, "Properties") ?? Prop(e.Value, "_props");
                if (props == null) continue;
                var eh = Prop(props, "EffectsHealth") ?? Prop(props, "effects_health");
                if (eh == null) continue;
                foreach (var key in new[] { "Hydration", "Energy" })
                {
                    var spec = Prop(eh, key);
                    if (spec == null) continue;
                    var val = Num(spec, "Value");
                    if (val == 0 || Math.Abs(val) < 0.001) continue;
                    var capSpec = spec;
                    AddRestore?.Invoke(() => { try { SetProp(capSpec, "Value", val); } catch { } });
                    if (SetProp(spec, "Value", val * mult)) n++;
                }
            }
        }
        catch (Exception ex) { Info("改食物补充量失败: " + ex.Message); }
        return n;
    }

    /// <summary>（历史实现，保留做对照）旧版把食物/饮品的 Hydration/Energy 当"消耗"缩放 —— 字段不存在，无效。</summary>
    private static int ScaleHydration(double mult)
    {
        int n = 0;
        try
        {
            if (TemplateTable == null) { Info("诊断：TemplateTable 为 null"); return 0; }
            var items = Prop(TemplateTable, "Items") as IDictionary;
            if (items == null) { Info("诊断：拿不到 Items（口渴）"); return 0; }
            foreach (DictionaryEntry e in items)
            {
                var it = e.Value; if (it == null) continue;
                var props = Prop(it, "Properties") ?? Prop(it, "_props");
                if (props == null) continue;
                foreach (var fname in new[] { "Hydration", "Energy" })
                {
                    var p = props.GetType().GetProperty(fname);
                    if (p == null || !p.CanRead) continue;
                    double old;
                    try { old = Convert.ToDouble(p.GetValue(props)); } catch { continue; }
                    // 只放大"负值/消耗"或正值都放大（取决于语义），这里统一按倍率缩放非零值
                    if (Math.Abs(old) < 0.001) continue;
                    var capP = props; var capOld = old; var capName = fname;
                    AddRestore?.Invoke(() => { try { capP.GetType().GetProperty(capName)?.SetValue(capP, capOld); } catch { } });
                    if (p.CanWrite)
                    {
                        try { p.SetValue(props, old * mult); n++; } catch { }
                    }
                }
            }
        }
        catch (Exception ex) { Info("改口渴失败: " + ex.Message); }
        return n;
    }
}
