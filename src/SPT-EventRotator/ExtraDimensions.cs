using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★★ v0.1.5「重验后补做」的 6 个维度（用户要求把重验出的 9 条能做的一次补齐）。
///
/// 背景：一次全清单重验发现，我原先判成"做不到"的条目里，有 9 条其实是**能做**的
/// （冻伤那次的教训：不是"SPT 没有"，而是"本体有、只是没打开/我落点找错了"）。
/// 本文件实现其中落点已逐字核实过的 6 条：
///   ① 取消购买上限   `assort.items[].upd.BuyRestrictionMax`（实测该字段存在，抽样 250/400）
///   ② boss 血量      `bots/types/&lt;type&gt;.json → health.BodyParts`
///   ③ 瞄准抖动       `globals.config.AimPunchMagnitude`（float，原值 5.76）+ `Aiming` 字典
///   ④ 惯性           `globals.config.Inertia`（字典，逐数值缩放）
///   ⑤ 玩家受伤倍率   `globals.config.HandsOverdamage / LegsOverdamage / StomachOverdamage`
///   ⑥ 撤离点锁定     `locations/&lt;map&gt;/base.json → exits[].Chance`（+ secretExits）
///   ⑦ 按图禁用转场   `locations/&lt;map&gt;/base.json → transits[].active`
///   ⑧ 按 bot 类型注入装备 `bots/types/&lt;type&gt;.json → inventory.equipment[槽位][tpl] = 概率`
///      （★ 这条解决了"PBS/ABPS 拦截 botAddItems"的误判：**直接写数据层完全有效**，
///        本轮已用同一路径做成圣诞老人刷新 + 礼物清单 1898 件）
///
/// 所有改动都记录还原闭包 ⇒ 换活动/关活动时逐条回滚。
/// </summary>
internal static class ExtraDimensions
{
    public static ISptLogger<EventRotatorServer> Log;
    public static Action<Action> AddRestore;
    public static object BotTableRef;
    public static object LocationTableRef;
    public static object GlobalTableRef;
    public static object LocaleTableRef;   // ★ v0.1.5 第1组：locale 表（官方 RenameBitcoin 落点）
    /// <summary>★ 物品模板表（`TemplateTable`）：容器守卫用 —— 判某个 tpl 有没有 `Properties.Grids`。</summary>
    public static object TemplateTableRef;

    // ── 容器守卫（2026-10-16 线上 NRE 事故）──────────────────────────────────────
    /// <summary>
    /// SPT 把这几个**装备槽**当容器用：`BotInventoryGenerator.cs:33`
    /// <c>_equipmentSlotsWithInventory = {Pockets, TacticalVest, Backpack, SecuredContainer}</c>，
    /// 命中就走 `BotInventoryGenerator.cs:498 AddEmptyContainerToBot(...)`
    /// → `BotInventoryContainerService.cs:39-51 ContainerDetails..ctor`
    /// → <c>foreach (Grid grid in containerDbItem.Properties.Grids)</c>
    /// ⇒ **写进这些槽的 tpl 必须真的有 `Properties.Grids`，否则 `foreach(null)` 直接 NRE，
    ///    表现为 `Failed to generate bot (assault)`，bot 整批刷不出来。**
    /// </summary>
    private static readonly HashSet<string> _containerEquipmentSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Pockets", "TacticalVest", "Backpack", "SecuredContainer"
    };

    /// <summary>容器守卫的"只提示一次"集合（键 = 槽名|tpl）。</summary>
    private static readonly HashSet<string> _containerGuardWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 容器守卫：某 tpl 能不能安全地放进**装备容器槽**（Pockets/TacticalVest/Backpack/SecuredContainer）。
    /// 返回 false 的唯一条件 = 查得到模板但**没有 Grids**（那种写进去必崩 bot 生成）。
    /// ⚠ 查不到模板时**返回 true**（不拦）—— 因为模板表拿不到时我们不该擅自阻断正常注入，
    ///   而真正会崩的是"模板存在但无 Grids"这一类（已由线上日志+数据证实）。
    /// </summary>
    private static bool IsSafeContainerEquipmentTpl(string tpl)
    {
        try
        {
            if (TemplateTableRef == null) return true;              // 拿不到模板表 ⇒ 不拦（保守）
            var items = Prop(TemplateTableRef, "Items") as IDictionary;
            if (items == null) return true;
            object key = null;
            var kt = items.GetType().IsGenericType ? items.GetType().GetGenericArguments()[0] : typeof(string);
            try { key = Coerce(tpl, kt); } catch { }
            if (key == null || !items.Contains(key)) return true;   // 查不到 ⇒ 不拦
            var props = Prop(items[key], "Properties") ?? Prop(items[key], "_props");
            if (props == null) return false;                        // 模板在但 Properties 为空 ⇒ 会崩，拦
            var grids = Prop(props, "Grids");
            if (grids == null) return false;                        // ★ 就是这一类在崩：Grids == null
            if (grids is System.Collections.ICollection c && c.Count == 0) return false;  // 空 Grids 也不是容器
            return true;
        }
        catch { return true; }
    }

    /// <summary>
    /// ★★★ P1-2/P1-3【verify-a 审计】**供其它维度复用的容器守卫入口**（`BotEquipmentDimension.ApplyBotItems`
    /// 与 `EventBotTemplateFix.FixEquipment` 共用同一实现，避免各写一份）。
    ///
    /// 语义：只有当 **槽是容器槽**（Pockets / TacticalVest / Backpack / SecuredContainer）**且** 该 tpl
    /// 查得到模板但**没有 `Properties.Grids`** 时才返回 false —— 那种写进去必崩：
    /// `BotInventoryContainerService.cs:39-51 ContainerDetails..ctor` → `foreach (Grid grid in containerDbItem.Properties.Grids)`
    /// ⇒ NRE ⇒ 客户端 `Failed to generate bot (assault)`、**整批 bot 刷不出来**。
    ///
    /// 返回 false 时调用方必须 <c>continue</c>（跳过这一条，不要写那个槽）；本函数会**只提示一次**（键 = `槽名|tpl`）。
    /// 非容器槽 / 空参数 / 模板表拿不到 / 模板查不到 ⇒ 一律返回 true（不拦）。
    /// </summary>
    internal static bool CanPutIntoEquipmentSlot(string slotKey, string tpl)
    {
        try
        {
            if (string.IsNullOrEmpty(slotKey) || string.IsNullOrEmpty(tpl)) return true;
            if (!_containerEquipmentSlots.Contains(slotKey)) return true;      // 不是容器槽 ⇒ 不管
            if (IsSafeContainerEquipmentTpl(tpl)) return true;                 // 有 Grids（或查不到 ⇒ 保守放行）
            var key = slotKey + "|" + tpl;
            if (_containerGuardWarned.Add(key))
                Log?.Warning($"[EventRotator][容器守卫] ⚠ {slotKey} 槽跳过物品 {tpl}：该模板**没有 Properties.Grids**，"
                           + "写进去会让 bot 生成 NRE（`Failed to generate bot`，整批刷不出来）。已跳过这一条（只会提示一次）");
            return false;
        }
        catch { return true; }                                                 // 守卫自身出问题 ⇒ 不拦（保守）
    }

    /// <summary>「撤离时间已缩放」护栏：`地图|撤离点名` → 已缩放到的秒数（防同一轮重复缩放）。</summary>
    private static readonly Dictionary<string, double> _exitTimeScaled = new();

    /// <summary>★ v2.6.39：`eventGearKeepBossIconic` 跳过提示只打一次（键 = `bot|槽`）。</summary>
    private static readonly HashSet<string> _iconicKeptWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>「服务端拿不到仪式信号」的详细说明只打一次（每局仍会打一行简短状态）。</summary>
    private static bool _ritualLockWarned;

    // ───────────────────────── 通用反射工具 ─────────────────────────
    /// <summary>★ v1.0.1【性能·第六轮】本文件有 162 处 Prop 调用；元数据查找改走 R.GP / R.GF 缓存
    /// （属性与字段的 flags 不同 ⇒ 分别缓存）；`?.GetValue` 的语义逐字保留。</summary>
    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        return R.GP(t, name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(o)
            ?? R.GF(t, name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(o);
    }

    private static bool SetProp(object o, string name, object val)
    {
        if (o == null) return false;
        var t = o.GetType();
        // ★ v1.0.1【性能】改走 R.GP / R.GF 的 (Type,name,flags) 缓存 —— 原来每次都做一次
        //   全量 GetProperty/GetField 扫描；战利品维度会按刷点数量调用它（几十万次量级）。
        var p = R.GP(t, name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (p != null && p.CanWrite) { p.SetValue(o, Coerce(val, p.PropertyType)); return true; }
        var f = R.GF(t, name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (f != null) { f.SetValue(o, Coerce(val, f.FieldType)); return true; }
        return false;
    }

    private static object Coerce(object v, Type t)
    {
        if (v == null) return null;
        try
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            if (u == typeof(double)) return Convert.ToDouble(v);
            if (u == typeof(float)) return (float)Convert.ToDouble(v);
            if (u == typeof(int)) return (int)Math.Round(Convert.ToDouble(v));
            if (u == typeof(bool)) return Convert.ToBoolean(v);
            if (u == typeof(string)) return v.ToString();
            if (u.IsEnum) return Enum.Parse(u, v.ToString(), true);
            // ★ SPT 的物品 id 类型是 **MongoId**（不是 string）—— 实测若不转换会报
            //   `The value "…" is not of type "SPTarkov.Server.Core.Models.Common.MongoId"`。
            if (u.Name == "MongoId" || u.FullName == "SPTarkov.Server.Core.Models.Common.MongoId")
            {
                var ctor = u.GetConstructor(new[] { typeof(string) });
                if (ctor != null) return ctor.Invoke(new object[] { v.ToString() });
                var parse = u.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (parse != null) return parse.Invoke(null, new object[] { v.ToString() });
            }
            return Convert.ChangeType(v, u);
        }
        catch { return v; }
    }

    /// <summary>把字典（IEnumerable&lt;KeyValuePair&gt;）转成 List&lt;(key, value)&gt;。</summary>
    private static List<KeyValuePair<object, object>> Pairs(object dict)
    {
        var list = new List<KeyValuePair<object, object>>();
        if (dict is IDictionary d)
            foreach (DictionaryEntry e in d) list.Add(new(e.Key, e.Value));
        else if (dict is IEnumerable en)
            foreach (var item in en)
            {
                if (item == null) continue;
                var k = Prop(item, "Key"); var v = Prop(item, "Value");
                if (k != null) list.Add(new(k, v));
            }
        return list;
    }

    // ───────────────────────── ① 取消购买上限 ─────────────────────────
    /// <summary>
    /// 官方「Unlocked Traders &amp; Discounts」第三条：**All item purchase limits are removed**。
    /// 落点：每个商人 `assort.items[].upd.BuyRestrictionMax`（实测样例
    /// `{"UnlimitedCount": true, "BuyRestrictionMax": 10, "BuyRestrictionCurrent": 0}`）。
    /// 置 `null` + `UnlimitedCount = true`；`BuyRestrictionCurrent = 0`。可还原。
    /// </summary>
    public static void ApplyPurchaseLimits(bool remove, object tradersTable)
    {
        if (!remove || tradersTable == null) return;
        int traders = 0, items = 0;
        try
        {
            var dict = tradersTable as IDictionary;
            if (dict == null) { Log?.Warning("[EventRotator][购买上限] TraderTable 拿不到，跳过"); return; }
            foreach (DictionaryEntry te in dict)
            {
                var tr = te.Value; if (tr == null) continue;
                var assort = Prop(tr, "Assort"); if (assort == null) continue;
                var itemList = Prop(assort, "Items") as IList;
                if (itemList == null || itemList.Count == 0) continue;
                bool touched = false;
                foreach (var it in itemList)
                {
                    var upd = Prop(it, "Upd"); if (upd == null) continue;
                    var pm = upd.GetType().GetProperty("BuyRestrictionMax",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (pm == null || !pm.CanWrite) continue;
                    var oldMax = pm.GetValue(upd);
                    if (oldMax == null) continue;                       // 本来就是无限
                    var cap = upd; var capP = pm; var capO = oldMax;
                    AddRestore?.Invoke(() => { try { capP.SetValue(cap, capO); } catch { } });
                    pm.SetValue(upd, null);
                    // UnlimitedCount 同步置 true（否则客户端仍显示限购）
                    var pu = upd.GetType().GetProperty("UnlimitedCount", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (pu != null && pu.CanWrite)
                    {
                        var oldU = pu.GetValue(upd);
                        var c2 = upd; var p2 = pu; var o2 = oldU;
                        AddRestore?.Invoke(() => { try { p2.SetValue(c2, o2); } catch { } });
                        pu.SetValue(upd, true);
                    }
                    items++; touched = true;
                }
                if (touched) traders++;
            }
            Log?.Warning($"[EventRotator][购买上限] ★已取消购买上限：{traders} 个商人 / {items} 个条目（官方 Unlocked Traders 第三条，可还原）");
            // ★ v2.6.74：通知「商人易物追加」维度 —— 它在本维度**之后**才追加条目
            //   （Plugin.cs:1941 取消上限 → :2082 追加易物）；不通知的话，同一轮里"官方说全取消"、
            //   而我们追加的那几条仍带 `BuyRestrictionMax`（自相矛盾）。由 `Plugin.Restore()` 复位。
            try { TraderAssortAddDimension.LimitsGloballyRemoved = true; } catch { }
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][购买上限] 失败: {ex.Message}"); }
    }

    // ───────────────────────── ② boss 血量 ─────────────────────────
    /// <summary>
    /// 按 bot 类型缩放血量：`bots/types/&lt;type&gt;.json → health.BodyParts[0][部位].max/min`。
    /// 实测 bossTagilla: Chest 320 / Head 100 / LeftArm 130…；官方 2022 万圣节说"血量池提到 1900"（未给逐部位值）⇒
    /// 本维度用**倍率**表达（1.5 = ×1.5），具体数字由活动数据决定并标注。
    /// </summary>
    public static void ApplyBotHealth(Dictionary<string, double> table)
    {
        if (table == null || table.Count == 0 || BotTableRef == null) return;
        try
        {
            var types = Prop(BotTableRef, "Types") as IDictionary;
            if (types == null) { Log?.Warning("[EventRotator][血量] BotTable.Types 拿不到"); return; }
            int hit = 0;
            foreach (var kv in table)
            {
                object bt = null;
                foreach (DictionaryEntry de in types)
                    if (string.Equals(de.Key?.ToString(), kv.Key, StringComparison.OrdinalIgnoreCase)) { bt = de.Value; break; }
                if (bt == null) { Log?.Warning($"[EventRotator][血量] 找不到 bot 类型 '{kv.Key}'"); continue; }

                // ★ 属性名兼容：SPT 的 BotType 模型用 **BotHealth**（DLL 里实测有 `BotHealth` 类型；
                //   JSON 里是 `health`）。旧实现只试 `Health` ⇒ 报"没有 Health 字段"，写 0 个数值。
                var health = Prop(bt, "BotHealth") ?? Prop(bt, "Health") ?? Prop(bt, "health");
                if (health == null) { Log?.Warning($"[EventRotator][血量] '{kv.Key}' 没有 Health/BotHealth 字段"); continue; }
                var partsObj = Prop(health, "BodyParts");
                var parts = partsObj as IList;
                if (parts == null || parts.Count == 0) { Log?.Warning($"[EventRotator][血量] '{kv.Key}'.BodyParts 为空"); continue; }

                foreach (var partSet in parts)
                {
                    // ★ 实测：`health.BodyParts` 是一个**列表**，元素是**普通对象**（每个部位是它的一个属性：
                    //   Chest/Head/LeftArm…，每个部位对象再带 max/min）⇒ 不能用字典那套读，要遍历属性/字段。
                    var members = new List<KeyValuePair<string, object>>();
                    if (partSet is IDictionary dpart)
                    {
                        foreach (DictionaryEntry de in dpart) members.Add(new(de.Key?.ToString() ?? "", de.Value));
                    }
                    else
                    {
                        foreach (var m in partSet.GetType().GetMembers(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (m is PropertyInfo pi2)
                            {
                                if (pi2.GetIndexParameters().Length > 0) continue;
                                object v2 = null; try { v2 = pi2.GetValue(partSet); } catch { continue; }
                                members.Add(new(pi2.Name, v2));
                            }
                            else if (m is FieldInfo fi2)
                            {
                                object v2 = null; try { v2 = fi2.GetValue(partSet); } catch { continue; }
                                members.Add(new(fi2.Name, v2));
                            }
                        }
                    }

                    foreach (var pair in members)
                    {
                        var part = pair.Value; if (part == null) continue;
                        // 只处理"部位对象"（要有 max/min 其一），跳过字符串/数字等元数据
                        bool looksLikePart = part.GetType().GetProperty("Max", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) != null
                                          || part.GetType().GetProperty("Min", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) != null
                                          || part.GetType().GetField("Max", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase) != null
                                          || part.GetType().GetField("Min", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase) != null;
                        if (!looksLikePart) continue;
                        foreach (var fld in new[] { "max", "min" })
                        {
                            var pf = part.GetType().GetProperty(fld, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                  ?? part.GetType().GetField(fld, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase) as MemberInfo;
                            if (pf == null) continue;
                            double oldV;
                            try { oldV = Convert.ToDouble(pf is PropertyInfo pi ? pi.GetValue(part) : ((FieldInfo)pf).GetValue(part)); }
                            catch { continue; }
                            var newV = oldV * kv.Value;
                            var capO = part; var capM = pf; var capV = oldV;
                            AddRestore?.Invoke(() =>
                            {
                                try
                                {
                                    if (capM is PropertyInfo p3) p3.SetValue(capO, Coerce(capV, p3.PropertyType));
                                    else if (capM is FieldInfo f3) f3.SetValue(capO, Coerce(capV, f3.FieldType));
                                }
                                catch { }
                            });
                            if (pf is PropertyInfo p4) p4.SetValue(part, Coerce(newV, p4.PropertyType));
                            else ((FieldInfo)pf).SetValue(part, Coerce(newV, ((FieldInfo)pf).FieldType));
                            hit++;
                        }
                    }
                }
                Log?.Warning($"[EventRotator][血量] ★'{kv.Key}' 血量 ×{kv.Value:0.##}（逐部位 max/min，可还原）");
            }
            Log?.Warning($"[EventRotator][血量] 共写入 {hit} 个数值");
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][血量] 失败: {ex.Message}"); }
    }

    // ──────────────────── ②b bot 血量逐部位覆盖（mod 自定义维度）────────────────────
    /// <summary>
    /// ★★★【按 bot 类型**逐部位覆盖**血量】**mod 自定义维度**（官方没有对应机制）。
    /// <para>写法：<c>"botHealthOverride": { "arenafighterevent": { "Head": 60, "Chest": 120 } }</c>。</para>
    /// <para>落点：`BotTable.Types[role].BotHealth.BodyParts[0][部位]` 的 `min` / `max` **同时**写成给定值
    /// （真实数据：`BodyParts` 是**列表**、只有 1 个元素；该元素在 JSON 里形如 `{部位名:{min,max}}`，
    /// 到了 CLR 模型是 `BodyPart` 对象、**部位是它的属性**，属性类型 `MinMax&lt;double&gt;`）。
    /// **只覆盖 JSON 里写了的部位**，没写的保持原值。</para>
    /// <para>还原：写之前把该部位的原 `{min,max}` 记进 <c>AddRestore</c> 快照 ⇒ 活动关闭/切换时精确放回
    /// （与 <see cref="ApplyBotAppearanceOverride"/> 同一套机制，`Restore()` 逆序回放）。</para>
    /// <para>降级：角色找不到 / 没有 `BotHealth` / `BodyParts` 为空 / 值非法（≤0、NaN、∞）
    /// ⇒ 只打 warning 并跳过这一条，不影响其它部位与其它维度。</para>
    /// </summary>
    public static void ApplyBotHealthOverride(Dictionary<string, Dictionary<string, double>> spec)
    {
        if (spec == null || spec.Count == 0) return;
        if (BotTableRef == null) { Log?.Warning("[EventRotator][血量] 拿不到 BotTable"); return; }
        try
        {
            var types = Prop(BotTableRef, "Types") as IDictionary;
            if (types == null) { Log?.Warning("[EventRotator][血量] BotTable.Types 拿不到"); return; }

            // 部位容器（`BodyParts` 列表的单个元素）→ [部位名, 部位对象] 列表。
            // 实测（真机数据 + 探针）：`BodyParts` 是 `List<BodyPart>`，部位是 `BodyPart` 的**属性**
            // （Chest/Head/LeftArm/…，值类型 `MinMax<double>`，带 Min/Max）；
            // 同时兼容「列表里一个 dict：部位名 → {min,max}」这种形态（判据与上面的 ApplyBotHealth 一致，
            // 避免同一份数据在两处行为不同）。
            List<KeyValuePair<string, object>> PartMembers(object partSet)
            {
                var list = new List<KeyValuePair<string, object>>();
                if (partSet is IDictionary d)
                {
                    foreach (DictionaryEntry de in d) list.Add(new(de.Key?.ToString() ?? "", de.Value));
                    return list;
                }
                foreach (var m in partSet.GetType().GetMembers(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m is PropertyInfo pi2)
                    {
                        if (pi2.GetIndexParameters().Length > 0) continue;
                        object v2 = null; try { v2 = pi2.GetValue(partSet); } catch { continue; }
                        list.Add(new(pi2.Name, v2));
                    }
                    else if (m is FieldInfo fi2)
                    {
                        object v2 = null; try { v2 = fi2.GetValue(partSet); } catch { continue; }
                        list.Add(new(fi2.Name, v2));
                    }
                }
                return list;
            }

            int hit = 0, bad = 0;
            foreach (var roleKv in spec)
            {
                var roleName = roleKv.Key?.ToString();
                if (string.IsNullOrWhiteSpace(roleName) || roleKv.Value == null || roleKv.Value.Count == 0) continue;

                // 角色名匹配：OrdinalIgnoreCase（与 ApplyBotHealth / BotEquipmentDimension 一致）
                object bt = null;
                foreach (DictionaryEntry de in types)
                    if (string.Equals(de.Key?.ToString(), roleName, StringComparison.OrdinalIgnoreCase)) { bt = de.Value; break; }
                if (bt == null) { Log?.Warning($"[EventRotator][血量] 找不到 bot 类型 '{roleName}'"); continue; }

                // ★ SPT 前缀陷阱：JSON 的 `health` 在 CLR 模型里是 **`BotHealth`**
                //   （与 inventory→BotInventory、appearance→BotAppearance 同一套路）
                var health = Prop(bt, "BotHealth") ?? Prop(bt, "Health") ?? Prop(bt, "health");
                if (health == null) { Log?.Warning($"[EventRotator][血量] '{roleName}' 没有 Health/BotHealth 字段"); continue; }

                var parts = Prop(health, "BodyParts") as IList;
                if (parts == null || parts.Count == 0) { Log?.Warning($"[EventRotator][血量] '{roleName}'.BodyParts 为空"); continue; }

                foreach (var partSet in parts)
                {
                    if (partSet == null) continue;
                    var members = PartMembers(partSet);

                    foreach (var wantKv in roleKv.Value)
                    {
                        var partName = wantKv.Key?.ToString();
                        var want = wantKv.Value;
                        if (string.IsNullOrWhiteSpace(partName)) continue;
                        if (double.IsNaN(want) || double.IsInfinity(want) || want <= 0)
                        {
                            bad++;
                            Log?.Warning($"[EventRotator][血量] ⚠ '{roleName}' 的 {partName}={want:0.##} 非法（必须 > 0），跳过该部位");
                            continue;
                        }

                        object part = null;
                        foreach (var kvp in members)
                            if (string.Equals(kvp.Key, partName, StringComparison.OrdinalIgnoreCase)) { part = kvp.Value; break; }
                        if (part == null) { Log?.Warning($"[EventRotator][血量] '{roleName}' 没有部位 '{partName}'"); continue; }

                        // `max` / `min` **同时**写成给定值；原值逐字段进快照（还原时精确放回）
                        foreach (var fld in new[] { "max", "min" })
                        {
                            var pf = part.GetType().GetProperty(fld, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                  ?? part.GetType().GetField(fld, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase) as MemberInfo;
                            if (pf == null) continue;
                            double oldV;
                            try { oldV = Convert.ToDouble(pf is PropertyInfo pi ? pi.GetValue(part) : ((FieldInfo)pf).GetValue(part)); }
                            catch { continue; }
                            var capO = part; var capM = pf; var capV = oldV;
                            AddRestore?.Invoke(() =>
                            {
                                try
                                {
                                    if (capM is PropertyInfo p3) p3.SetValue(capO, Coerce(capV, p3.PropertyType));
                                    else if (capM is FieldInfo f3) f3.SetValue(capO, Coerce(capV, f3.FieldType));
                                }
                                catch { }
                            });
                            if (pf is PropertyInfo p4) p4.SetValue(part, Coerce(want, p4.PropertyType));
                            else ((FieldInfo)pf).SetValue(part, Coerce(want, ((FieldInfo)pf).FieldType));
                        }
                        hit++;
                        Log?.Info($"[EventRotator][血量] {roleName} {partName}={want:0.##}");
                    }
                }
            }
            Log?.Info($"[EventRotator][血量] ★逐部位覆盖：{hit} 个部位（min/max 同写）、{bad} 个非法值跳过"
                    + "（落点 BotHealth.BodyParts[0][部位]；原值已进快照，可精确还原）");
        }
        catch (Exception ex) { Log?.Warning("[EventRotator][血量] 逐部位覆盖失败（已降级）: " + ex.Message); }
    }

    // ───────────────────────── ③④⑤ globals 标量 / 字典缩放 ─────────────────────────
    /// <summary>
    /// 通用：把一个 `globals.config` 下的**数值字段**按倍率缩放（如 `AimPunchMagnitude` 5.76 → ×0.4 = −60%）。
    /// </summary>
    public static void ScaleConfigNumber(string key, double mult)
    {
        try
        {
            var cfg = Prop(GlobalTableRef, "Configuration") ?? Prop(GlobalTableRef, "config");
            if (cfg == null) { Log?.Warning($"[EventRotator][globals] 拿不到 config（{key}）"); return; }
            var cur = Prop(cfg, key);
            if (cur == null) { Log?.Warning($"[EventRotator][globals] config.{key} 不存在"); return; }
            double oldV;
            try { oldV = Convert.ToDouble(cur); } catch { Log?.Warning($"[EventRotator][globals] config.{key} 不是数值"); return; }
            var newV = oldV * mult;
            if (!SetProp(cfg, key, newV)) { Log?.Warning($"[EventRotator][globals] config.{key} 写不进去"); return; }
            var capO = cfg; var capK = key; var capV = oldV;
            AddRestore?.Invoke(() => { try { SetProp(capO, capK, capV); } catch { } });
            Log?.Warning($"[EventRotator][globals] ★config.{key}: {oldV:0.####} → {newV:0.####}（×{mult:0.##}，可还原）");
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][globals] {key} 缩放失败: {ex.Message}"); }
    }

    /// <summary>
    /// 通用：把一个**配置子对象**里的所有数值字段按倍率缩放（如 `config.Inertia` 是字典、`config.Aiming` 也是字典）。
    /// 只动数值型；字符串/布尔跳过。逐字段快照还原。
    /// </summary>
    public static void ScaleConfigObject(string key, double mult)
    {
        try
        {
            var cfg = Prop(GlobalTableRef, "Configuration") ?? Prop(GlobalTableRef, "config");
            var obj = Prop(cfg, key);
            if (obj == null) { Log?.Warning($"[EventRotator][globals] config.{key} 不存在"); return; }
            int n = 0;
            foreach (var m in obj.GetType().GetMembers(BindingFlags.Public | BindingFlags.Instance))
            {
                Type mt = m switch { PropertyInfo p => p.PropertyType, FieldInfo f => f.FieldType, _ => null };
                if (mt == null) continue;
                var u = Nullable.GetUnderlyingType(mt) ?? mt;
                if (u != typeof(double) && u != typeof(float) && u != typeof(int)) continue;
                double oldV;
                try
                {
                    var v = m is PropertyInfo p2 ? p2.GetValue(obj) : ((FieldInfo)m).GetValue(obj);
                    if (v == null) continue;
                    oldV = Convert.ToDouble(v);
                }
                catch { continue; }
                var capO = obj; var capM = m; var capV = oldV;
                AddRestore?.Invoke(() =>
                {
                    try
                    {
                        if (capM is PropertyInfo p3) p3.SetValue(capO, Coerce(capV, p3.PropertyType));
                        else ((FieldInfo)capM).SetValue(capO, Coerce(capV, ((FieldInfo)capM).FieldType));
                    }
                    catch { }
                });
                try
                {
                    if (m is PropertyInfo p4) p4.SetValue(obj, Coerce(oldV * mult, p4.PropertyType));
                    else ((FieldInfo)m).SetValue(obj, Coerce(oldV * mult, ((FieldInfo)m).FieldType));
                    n++;
                }
                catch { }
            }
            Log?.Warning($"[EventRotator][globals] ★config.{key} 内 {n} 个数值 ×{mult:0.##}（可还原）");
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][globals] {key} 对象缩放失败: {ex.Message}"); }
    }

    // ───────────────────────── ⑥⑦ 撤离点锁定 / 按图禁用转场 ─────────────────────────
    /// <summary>
    /// ⑥ 撤离点锁定：把**不在 keepList** 里的常规撤离点 `Chance` 置 0（保留清单里的）。
    ///   `base.exits[].Chance` 实测存在（bigmap 11 个）；另把 `secretExits` 也一并按清单处理。
    /// ⑦ 按图禁用转场：把指定地图 `base.transits[].active` 置 false。
    /// 两者都逐值快照。
    /// </summary>
    /// <summary>
    /// ★★★ v2.6.17 【撤离点锁定**触发时机**】—— 从 `exitKeepOnly` 列表里的**指令项**读（无需改 events.json 的 schema）：
    ///   · <c>@timing=always</c>：**进图就锁**（旧行为；只在手动/测试时用，官方语义不是这样）
    ///   · <c>@timing=off</c>   ：明确不要这个功能（静默）
    ///   · <c>@timing=ritual</c>（**默认**，没写指令时也是它）：官方语义 =「杀了 Zryachiy、仪式开始之后才只留 1 个」，
    ///     但**服务端拿不到那个时刻**（客户端纯本地事件链，见 <see cref="ApplyLocationExtras"/> 里的长注释）
    ///     ⇒ 实际行为 = **什么都不锁**（保证"打仪式前撤离点正常"）。
    ///   多个指令时**后者优先**；大小写与空格不敏感。
    /// </summary>
    private static string ExitLockTiming(List<string> keep)
    {
        var res = "ritual";
        if (keep == null) return res;
        foreach (var raw in keep)
        {
            var s = (raw ?? "").Trim();
            if (!s.StartsWith("@", StringComparison.Ordinal)) continue;
            var body = s.Substring(1).Trim();
            int eq = body.IndexOf('=');
            var key = (eq < 0 ? body : body.Substring(0, eq)).Trim().ToLowerInvariant();
            var val = (eq < 0 ? "" : body.Substring(eq + 1)).Trim().ToLowerInvariant();
            if (key != "timing") continue;
            if (val == "always" || val == "ritual" || val == "off") res = val;
        }
        return res;
    }

    /// <summary>
    /// `exits[].EntryPoints`（形如 "Customs,Boiler Tanks"）→ **小写、去空格**的出生点组名列表。
    /// 与客户端同款解析：`Assembly-CSharp:910080`
    /// `EligibleEntryPoints = settings.EntryPoints.ToLower().Split(',')`，
    /// 匹配用的是 `:521601` `EligibleEntryPoints.Contains(profile.Info.EntryPoint.ToLower())`
    /// （`Info.EntryPoint` 由**客户端自己**在战局开始时写入：`:668971-668973`）。
    /// </summary>
    private static List<string> ExitGroupsOf(object exit)
    {
        var res = new List<string>();
        var raw = Prop(exit, "EntryPoints")?.ToString();
        if (string.IsNullOrEmpty(raw)) return res;
        foreach (var part in raw.Split(','))
        {
            var g = part.Trim().ToLowerInvariant();
            if (g.Length > 0 && !res.Contains(g)) res.Add(g);
        }
        return res;
    }

    /// <summary>
    /// 撤离点「能不能当那个唯一保留点」的档位，依据客户端 `Assembly-CSharp:911129-911140`
    /// 的 `ERequirementState → IExfiltrationRequirement` 映射：
    ///   3 = **无阻塞条件**（`PassageRequirement` 缺失 / `None` → null）；
    ///   2 = **空槽类**（`Empty` → `EmptySlotRequirement`，玩家身上留个空位就能走；
    ///       只有 3 档一个都没有时才用它，例如 Reserve 的 `EXFIL_vent`）；
    ///   1 = **有阻塞条件**（`TransferItem` 要道具 / `WorldEvent` 要世界事件 / `ScavCooperation` 要合作 /
    ///       `Train` 要列车 / `Reference` 语义存疑 ⇒ 一律不当保留点）。
    /// </summary>
    private static int ExitUsableTier(object exit)
    {
        var s = Prop(exit, "PassageRequirement")?.ToString();
        if (string.IsNullOrEmpty(s)) return 3;
        if (string.Equals(s, "None", StringComparison.OrdinalIgnoreCase)) return 3;
        if (string.Equals(s, "Empty", StringComparison.OrdinalIgnoreCase)) return 2;
        return 1;
    }

    /// <summary>撤离点当前 Chance（读不到按 0）。</summary>
    private static double ExitChanceOf(object exit)
    {
        try { var v = Prop(exit, "Chance"); return v == null ? 0 : Convert.ToDouble(v); } catch { return 0; }
    }

    /// <summary>
    /// 官方「延长撤离时间」倍率 = `globals.config.EventSettings.ExitTimeMultiplier`（本机实测 = 3）。
    /// 客户端那份 `HalloweenEventController.UpdateExits`（`Assembly-CSharp:568345`）只在**收到服务端
    /// `ExitName`** 时才把该点 `ExfiltrationTime *= ExitTimeMultiplier`；而 SPT **没有**下发
    /// `HalloweenSyncExitsEvent`/`ExitName` 的通道（全库只有 `SeasonalEventService.UpdateGlobalEvents`
    /// 改 globals，不碰网络事件）⇒ 我们直接按**同一个倍率**改数据，效果等价（客户端 `:909833` 就从
    /// 服务端数据读 `ExfiltrationTime`）。读不到 ≤1 的值 ⇒ 返回 0（不缩放）。
    /// </summary>
    private static double ExitTimeMultiplier()
    {
        try
        {
            var es = Prop(Prop(GlobalTableRef, "Configuration"), "EventSettings");
            var v = Prop(es, "ExitTimeMultiplier");
            if (v != null)
            {
                double d;
                if (double.TryParse(v.ToString(), out d) && d > 1.0001 && d < 100) return d;
            }
        }
        catch { }
        return 0;
    }
    public static void ApplyLocationExtras(List<string> exitKeep, List<string> lockMaps, List<string> transitDisableMaps,
                                             Dictionary<string, List<string>> transitDisableIds = null)
    {
        if (LocationTableRef == null) return;
        try
        {
            var dict = LocationTableRef.GetType().GetMethod("GetDictionary")?.Invoke(LocationTableRef, null) as IDictionary;
            if (dict == null) { Log?.Warning("[EventRotator][撤离点] LocationTable.GetDictionary 拿不到"); return; }

            object Find(string mapKey)
            {
                foreach (DictionaryEntry de in dict)
                    if (string.Equals(de.Key?.ToString(), mapKey, StringComparison.OrdinalIgnoreCase)) return de.Value;
                return null;
            }

            // ⑥ 撤离点锁定（★★★ v2.6.16 重写：**按出生点**保证「每局恰好 1 个可用撤离点」）
            //
            //   ── 为什么必须重写（用户实测 + 客户端反编译实证）──
            //   旧实现把各图 `exits[0]` 当保留点（bigmap=Crossroads / woods=ZB-016 / shoreline=Tunnel …），
            //   但客户端**在服务端数据之上还有一层按出生点的过滤**（0.16.9 实测行号）：
            //     · `Assembly-CSharp:668971-668973` 战局开始时**客户端自己挑出生点**，写 `Profile.Info.EntryPoint`，
            //       然后 `ExfiltrationController.InitAllExfiltrationPoints(..., justLoadSettings:false, ...)`；
            //     · `:521498-521500` 该初始化里 `RollChance(ep)`（`:521614` = `Random(0,100) <= Settings.Chance`）
            //       失败 ⇒ `Status = NotPresent`（Chance=0 的点**永远**失败 ⇒ 看不见 —— **我们置 0 的思路是对的**）；
            //     · `:521592` + `:521600-521607` `EligiblePoints(profile)` = 先按
            //       `EligibleEntryPoints.Contains(Info.EntryPoint.ToLower())` 过滤，再按 `Chance > 0` 过滤；
            //       而 `EligibleEntryPoints` 就是服务端 `exits[].EntryPoints` 的**小写逗号拆分**（`:910080`）。
            //   ⇒ 旧实现的致命处：`Crossroads` 的 `EntryPoints` 只有 `"Boiler Tanks"`、`woods|ZB-016` 只有 `"House"`、
            //     `shoreline|Tunnel` 只有 `"Riverside"` …… **出生点不落在这一组时，唯一保留的点也会被客户端过滤掉
            //     ⇒ UI 上一个撤离点都没有**（= 用户实测「撤离点全都关了没地方撤离」，且 ZB-1011 之类的点根本不亮）。
            //
            //   ── 本版语义（官方原文 "close all extractions but **1** with an increased extraction timer"）──
            //     ① 每个**出生点组**（= `exits[].EntryPoints` 的并集；实测与 `SpawnPointParams[].Infiltration` 一一对应，
            //        7 张图**无缺口**）都保证有 **1 个**可用点 ⇒ 任何出生点都能撤，而对**具体某个玩家**来说仍然只有 1 个；
            //     ② 保留点**强制 `Chance=100`**：`RollChance` 是按概率掷的，`Chance<100` 的点有概率当局被关
            //        —— 旧清单里 `woods|ZB-016` 就是 66 ⇒ 34% 的局会「一个都没有」；
            //     ③ 秘密撤离点关 **PMC**（`SecretExits[].EligibleForPMC=false`）：`SecretEligiblePoints()`（`:521609`）
            //        只看这个字段，而它们的 JSON 里**没有 `Chance`** ⇒ 旧代码那句「置 0」是**静默无效**的；
            //        `EligibleForScav` **不动** ⇒ 不会把 SCAV 玩家困死（SCAV 专用点来自 `AllExtracts`，本函数不碰）；
            //     ④ 「延长撤离时间」用**数据**实现（官方那半条，我们从来没做过）：保留点
            //        `ExfiltrationTime ×= globals.config.EventSettings.ExitTimeMultiplier`（本机实测 = 3）。
            //        ⚠ 不下发 `HalloweenSyncExitsEvent(ExitName)`：客户端只在 `HalloweenEventController.UpdateExits`
            //        （`:568339`）里用它来 `*= ExitTimeMultiplier`，而 **SPT 没有这条 global event 下发通道**
            //        （全库只有 `SeasonalEventService.UpdateGlobalEvents` 改 `globals`，不碰网络事件）⇒ 改数据是等价做法。
            // ★★★ v2.6.17【触发时机语义修正】官方原文是
            //   "**Finding and killing the roaming Zryachiy** will close all extractions but 1 with an increased
            //    extraction timer, turn the time to night and start a ritual…" ⇒ **触发条件是「杀了 Zryachiy」**，
            //   **不是「活动生效」**。所以默认 **进图什么都不锁**（打仪式之前撤离点一切正常）；
            //   只有 `"@timing=always"` 才立刻锁（供手动/测试，例如想现场看效果）。
            //   ⚠ 为什么默认不能做成 "ritual"：见下面 else 分支的日志 —— **服务端拿不到「仪式开始」这个时刻**
            //   （客户端 `BotHalloweenEvent.StartRitual` 是纯本地事件，且撤离点在客户端只在开战时读一次）。
            var exitTiming = ExitLockTiming(exitKeep);
            if (exitKeep != null && exitKeep.Count > 0 && lockMaps != null && exitTiming == "always")
            {
                int mapsDone = 0, zeroed = 0, alreadyZero = 0, forced100 = 0, secretOff = 0, timeScaled = 0;
                double timeMul = ExitTimeMultiplier();      // 0 = 不缩放
                var keptAll = new List<string>();
                var noCover = new List<string>();

                foreach (var mk in lockMaps)
                {
                    var loc = Find(mk); if (loc == null) continue;
                    var b = Prop(loc, "Base"); if (b == null) continue;
                    // 该图适用的保留片段 = 全局片段 + 前缀匹配本图的片段（`"地图键|名称片段"`）
                    var frags = new List<string>();
                    foreach (var raw in exitKeep)
                    {
                        var s = (raw ?? "").Trim();
                        if (s.Length == 0) continue;
                        if (s.StartsWith("@", StringComparison.Ordinal)) continue;   // ★ 指令项（@timing=…）不是名字片段
                        int bar = s.IndexOf('|');
                        if (bar < 0) { frags.Add(s); continue; }
                        var mkPre = s.Substring(0, bar).Trim();
                        if (string.Equals(mkPre, mk, StringComparison.OrdinalIgnoreCase))
                            frags.Add(s.Substring(bar + 1).Trim());
                    }
                    if (frags.Count == 0) continue;   // 该图没指定保留谁 ⇒ 不动（避免"全关=出不去"）

                    var list = Prop(b, "Exits") as IList;
                    if (list == null || list.Count == 0) continue;

                    // ── 收集：名字 / 出生点组 / 可用档位（1=无阻塞条件，2=空槽类，0=有阻塞条件）──
                    var names = new List<string>();
                    var groupsOf = new List<List<string>>();
                    var tier = new List<int>();
                    var allGroups = new List<string>();
                    foreach (var ex in list)
                    {
                        names.Add((Prop(ex, "Name") ?? Prop(ex, "Id"))?.ToString() ?? "");
                        var gs = ExitGroupsOf(ex);
                        groupsOf.Add(gs);
                        tier.Add(ExitUsableTier(ex));
                        foreach (var g in gs) if (!allGroups.Contains(g)) allGroups.Add(g);
                    }
                    bool exactAny = names.Any(n => frags.Any(k => string.Equals(n, k, StringComparison.OrdinalIgnoreCase)));

                    // ── 选保留点：先认 JSON 清单（要求"无阻塞条件"），再按出生点组补齐 ──
                    var keepIdx = new List<int>();
                    for (int i = 0; i < names.Count; i++)
                    {
                        bool want = exactAny
                            ? frags.Any(k => string.Equals(names[i], k, StringComparison.OrdinalIgnoreCase))
                            : frags.Any(k => names[i].IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!want) continue;
                        if (tier[i] < 2)
                        {
                            Log?.Warning($"[EventRotator][撤离点] ⚠ {mk} 的保留点 '{names[i]}' 有额外通行条件"
                                       + $"（PassageRequirement={Prop(list[i], "PassageRequirement") ?? "None"}）"
                                       + " ⇒ 本图不用它当保留点（否则玩家可能撤不出去），改按出生点自动挑");
                            continue;
                        }
                        if (!keepIdx.Contains(i)) keepIdx.Add(i);
                    }
                    foreach (var g in allGroups)
                    {
                        if (keepIdx.Any(i => groupsOf[i].Contains(g))) continue;
                        // ★ 候选必须"尽量不与**已被保留点覆盖**的组重叠"：否则同一个出生点会出现 2 个可用点
                        //   （实测踩过：tarkovstreets 里 E9_sniper 覆盖全部 6 组 ⇒ 贪心"覆盖最多"会让
                        //    e1_2/e6_1 各有 2 个可用点，而 e3_4/e4_5 却没人管）⇒ 先比"重叠最少"，再比"新覆盖最多"。
                        int best = -1, bestTier = -1, bestCover = -1, bestOverlap = int.MaxValue; double bestChance = -1;
                        for (int i = 0; i < names.Count; i++)
                        {
                            if (tier[i] < 2 || keepIdx.Contains(i) || !groupsOf[i].Contains(g)) continue;
                            int overlap = groupsOf[i].Count(x => keepIdx.Any(j => groupsOf[j].Contains(x)));
                            int cover = groupsOf[i].Count(x => !keepIdx.Any(j => groupsOf[j].Contains(x)));
                            double ch = ExitChanceOf(list[i]);
                            bool better = overlap < bestOverlap
                                || (overlap == bestOverlap && cover > bestCover)
                                || (overlap == bestOverlap && cover == bestCover && tier[i] > bestTier)
                                || (overlap == bestOverlap && cover == bestCover && tier[i] == bestTier && ch > bestChance);
                            if (better) { best = i; bestTier = tier[i]; bestCover = cover; bestOverlap = overlap; bestChance = ch; }
                        }
                        if (best < 0) { noCover.Add(mk + "/" + g); continue; }
                        keepIdx.Add(best);
                        if (bestOverlap > 0)
                            Log?.Warning($"[EventRotator][撤离点] ⚠ {mk}: 出生点组 '{g}' 在「无阻塞条件」的候选里"
                                       + $"没有不与已保留点重叠的 ⇒ 只能补留 '{names[best]}'（会让 {bestOverlap} 个出生点组各有 2 个可用点）");
                        else
                            Log?.Warning($"[EventRotator][撤离点] {mk}: 出生点组 '{g}' 在 JSON 清单里没有可用保留点"
                                   + $" ⇒ 自动补留 '{names[best]}'（Chance={bestChance:0.##}，"
                                   + (bestTier >= 3 ? "无阻塞条件" : "空槽类条件") + "）");
                    }

                    // ── 写：保留 ⇒ Chance=100；其余 ⇒ Chance=0（两者都幂等 + 可还原）──
                    int z = 0, az = 0, f = 0;
                    for (int i = 0; i < names.Count; i++)
                    {
                        var ex = list[i];
                        var pc = ex.GetType().GetProperty("Chance", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (pc == null || !pc.CanWrite) continue;
                        var oldV = pc.GetValue(ex);
                        double cur; try { cur = oldV == null ? 0 : Convert.ToDouble(oldV); } catch { continue; }
                        if (keepIdx.Contains(i))
                        {
                            if (cur == 100) continue;                                 // 幂等：已经是 100
                            var capO = ex; var capP = pc; var capV = oldV;
                            AddRestore?.Invoke(() => { try { capP.SetValue(capO, capV); } catch { } });
                            pc.SetValue(ex, Coerce(100.0, pc.PropertyType));
                            f++;
                        }
                        else
                        {
                            if (cur == 0) { az++; continue; }                         // 本来就是 0（shoreline Rock Passage / streets E6）⇒ 无需改
                            var capO = ex; var capP = pc; var capV = oldV;
                            AddRestore?.Invoke(() => { try { capP.SetValue(capO, capV); } catch { } });
                            pc.SetValue(ex, Coerce(0.0, pc.PropertyType));
                            z++;
                        }
                    }

                    // ── 官方"延长撤离时间"：保留点 ExfiltrationTime ×= ExitTimeMultiplier ──
                    int sc = 0;
                    if (timeMul > 1.0001)
                    {
                        foreach (var i in keepIdx)
                        {
                            var ex = list[i];
                            var pt = ex.GetType().GetProperty("ExfiltrationTime", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                            if (pt == null || !pt.CanWrite) continue;
                            var oldT = pt.GetValue(ex); if (oldT == null) continue;
                            double t; try { t = Convert.ToDouble(oldT); } catch { continue; }
                            if (t <= 0) continue;
                            var capO = ex; var capP = pt; var capV = oldT;
                            // ★ 幂等护栏：记下"已缩放到的值"；若下次进来当前值仍等于它 ⇒ 中间没发生还原 ⇒ 跳过
                            //   （防止"没还原又跑一次"把它 ×9；正常流程 RestoreThenApply 会先还原 ⇒ 仍会正常 ×3）
                            var tk = mk + "|" + names[i];
                            double scaled = t * timeMul;
                            //   当前值 == 我们已经缩放到的那个值 ⇒ 中间没还原过 ⇒ 跳过（防 ×9）；
                            //   还原过（当前值回到原值）⇒ 照常再缩放一次 ⇒ 每局都是 ×3
                            if (_exitTimeScaled.TryGetValue(tk, out var rec) && Math.Abs(t - rec) < 0.001) continue;
                            AddRestore?.Invoke(() => { try { capP.SetValue(capO, capV); } catch { } });
                            pt.SetValue(ex, Coerce(scaled, pt.PropertyType));
                            _exitTimeScaled[tk] = scaled;
                            sc++;
                            double after; try { after = Convert.ToDouble(pt.GetValue(ex)); } catch { after = -1; }   // ★ 回读自证
                            Log?.Info($"[EventRotator][撤离点] {mk}: '{names[i]}' 撤离时间 {t:0.##}s → {after:0.##}s"
                                    + $"（×{timeMul:0.##}，官方 EventSettings.ExitTimeMultiplier；可还原）");
                        }
                        timeScaled += sc;
                    }

                    // ── 秘密撤离点：关 PMC（SCAV 不动，避免把 SCAV 困死）──
                    int so = 0;
                    var slist = Prop(b, "SecretExits") as IList;
                    if (slist != null)
                    {
                        foreach (var sx in slist)
                        {
                            var pe = sx.GetType().GetProperty("EligibleForPMC", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                            if (pe == null || !pe.CanWrite) continue;
                            var oldE = pe.GetValue(sx);
                            if (oldE is bool ob && !ob) continue;                     // 幂等
                            var capO = sx; var capP = pe; var capV = oldE;
                            AddRestore?.Invoke(() => { try { capP.SetValue(capO, capV); } catch { } });
                            pe.SetValue(sx, false);
                            so++;
                        }
                    }
                    secretOff += so;

                    // ── 逐图日志（数字与"逐图复算"一致）──
                    var keptNames = keepIdx.Select(i => $"'{names[i]}'[{string.Join("+", groupsOf[i])}]").ToList();
                    foreach (var i in keepIdx) keptAll.Add(mk + ":" + names[i]);
                    zeroed += z; alreadyZero += az; forced100 += f; mapsDone++;
                    Log?.Warning($"[EventRotator][撤离点] {mk}: {names.Count} 个撤离点 / 出生点组 {allGroups.Count} 个"
                               + $" → 保留 {keepIdx.Count} 个（{string.Join("，", keptNames)}；每个出生点各 1 个可用）"
                               + $"｜置 0 {z} 个｜本来就是 0 {az} 个"
                               + (f > 0 ? $"｜强制 Chance=100 {f} 个" : "")
                               + (so > 0 ? $"｜秘密点关 PMC {so} 个" : ""));
                }
                Log?.Warning($"[EventRotator][撤离点] ★已锁定：{mapsDone} 张图 / 置 0 共 {zeroed} 个"
                           + (alreadyZero > 0 ? $"（另有 {alreadyZero} 个本来就是 0，无需改动）" : "")
                           + $" / 保留 {keptAll.Count} 个（{string.Join("，", keptAll)}）"
                           + (forced100 > 0 ? $" / 强制 Chance=100 共 {forced100} 个" : "")
                           + (secretOff > 0 ? $" / 秘密撤离点关 PMC 共 {secretOff} 个" : "")
                           + (timeScaled > 0 ? $" / 撤离时间 ×{timeMul:0.##} 共 {timeScaled} 个" : "")
                           + "（全部可还原）");
                if (noCover.Count > 0)
                    Log?.Warning("[EventRotator][撤离点] ⚠ 有出生点组一个可用点都没选出来（该出生点会出不去）："
                               + string.Join("，", noCover));
            }
            else if (exitKeep != null && exitKeep.Count > 0 && lockMaps != null)
            {
                // ★ 声明了 exitKeepOnly，但触发时机不是 "always"：
                //   "off"    ⇒ 明确不用这个功能（静默，verbose 时一行）
                //   "ritual" ⇒ **默认**：官方语义（杀了 Zryachiy 才锁），但**服务端拿不到那个时刻** ⇒ 只能不锁
                if (exitTiming == "off")
                {
                    if (EventEngine.VerboseLogging)
                        Log?.Info("[EventRotator][撤离点] exitKeepOnly 声明了 @timing=off ⇒ 不锁任何撤离点（静默）");
                }
                else
                {
                    if (!_ritualLockWarned)
                    {
                        _ritualLockWarned = true;
                        Log?.Warning("[EventRotator][撤离点] ⚠ 本活动声明了 exitKeepOnly，但官方语义是"
                                   + "「**杀了 Zryachiy 开始仪式之后**才只留 1 个撤离点」（wiki 原文："
                                   + "Finding and killing the roaming Zryachiy will close all extractions but 1 …），"
                                   + "而**服务端拿不到「仪式开始」这个时刻** ⇒ **本 mod 什么都不锁**（打仪式前撤离点保持正常）。"
                                   + "依据：① 仪式是客户端纯本地事件链 —— `BotHalloweenEvent.OnBotRemove`（Assembly-CSharp:156627，"
                                   + "死的是 sectactPriestEvent 就 CheckOnSectantsCount）→ `StartRitual()`（:156633）→ "
                                   + "`GlobalEventsController.CreateCommonEvent<HalloweenSummonStartedEvent>().Invoke(...)`（:156640）"
                                   + "→ 本地 `HalloweenEventController.RitualStartedHandler`（:568356），全程**没有任何 HTTP/网络上报**；"
                                   + "② 撤离点客户端**只在本局开战时读一次**（`InitAllExfiltrationPoints` 仅 :668974/:671580 两个调用点，"
                                   + "数据来自开战时的 `/client/match/local/start`）⇒ 就算服务端中途知道了也改不动客户端；"
                                   + "③ SPT 侧唯一的开战通道 `/client/raid/configuration`（MatchStaticRouter.cs:42 → ConfigureOfflineRaid + "
                                   + "NullResponse）不带仪式信息，收尾包 EndRaidResult 只有 profile/killerId/killerAid/exitName/inSession/"
                                   + "favorite/playTime ⇒ 连「事后」都拿不到击杀信息。"
                                   + "　★ ★★ **已经做到了（2026-10-16）—— 客户端插件 `fix/SPT-EventRotator.Client/RitualExitLock.cs`**："
                                   + "Harmony postfix `BotHalloweenEvent.StartRitual`（:156633，主触发）+ 订阅 `HalloweenSummonStartedEvent`（备触发）"
                                   + "⇒ 调客户端自己的官方实现 `ExfiltrationController.Instance.EventDisableAllExitsExceptOne(exitName)`"
                                   + "（:521560，0.16.9 原是死代码 —— 它正是「关掉除 exitName 外所有点 + 该点 ExfiltrationTime ×3」）。"
                                   + "**保留点按玩家自己的 `Profile.Info.EntryPoint` 匹配 `EligibleEntryPoints` 选**（不写死名字）；"
                                   + "**F12 有开关**（`★仪式开始后只留 1 个撤离点`，默认开，当帧生效）。"
                                   + "⚠ **别和本项 `@timing=always` 同时开**（两边会叠加 ×3）。"
                                   + "　★ 只想手动看效果：在 events.json 的 exitKeepOnly 里加一项 \"@timing=always\"（见 audit/撤离点-触发时机.md）");
                    }
                    Log?.Warning($"[EventRotator][撤离点] 本局未锁任何撤离点（触发时机={exitTiming}；"
                               + "服务端拿不到「仪式开始」⇒ 保持官方「打仪式前撤离点正常」的语义）");
                }
            }

            // ⑦ 按图禁用转场
            if (transitDisableMaps != null && transitDisableMaps.Count > 0)
            {
                int maps = 0, n = 0;
                foreach (var mk in transitDisableMaps)
                {
                    var loc = Find(mk); if (loc == null) continue;
                    var list = Prop(Prop(loc, "Base"), "Transits") as IList;
                    if (list == null) continue;
                    bool touched = false;
                    foreach (var tr in list)
                    {
                        var pa = tr.GetType().GetProperty("Active", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (pa == null || !pa.CanWrite) continue;
                        var oldV = pa.GetValue(tr);
                        if (oldV is bool ob && !ob) continue;
                        var capO = tr; var capP = pa; var capV = oldV;
                        AddRestore?.Invoke(() => { try { capP.SetValue(capO, capV); } catch { } });
                        pa.SetValue(tr, false); n++; touched = true;
                    }
                    if (touched) maps++;
                }
                Log?.Warning($"[EventRotator][转场] ★已禁用：{maps} 张图 / {n} 条转场（location.Base.transits[].active=false，可还原）");
            }

            // ⑦b ★★ v0.1.5 第⑤条：**按 transit id 精确禁用**（只关指定的那几条，不动同图其它转场）
            if (transitDisableIds != null && transitDisableIds.Count > 0)
            {
                int maps2 = 0, n2 = 0;
                foreach (var kv in transitDisableIds)
                {
                    var mk = kv.Key;
                    var wanted = kv.Value;
                    if (string.IsNullOrEmpty(mk) || wanted == null || wanted.Count == 0) continue;
                    var loc = Find(mk); if (loc == null) { Log?.Warning($"[EventRotator][转场ID] 找不到地图 '{mk}'"); continue; }
                    var list = Prop(Prop(loc, "Base"), "Transits") as IList;
                    if (list == null) { Log?.Warning($"[EventRotator][转场ID] '{mk}' 没有 transits"); continue; }
                    bool touched = false;
                    foreach (var tr in list)
                    {
                        var idS = (Prop(tr, "Id") ?? Prop(tr, "ID"))?.ToString();
                        var nmS = Prop(tr, "Name")?.ToString();
                        bool hit = false;
                        foreach (var w in wanted)
                        {
                            var ww = w?.Trim();
                            if (string.IsNullOrEmpty(ww)) continue;
                            if (string.Equals(idS, ww, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(nmS, ww, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                        }
                        if (!hit) continue;
                        var pa = tr.GetType().GetProperty("Active", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (pa == null || !pa.CanWrite) continue;
                        var oldV = pa.GetValue(tr);
                        if (oldV is bool ob && !ob) continue;
                        var capO = tr; var capP = pa; var capV = oldV;
                        AddRestore?.Invoke(() => { try { capP.SetValue(capO, capV); } catch { } });
                        pa.SetValue(tr, false); n2++; touched = true;
                        Log?.Info($"[EventRotator][转场ID] {mk}: 已禁用转场 id={idS} name={nmS}（官方『只禁进入某图的转场』同款，可还原）");
                    }
                    if (touched) maps2++;
                }
                Log?.Warning($"[EventRotator][转场ID] ★已按 id 禁用：{maps2} 张图 / {n2} 条转场（可还原）");
            }
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][撤离点/转场] 失败: {ex.Message}"); }
    }

    // ───────────────────────── ⑧ 按 bot 类型注入装备（走数据层，绕开 PBS 拦截）─────────────────────────
    /// <summary>
    /// 按 bot 类型注入装备/战利品：写 `bots/types/&lt;type&gt;.json → inventory.equipment[槽位][tpl] = 概率`。
    /// ★ 这条同时纠正了原清单第 23 条的误判：被 PBS 拦的是"运行时注入"，**直接写数据层完全有效**
    /// （本轮已用同一路径做成圣诞老人刷新 + 礼物清单 1898 件，实测生效）。
    /// 数据形如：{"botType": {"Headwear": {"tplId": 60}, "Backpack": {"tplId": 30}}}
    /// </summary>
    /// <summary>
    /// ★★★ P1-① 修复：官方 `AddEventLootToBots` 的**专属落点** —— `BotInventory.Items`。
    ///
    /// <para><b>官方原句</b>（`_forensic_tmp/ilspy_full/SPTarkov.Server.Core.Services.Server/SeasonalEventService.cs:958-980`）：
    /// <code>
    /// // :958 AddEventLootToBots
    /// foreach (var item2 in eventBotLoot[item.Key]) {
    ///     var value = item2.Value;                      // item2.Key = 池名（Backpack/Pockets/TacticalVest）
    ///     foreach (var item3 in value)
    ///         botType.BotInventory.Items.GetAllPropertiesAsDictionary()[item3.Key] = value[item3.Key];
    ///     //  ↑★ 写的是 Items（背包里**装什么**），不是 Equipment（**背什么**背包），且**不抬概率**
    /// }
    /// </code></para>
    ///
    /// <para><b>修的是什么</b>：原来 loot 也走 <see cref="ApplyBotEquipment"/>，而那条路是
    /// `equipment` 优先、`items` 只是回退。对 `Backpack`/`Pockets`/`TacticalVest` 这三个
    /// **同时存在于两处**的槽名 ⇒ **战利品被写进了"背哪种背包"**。后果：① 圣诞战利品几乎不会进 SCAV 背包；
    /// ② 极小概率让 SCAV 试图"背一个圣诞装饰球"（非法装备）；③ 还多抬了 `EquipmentChances`（**官方 loot 路径不抬**）。</para>
    ///
    /// <para><b>结构（实测）</b>：`BotTypeInventory.Items` 的类型是
    /// <c>SPTarkov.Server.Core.Models.Eft.Common.Tables.ItemPools</c>，
    /// 它的槽位是**属性**（不是字典键）：<c>Backpack</c> / <c>Pockets</c> / <c>SecuredContainer</c> /
    /// <c>SpecialLoot</c> / <c>TacticalVest</c> —— **全都没有 `[JsonPropertyName]`**（JSON 同名）。
    /// 每个属性的值是 <c>Dictionary&lt;MongoId, double&gt;</c>（tpl → 权重）。
    /// 这就是官方 `GetAllPropertiesAsDictionary()` 的等价物。</para>
    ///
    /// <para><b>可还原</b>：写入前快照旧值进 <c>AddRestore</c>（无旧值 ⇒ 还原时删键）。
    /// <b>幂等</b>：同值重复赋值结果一致。<b>可降级</b>：逐条 try/catch。
    /// <b>⚠ 绝不触碰 `BotChances.EquipmentChances`</b>（那是 gear 路径的事）。</para>
    /// </summary>
    public static void ApplyBotItems(Dictionary<string, Dictionary<string, Dictionary<string, double>>> table)
    {
        if (table == null || table.Count == 0 || BotTableRef == null) return;
        try
        {
            var types = Prop(BotTableRef, "Types") as IDictionary;
            if (types == null) { Log?.Warning("[EventRotator][官方掉落] 拿不到 BotTable.Types（已跳过）"); return; }

            int added = 0;
            var detail = new List<string>();
            foreach (var kv in table)
            {
                object bt = null;
                foreach (DictionaryEntry de in types)
                    if (string.Equals(de.Key?.ToString(), kv.Key, StringComparison.OrdinalIgnoreCase)) { bt = de.Value; break; }
                if (bt == null) { Log?.Warning($"[EventRotator][官方掉落] BotTable.Types 里没有 '{kv.Key}'（已跳过）"); continue; }

                var inv = Prop(bt, "BotInventory") ?? Prop(bt, "Inventory") ?? Prop(bt, "BotBaseInventory");
                if (inv == null)
                {
                    Log?.Warning($"⚠ [EventRotator][官方掉落] '{kv.Key}'.BotInventory 取不到"
                               + "（试过 BotInventory / Inventory / BotBaseInventory）⇒ 本维度未生效");
                    continue;
                }
                var itemPools = Prop(inv, "Items") ?? Prop(inv, "BotItems");
                if (itemPools == null)
                {
                    Log?.Warning($"⚠ [EventRotator][官方掉落] '{kv.Key}'.BotInventory.Items 取不到（试过 Items / BotItems）⇒ 本维度未生效");
                    continue;
                }

                foreach (var slotKv in kv.Value)
                {
                    // ★ ItemPools 的槽位是**属性**（Backpack/Pockets/TacticalVest/SecuredContainer/SpecialLoot）
                    var cd = Prop(itemPools, slotKv.Key) as IDictionary;
                    if (cd == null)
                    {
                        var names = string.Join(",", itemPools.GetType()
                            .GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name));
                        Log?.Warning($"[EventRotator][官方掉落] '{kv.Key}' ItemPools 里没有槽位 '{slotKv.Key}'"
                                   + $"　｜ ItemPools 属性=[{names}]");
                        continue;
                    }
                    foreach (var itemKv in slotKv.Value)
                    {
                        try
                        {
                            var key = Coerce(itemKv.Key, cd.GetType().IsGenericType ? cd.GetType().GetGenericArguments()[0] : typeof(string));
                            var valType = cd.GetType().IsGenericType ? cd.GetType().GetGenericArguments()[1] : typeof(double);
                            var oldV = cd.Contains(key) ? cd[key] : null;
                            var capCd = cd; var capK = key; var capOld = oldV;
                            AddRestore?.Invoke(() => { try { if (capOld == null) { if (capCd.Contains(capK)) capCd.Remove(capK); } else capCd[capK] = capOld; } catch { } });
                            cd[key] = Coerce(itemKv.Value, valType);
                            // 回读自证
                            var now = cd.Contains(key) ? cd[key]?.ToString() : "?(写后读不到)";
                            added++;
                            if (detail.Count < 12) detail.Add($"{kv.Key}.Items.{slotKv.Key} += {itemKv.Key}（权重 {now}）");
                        }
                        catch (Exception ex) { Log?.Warning($"[EventRotator][官方掉落] 写入失败（已跳过）: {ex.GetType().Name}"); }
                    }
                }
            }

            Log?.Info($"[EventRotator][官方掉落] ★ 落点 = BotInventory.Items（= 官方 AddEventLootToBots 的 Items）"
                    + $"，共 {added} 件，**未抬 EquipmentChances**（官方 loot 路径不抬）");
            foreach (var d in detail) Log?.Info($"[EventRotator][官方掉落] 回读自证 {d}");
        }
        catch (Exception ex) { Log?.Error($"[EventRotator][官方掉落] 应用失败: {ex.Message}"); }
    }

    public static void ApplyBotEquipment(Dictionary<string, Dictionary<string, Dictionary<string, double>>> table,
                                         bool keepBossIconic = false)
    {
        if (table == null || table.Count == 0 || BotTableRef == null) return;
        try
        {
            var types = Prop(BotTableRef, "Types") as IDictionary;
            if (types == null) return;
            // ★【日志口径修复】`added` 原来把"已存在键的覆盖写"也算成"新增"（每局都报
            //   "共新增 59 条"，其实大部分是覆盖）⇒ 拆成 newCount / overwrittenCount。**不改实际行为**。
            int newCount = 0, overwrittenCount = 0;
            var detail = new List<string>();   // ★ v2.6.5 逐条自证用
            var chanceRaised = new List<string>();   // ★ v2.6.5 概率抬升自证（官方 Math.Max(...,75)）
            foreach (var kv in table)
            {
                object bt = null;
                foreach (DictionaryEntry de in types)
                    if (string.Equals(de.Key?.ToString(), kv.Key, StringComparison.OrdinalIgnoreCase)) { bt = de.Value; break; }
                if (bt == null) { Log?.Warning($"[EventRotator][装备注入] 找不到 bot 类型 '{kv.Key}'"); continue; }

                // ★★ v0.1.5 修正（实测 `没有 Ammo 字段` / `没有槽位` / `共新增 0 条`）：
                //   SPT 的 BotType 模型属性带前缀 —— **`BotInventory`**（DLL 实证有 `BotInventory`/`get_BotInventory`），
                //   和血量叫 `BotHealth` 是同一类问题。这里把常见命名都试一遍。
                var inv = Prop(bt, "BotInventory") ?? Prop(bt, "Inventory") ?? Prop(bt, "BotBaseInventory");
                var items = Prop(inv, "Items") ?? Prop(inv, "BotItems");
                var equip = Prop(inv, "Equipment") ?? Prop(inv, "BotEquipment");
                var ammoRoot = (Prop(inv, "Ammo") ?? Prop(inv, "BotAmmo")) as IDictionary;
                // ★★★ v2.6.5 修复：SPT 名字又绕了一层 —— `Chances` 的 CLR 属性名是 **`EquipmentChances`**
                //   （JSON 名才是 `equipment`，见 audit/ilspy_quest/.../Chances.cs:12-13）。
                //   原实现只找 `"Equipment"` ⇒ **恒为 null** ⇒ 下面的概率抬升整块被跳过
                //   ⇒ 实测 `assault` Headwear 停在 70、FaceCover 停在 47，都低于官方要求的 75，
                //     即使池子里有装备也会被概率挡掉一部分。
                var chances = Prop(Prop(bt, "BotChances"), "EquipmentChances")
                           ?? Prop(Prop(bt, "Chances"), "EquipmentChances")
                           ?? Prop(Prop(bt, "BotChances"), "Equipment")
                           ?? Prop(Prop(bt, "Chances"), "Equipment")
                           ?? Prop(Prop(bt, "BotChances"), "equipment")
                           ?? Prop(Prop(bt, "chances"), "equipment");
                if (inv == null) { Log?.Warning($"[EventRotator][装备注入] '{kv.Key}' 拿不到 BotInventory/Inventory，跳过"); continue; }
                foreach (var slotKv in kv.Value)
                {
                    // ★★★ v2.6.39【opt-in · 用户裁决】`eventGearKeepBossIconic: true` ⇒
                    //   首领/随从（`boss*`/`follower*`）的 `FaceCover`/`Headwear` 槽**整槽跳过**
                    //   （连"概率抬到 75"一起跳）⇒ 保住「Death Knight面具」这类标志性装备。
                    //   默认 `false` ⇒ 与官方逐字一致（`SeasonalEventService.cs:948` 直接赋值、不清理原条目）。
                    if (EventGearSlotFilter.Skip(kv.Key, slotKv.Key, keepBossIconic))
                    {
                        if (_iconicKeptWarned.Add(kv.Key + "|" + slotKv.Key))
                            Log?.Info($"[EventRotator][装备注入] eventGearKeepBossIconic=true ⇒ 跳过 '{kv.Key}.{slotKv.Key}'"
                                    + "（保住首领标志性装备；这是**非官方**的 opt-in 行为，默认不开）");
                        continue;
                    }
                    // ── 特殊键：Ammo:<口径> → 写 inventory.Ammo[口径][tpl] = 数量 ──
                    if (slotKv.Key.StartsWith("Ammo:", StringComparison.OrdinalIgnoreCase))
                    {
                        var cal = slotKv.Key.Substring(5);
                        if (ammoRoot == null) { Log?.Warning($"[EventRotator][装备注入] '{kv.Key}' 没有 Ammo 字段"); continue; }
                        object calD = null;
                        foreach (DictionaryEntry de in ammoRoot)
                            if (string.Equals(de.Key?.ToString(), cal, StringComparison.OrdinalIgnoreCase)) { calD = de.Value; break; }
                        if (calD is IDictionary cd2)
                            foreach (var itemKv in slotKv.Value)
                            {
                                var k2 = Coerce(itemKv.Key, cd2.GetType().IsGenericType ? cd2.GetType().GetGenericArguments()[0] : typeof(string));
                                var oldV = cd2.Contains(k2) ? cd2[k2] : null;
                                var capCd = cd2; var capK = k2; var capOld = oldV;
                                AddRestore?.Invoke(() => { try { if (capOld == null) { if (capCd.Contains(capK)) capCd.Remove(capK); } else capCd[capK] = capOld; } catch { } });
                                cd2[k2] = Coerce(itemKv.Value, cd2.GetType().IsGenericType ? cd2.GetType().GetGenericArguments()[1] : typeof(int));
                                // ★ 日志口径：oldV == null == 该键本来不存在（⇒ 真新增）；否则是覆盖写
                                if (oldV == null) newCount++; else overwrittenCount++;
                            }
                        else Log?.Warning($"[EventRotator][装备注入] '{kv.Key}' Ammo 里没有口径 '{cal}'");
                        continue;
                    }

                    // ★★★ v2.6.5 修复：槽位必须按**真实结构**取，不能用 `Prop(对象, 槽位名)` ★★★
                    //  实测（反编译 `BotTypeInventory` 实证）：
                    //     • `BotInventory.Equipment` = `Dictionary<EquipmentSlots, Dictionary<MongoId,double>>`
                    //       ⇒ 键是**枚举**，不是 string。原来的 `Prop(equip, "FaceCover")` 是按**属性名**反射，
                    //         而 FaceCover 是**字典键** ⇒ 必然返回 null ⇒ 整个注入落空
                    //         （日志症状：「'assault' 没有槽位 'FaceCover'」——尽管探测打印的键列表里明明有它）。
                    //     • `BotInventory.Items`（`ItemPools`）才是**对象**，每个槽位是它的**属性**
                    //       ⇒ 对它必须继续用 `Prop`。
                    // ── ★★★ 容器守卫（2026-10-16 线上 NRE 事故的永久防线）──────────────────────
                    //   一句话：**装备容器槽里绝不能放"没有 Grids 的物品"**，否则 SPT
                    //   `BotInventoryGenerator.cs:498 AddEmptyContainerToBot` →
                    //   `BotInventoryContainerService.cs:43 foreach (Grid grid in containerDbItem.Properties.Grids)`
                    //   会在 `Grids == null` 上 NRE ⇒ `Failed to generate bot (X)`，**整批 bot 刷不出来**。
                    //   事故实证：`eventLoot.christmas`（战利品：圣诞装饰球=Jewelry/无 Grids、酸黄瓜、信号弹）
                    //   被旧代码写进了 `Equipment.Backpack/Pockets/TacticalVest`；11:05 一局里 SPT 加权随机
                    //   抽中了装饰球 ⇒ 135 次 `Failed to generate bot (assault)`。
                    //   （loot 的正确落点已由 `ApplyBotItems` 走 `Items`；这里只加**防御性守卫**，
                    //     保证将来任何来源写错槽位时**最多被跳过 + 告警，绝不崩 bot 生成**。）
                    //   ⚠ 只对"装备容器槽 + 解析到 equipment 字典"生效 ⇒ 对 FaceCover/Headwear 等
                    //     非容器槽、以及 Items 路径**完全没影响**（既有已测成功的行为不变）。
                    object container = null;
                    bool fromEquipment = false;
                    if (equip is IDictionary ed)
                    {
                        var ekt = ed.GetType().IsGenericType ? ed.GetType().GetGenericArguments()[0] : typeof(string);
                        object ek = null;
                        try { ek = Coerce(slotKv.Key, ekt); } catch { }
                        if (ek != null && ed.Contains(ek)) { container = ed[ek]; fromEquipment = true; }
                    }
                    if (container == null) container = Prop(items, slotKv.Key);   // ItemPools：槽位是属性
                    if (container == null) container = Prop(inv, slotKv.Key);
                    if (container == null)
                    {
                        // ★ 打印真实结构（只打一次/每类型），下次日志就能看出槽位到底挂在哪一层
                        string Keys(object o)
                        {
                            if (o == null) return "null";
                            if (o is IDictionary dd) { var ks = new List<string>(); foreach (DictionaryEntry e in dd) ks.Add(e.Key?.ToString()); return string.Join(",", ks); }
                            return string.Join(",", o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name));
                        }
                        Log?.Warning($"[EventRotator][装备注入] '{kv.Key}' 没有槽位 '{slotKv.Key}'"
                                   + $"　｜ equipment 键=[{Keys(equip)}]　items 键=[{Keys(items)}]　inv 键=[{Keys(inv)}]");
                        continue;
                    }
                    var cd = container as IDictionary;
                    if (cd == null) continue;
                    foreach (var itemKv in slotKv.Value)
                    {
                        try
                        {
                            // ★ 守卫：装备容器槽 + 该 tpl 无 Grids ⇒ 跳过（否则 bot 生成必崩）
                            if (fromEquipment && _containerEquipmentSlots.Contains(slotKv.Key)
                                && !IsSafeContainerEquipmentTpl(itemKv.Key))
                            {
                                var gk = slotKv.Key + "|" + itemKv.Key;
                                if (_containerGuardWarned.Add(gk))
                                    Log?.Warning($"[EventRotator][装备注入] ⚠守卫拦下：'{kv.Key}.{slotKv.Key}' 里的 {itemKv.Key} "
                                               + "没有 Grids（不是容器）⇒ **已跳过**。"
                                               + "写进去会让 SPT `ContainerDetails..ctor` 在 `Properties.Grids` 上 NRE、"
                                               + "导致 `Failed to generate bot` 整批刷不出 bot（2026-10-16 实机事故根因）。"
                                               + "战利品请走 `officialLoot`/`botLootAdd_`（落点 = Items，不是 Equipment）。"
                                               + "（同一 tpl 只提示一次）");
                                continue;
                            }
                            var key = Coerce(itemKv.Key, cd.GetType().IsGenericType ? cd.GetType().GetGenericArguments()[0] : typeof(string));
                            var valType = cd.GetType().IsGenericType ? cd.GetType().GetGenericArguments()[1] : typeof(double);
                            var newVal = Coerce(itemKv.Value, valType);

                            // ★★★ v2.6.5 修复（实测「万圣节装备看不到」的真正原因）★★★
                            //   官方 `AddEventGearToBots` 对装备池是**直接赋值、无条件覆盖**：
                            //       botType.BotInventory.Equipment[slot][tpl] = 权重;
                            //   而本方法原先写成 `if (cd.Contains(key)) continue;` ⇒ **已存在的键直接跳过**。
                            //   实测翻车点：`bear.json` / `usec.json` 的 `inventory.equipment.Headwear` 里
                            //   **本来就存在南瓜头盔 `59ef13ca86f77445fd0e2483`，但权重是 0**
                            //   （原始数据里 `5a439435…` / `5a439576…` 等也是 0，是官方的"占位"写法）
                            //   ⇒ 被 `Contains` 命中后跳过 ⇒ 权重 0 永远修不成官方要求的 25
                            //   ⇒ 客户端按 0 权重永远抽不到 ⇒ 表现就是"万圣节装备看不到"。
                            //   ⇒ 现在按官方语义：**已存在也要覆盖**；还原动作与注入动作**一一对应**。
                            if (cd.Contains(key))
                            {
                                var capD = cd; var capK = key; var capOld = cd[key];
                                AddRestore?.Invoke(() => { try { capD[capK] = capOld; } catch { } });   // 还原原值
                                overwrittenCount++;                        // ★ 日志口径：这是"覆盖写"，不是新增
                            }
                            else
                            {
                                var capD = cd; var capK = key;
                                AddRestore?.Invoke(() => { try { if (capD.Contains(capK)) capD.Remove(capK); } catch { } });   // 收尾时移除
                                newCount++;                                // ★ 日志口径：真的新增了键
                            }
                            cd[key] = newVal;
                            // ★ v2.6.5 逐条自证：把「哪个 bot / 哪个槽 / 哪件 / 权重多少」落到日志
                            //   （此前只打「注入 N 个物品」⇒ 无法判断权重是否真的写对；
                            //     实测踩坑：权重被写成 0、槽位根本没找到，日志却都显示"注入成功"）
                            try { detail.Add($"{kv.Key}.{slotKv.Key}.{itemKv.Key}={newVal}"); } catch { }
                        }
                        catch (Exception e1) { Log?.Warning($"[EventRotator][装备注入] 写 '{kv.Key}.{slotKv.Key}.{itemKv.Key}' 失败: {e1.Message}"); continue; }

                        // ★ 概率（`BotChances.EquipmentChances`，官方语义 = `Math.Max(现值, 75)`）
                        //   官方原句：`botType.BotChances.EquipmentChances[slot] = Math.Max(...EquipmentChances[slot], 75.0);`
                        //   ⇒ **必须保证 ≥75**，否则池子里有东西也抽不到（掷骰不过）。
                        //   ★★ v2.6.5 连带修复：`EquipmentChances` 是 **`Dictionary<string,double>`**（字符串键），
                        //      而本方法原先用 `GetProperty("Headwear")` 按**属性名**查 ⇒ 恒为 null ⇒
                        //      **概率根本没被抬起来**（实测 `assault` Headwear 停在 70、FaceCover 停在 47，
                        //      都低于官方要求的 75 ⇒ 即使池子里有装备也会被概率挡掉一部分）。
                        //      ⇒ 与上面的 `Equipment` 同一个病根：**字典键被当成属性名反射**。
                        if (chances is IDictionary chanceDict)
                        {
                            var ckt = chanceDict.GetType().IsGenericType ? chanceDict.GetType().GetGenericArguments()[0] : typeof(string);
                            var cvt = chanceDict.GetType().IsGenericType ? chanceDict.GetType().GetGenericArguments()[1] : typeof(double);
                            object ck = null;
                            try { ck = Coerce(slotKv.Key, ckt); } catch { }
                            if (ck != null)
                            {
                                double oldD = 0;
                                try { if (chanceDict.Contains(ck)) oldD = Convert.ToDouble(chanceDict[ck]); } catch { }
                                double want = Math.Max(oldD, 75.0);   // ★ 官方 Math.Max(..., 75.0)
                                // ★ v2.6.5 自证：只要**读到**低于 75 就记一条（无论是否发生写入），
                                //   这样"抬升逻辑到底有没有跑到"一眼可见，不受"值是否已达标"影响。
                                if (oldD < 75.0)
                                    chanceRaised.Add($"{kv.Key}.{slotKv.Key}={oldD}→{(want > oldD ? want : oldD)}");
                                if (want > oldD)
                                {
                                    // 只在此刻才登记还原（此时 chanceDict[ck] 就是**本进程首次**的真实原值；
                                    // 已抬过的话 want == oldD，根本不会进这里 ⇒ 不会污染还原基线）
                                    var capD2 = chanceDict; var capK2 = ck; var capV2 = oldD;
                                    AddRestore?.Invoke(() => { try { capD2[capK2] = capV2; } catch { } });
                                    chanceDict[ck] = Coerce(want, cvt);
                                    chanceRaised.Add($"{kv.Key}.{slotKv.Key}:{oldD}→{want}");
                                }
                            }
                        }
                        else if (chances != null)
                        {
                            var pc = chances.GetType().GetProperty(slotKv.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                            if (pc != null && pc.CanWrite)
                            {
                                var oldC = pc.GetValue(chances);
                                double oldD = 0; try { oldD = Convert.ToDouble(oldC); } catch { }
                                double want = Math.Max(oldD, 75.0);   // ★ 官方 Math.Max(..., 75.0)
                                if (want > oldD)
                                {
                                    var capO = chances; var capP = pc; var capV = oldC;
                                    AddRestore?.Invoke(() => { try { capP.SetValue(capO, capV); } catch { } });
                                    pc.SetValue(chances, Coerce(want, pc.PropertyType));
                                }
                            }
                        }
                    }
                }
                Log?.Warning($"[EventRotator][装备注入] ★'{kv.Key}' 注入 {kv.Value.Sum(x => x.Value.Count)} 个物品（走 bots/types 数据层，可还原）");
            }
            // ★【日志口径修复】拆成「新增 / 覆盖」：旧口径把"覆盖已存在的键"也算进新增，
            //   于是每局都打「共新增 59 条」，看着像每次都在堆物品，其实大部分是改写原有权重。
            Log?.Warning($"[EventRotator][装备注入] 共新增 {newCount} 条、覆盖 {overwrittenCount} 条");
            // ★ v2.6.5：把每一条的「bot.槽位.物品=权重」打出来（默认只打前 40 条，避免刷屏）
            if (detail.Count > 0)
                Log?.Info("[EventRotator][装备注入] 明细（" + detail.Count + " 条）："
                        + string.Join(" ", detail.Take(40))
                        + (detail.Count > 40 ? $" …（还有 {detail.Count - 40} 条）" : ""));
            // ★ v2.6.5：概率抬升自证（官方 Math.Max(现值,75)；抬了才说明池子里的东西真抽得到）
            if (chanceRaised.Count > 0)
                Log?.Info("[EventRotator][装备注入] 概率抬到 ≥75：" + string.Join(" ", chanceRaised.Distinct().Take(20)));
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][装备注入] 失败: {ex.Message}"); }
    }

    // ───────────────────────── 商人万圣节头像（落点已核实，本机图片齐全）─────────────────────────
    /// <summary>
    /// 官方 `AdjustTraderIcons(eventType.Type)`（`SeasonalEventService.cs:881-894`）：
    /// 给 **8 个商人**把头像换成万圣节版，逐字 8 条：
    /// <code>
    /// httpConfig.ServerImagePathOverride["./assets/images/traders/&lt;X&gt;.png"]
    ///                              = "./assets/images/traders/halloween/&lt;X&gt;.png";
    /// </code>
    /// 8 个 &lt;X&gt; = `5a7c2ebb86f7746e324a06ab`(Mechanic) / `5ac3b86a86f77461491d1ad8`(Ragman) /
    /// `5c06531a86f7746319710e1b`(Jaeger) / `59b91ca086f77469a81232e4`(Prapor) /
    /// `59b91cab86f77469aa5343ca`(Therapist) / `59b91cb486f77469a81232e5`(Skier) /
    /// `59b91cbd86f77469aa5343cb`(Peacekeeper) / `579dc571d53a0658a154fbec`(Fence)。
    ///
    /// ── ★★ 为什么不照抄官方那条 `ServerImagePathOverride`（实测证明它是**死机制**）──────────
    ///   用 Mono.Cecil 对**全部 6 个 SPTarkov.*.dll**做 IL 引用扫描，`get_ServerImagePathOverride`
    ///   只有 9 处调用：8 处就是官方自己那个 `AdjustTraderIcons`（写字典 = 先 get 再 set），
    ///   第 9 处是 `HttpConfig.PrintMembers`（只用于打印配置）⇒ **没有任何代码读它来路由图片**。
    ///   ⇒ 官方这段在 SPT 4.1.6 里是 **no-op**，照抄等于什么都没做。
    ///
    /// ── 真正生效的机制（已核实）────────────────────────────────────────────────
    ///   · `Utils/ImageRouteImporter.cs:15-19` 在启动时把 **`./SPT_Data/images/` 整棵树**注册成路由：
    ///       `key = "/files/" + <相对 images/ 的路径去掉扩展名>` → 真实文件路径
    ///   · 磁盘上真实存在 **`SPT_Data/images/trader/`（单数！）** 与 **`SPT_Data/images/trader/halloween/`**，
    ///     两边 **各 8 个同名 PNG**（文件名 = 商人 id）
    ///   · 客户端请求的头像路径来自商人数据 `TraderBase.Avatar`
    ///     （`TraderBase.cs:60-61 [JsonPropertyName("avatar")] public string? Avatar`），
    ///     实测 = **`/files/trader/avatar/&lt;id&gt;.jpg`**（小写 `trader`、带 `/avatar/`）
    ///   · `Routers/ImageRouter.cs:20-30` 匹配时**去扩展名 + 转小写** ⇒
    ///     `/files/trader/avatar/&lt;id&gt;.jpg` → 键 `/files/trader/avatar/&lt;id&gt;` → 命中磁盘那个 `.png` ✔
    ///   ⇒ **把 `TraderBase.Avatar` 指到 `/files/trader/halloween/&lt;id&gt;.png` 即可真正换头像**，
    ///     而且它天然可还原（改回原字符串）。
    ///
    /// 幂等：已是该值就跳过。可还原：登记原 avatar。可降级：商人/字段缺失只 warning。
    /// 自证：回读 avatar 并打印。
    /// </summary>
    /// <summary>商人表诊断只打一次。</summary>
    private static bool _traderIconDiagDone;

    private static readonly (string Id, string File)[] HalloweenTraderIcons =
    {
        // 官方 8 条的逐字顺序（SeasonalEventService.cs:885-892）
        ("5a7c2ebb86f7746e324a06ab", "5a7c2ebb86f7746e324a06ab.png"),
        ("5ac3b86a86f77461491d1ad8", "5ac3b86a86f77461491d1ad8.png"),
        ("5c06531a86f7746319710e1b", "5c06531a86f7746319710e1b.png"),
        ("59b91ca086f77469a81232e4", "59b91ca086f77469a81232e4.png"),
        ("59b91cab86f77469aa5343ca", "59b91cab86f77469aa5343ca.png"),
        ("59b91cb486f77469a81232e5", "59b91cb486f77469a81232e5.png"),
        ("59b91cbd86f77469aa5343cb", "59b91cbd86f77469aa5343cb.png"),
        ("579dc571d53a0658a154fbec", "579dc571d53a0658a154fbec.png"),
    };

    /// <summary>
    /// 万圣节商人头像：把 8 个商人的 `TraderBase.Avatar` 指向 `/files/trader/halloween/&lt;id&gt;.png`。
    /// `traderTable` = `Dictionary&lt;MongoId, Trader&gt;`（Plugin 注入 `TraderAssortDimension.TraderTable`）。
    /// </summary>

    private static string LocateSptDataForIcons()
    {
        try
        {
            var dir = new System.IO.DirectoryInfo(System.IO.Path.GetFullPath(EventEngine.ModPath ?? "."));
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var c1 = System.IO.Path.Combine(dir.FullName, "SPT_Data");
                if (System.IO.Directory.Exists(c1)) return c1;
                var c2 = System.IO.Path.Combine(dir.FullName, "SPT_Runtime", "SPT_Data");
                if (System.IO.Directory.Exists(c2)) return c2;
                dir = dir.Parent;
            }
        }
        catch { }
        return null;
    }

    public static void ApplyTraderIcons(bool halloween, object traderTable)
    {
        if (!halloween) return;
        if (traderTable == null) { Log?.Warning("[EventRotator][商人头像] 拿不到 TraderTable，跳过"); return; }
        try
        {
            var traders = traderTable as IDictionary;
            if (traders == null) { Log?.Warning("[EventRotator][商人头像] TraderTable 不是字典，跳过"); return; }

            // ★ 2026-10-31【实机根因】原来按**字典 key** 找商人；实测 key 不是 24 位 id（只有 Fence 命中）。
            //   真实模型（反射实证）：`Trader` 本身没有 Id 字段，商人 id 在 `Trader.Base.Id`（MongoId）。
            //   ⇒ 改成遍历 de.Value，读 `Base.Id`（兜底 `Id` / `_id`）再匹配。
            static string TraderId(object tr) => (Prop(Prop(tr, "Base"), "Id")
                                               ?? Prop(tr, "Id") ?? Prop(tr, "_id"))?.ToString();

            if (!_traderIconDiagDone)
            {
                _traderIconDiagDone = true;
                try
                {
                    var parts = new List<string>();
                    foreach (DictionaryEntry de in traders)
                    {
                        if (parts.Count >= 10) break;
                        var v = de.Value;
                        parts.Add($"key={de.Key} id={Prop(v, "Id") ?? "(无)"} baseId={Prop(Prop(v, "Base"), "Id") ?? "(无)"}");
                    }
                    Log?.Info($"[EventRotator][商人头像][诊断] 表内 {traders.Count} 个：{string.Join("｜", parts)}");
                }
                catch (Exception ex) { Log?.Warning("[EventRotator][商人头像][诊断] 失败: " + ex.Message); }
            }

            // ★★ 2026-10-31【第二轮根因】`Base.Avatar` 里的文件名**不是商人 id**，而是"头像 image id"
            //   （例：Mechanic 的 Base.Id = 5a7c2eca46aef81a7ca2145d，但 Avatar = /files/trader/avatar/5a7c2ebb86f7746e324a06ab.jpg）。
            //   而 `images/trader/{avatar,halloween}/` 两个目录里的文件名集合**完全一致**（都是那 8 个 image id）
            //   ⇒ 正确做法：读每个商人当前的 Avatar，把 `/trader/avatar/xxx.jpg` 换成 `/trader/halloween/xxx.png`。
            //   这样不需要任何硬编码 id 表，且天然对所有商人成立（有对应万圣节图的才换）。
            var halloweenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var sptData = LocateSptDataForIcons();
                if (sptData != null)
                {
                    var dir = System.IO.Path.Combine(sptData, "images", "trader", "halloween");
                    if (System.IO.Directory.Exists(dir))
                        foreach (var f in System.IO.Directory.GetFiles(dir, "*.png"))
                            halloweenFiles.Add(System.IO.Path.GetFileNameWithoutExtension(f));
                }
            }
            catch { }

            int swapped = 0;
            if (halloweenFiles.Count > 0)
            {
                foreach (DictionaryEntry de in traders)
                {
                    var tr = de.Value;
                    var bs = Prop(tr, "Base");
                    if (bs == null) continue;
                    var cur = Prop(bs, "Avatar")?.ToString();
                    if (string.IsNullOrEmpty(cur)) continue;
                    var norm = cur.Replace((char)92, '/');
                    var name = System.IO.Path.GetFileNameWithoutExtension(norm.Split('/').Last());
                    if (!halloweenFiles.Contains(name)) continue;
                    var want2 = "/files/trader/halloween/" + name + ".png";
                    if (string.Equals(cur, want2, StringComparison.OrdinalIgnoreCase)) { swapped++; continue; }
                    var capBase2 = bs; var capOld2 = cur;
                    AddRestore?.Invoke(() => { try { SetProp(capBase2, "Avatar", capOld2); } catch { } });
                    if (SetProp(bs, "Avatar", want2))
                    {
                        swapped++;
                        Log?.Info($"[EventRotator][商人头像] {TraderId(tr)}（image id {name}）：{cur} → {want2} ✔");
                    }
                }
                Log?.Info($"★商人万圣节头像（按 Avatar 文件名推导）：{swapped}/{traders.Count} 个已有万圣节图并已切换");
            }

            int n = 0, skipped = 0, missing = 0;
            foreach (var want in HalloweenTraderIcons)
            {
                object tr = null;
                foreach (DictionaryEntry de in traders)
                {
                    var id = TraderId(de.Value);
                    if (string.Equals(id, want.Id, StringComparison.OrdinalIgnoreCase)) { tr = de.Value; break; }
                }
                if (tr == null) { missing++; continue; }

                var baseObj = Prop(tr, "Base");
                if (baseObj == null) { missing++; continue; }
                var cur = Prop(baseObj, "Avatar")?.ToString();
                var newPath = "/files/trader/halloween/" + want.File;

                if (string.Equals(cur, newPath, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }

                var capBase = baseObj; var capOld = cur;
                AddRestore?.Invoke(() => { try { SetProp(capBase, "Avatar", capOld); } catch { } });
                if (!SetProp(baseObj, "Avatar", newPath)) { missing++; continue; }
                n++;

                // 回读自证
                var back = Prop(baseObj, "Avatar")?.ToString();
                Log?.Info($"[EventRotator][商人头像] {want.Id}：{cur ?? "(空)"} → {back}"
                        + (string.Equals(back, newPath, StringComparison.OrdinalIgnoreCase) ? " ✔" : " —— ⚠ 回读不符！")
                        + "（官方 AdjustTraderIcons 万圣节 8 商人同款；可还原）");
            }
            Log?.Info($"★商人万圣节头像：改 {n} 个、已是目标值跳过 {skipped} 个、找不到 {missing} 个"
                    + "（落点 Trader.Base.Avatar → /files/trader/halloween/，走 ImageRouteImporter 已注册的静态路由）");
        }
        catch (Exception ex) { Log?.Warning("[EventRotator][商人头像] 失败（已降级）: " + ex.Message); }
    }

    // ───────────────────────── 商人万圣节头像 · 延迟补做（时刻表问题）─────────────────────────
    /// <summary>
    /// ★★★ 头像"延迟补做"的**挂起登记**（2026-10-16 实机证实的问题）。
    ///
    /// ── 根因（真服务端日志实测，不是推测）────────────────────────────────────────
    ///   同一个服务端进程内：
    ///     · **12:40:34 = mod `OnLoad` 套用** → `TradersTable` 里**只有 1 个商人（Fence）** ⇒ 头像「改 1 个、找不到 7 个」
    ///     · **12:42:55 = 用户随后 F12 换活动触发的第二次套用** → 同一张表里**已能找到 Mechanic** ⇒ `traderAssortAdd` 成功
    ///   ⇒ **SPT 的 `TradersTable` 在 `OnLoad` 时刻还没装满**，是在之后（用户交互前）才齐的。
    ///   两次干净重启（12:40:34 / 12:55:32）都是 `改 1 个、找不到 7 个`，逐字一致 ⇒ **稳定复现**。
    ///
    /// ⚠ 顺带结论：`traderAssortAdd` / `traderBarterAdd` **与头像同在一处**（`Plugin.ApplyExtraDimensions`），
    ///   所以它们**有同一时刻风险** —— 之前"测试成功"是因为用户恰好做了第二次套用（F12）。
    ///
    /// ── 本机制 ───────────────────────────────────────────────────────────────────
    ///   套用时若表没装满 ⇒ 不硬撑，登记 <see cref="WantTraderIcons"/>；
    ///   由 `Patches/TraderIconPatch` 挂在 **`TraderHelper.GetAllTraders` 的 prefix** 上
    ///   （= `client/trading/api/traderSettings` 的**唯一**构建点，`TraderCallbacks.cs:32-35`）
    ///   在那时补做 —— 那一刻意客户端**正要拿头像**，且表**必然已装满**。
    ///   幂等：全 8 个都已是目标值就立刻返回（一次 bool 比较）。
    ///   可降级：表为 null / 调用抛异常 ⇒ 只 warning，绝不抛。
    /// </summary>
    public static bool WantTraderIcons;
    /// <summary>延迟补做用的商人表（由 Plugin 在套用时注入）。</summary>
    public static object TraderIconTableRef;
    /// <summary>只警告一次，避免 traderSettings 被反复请求时刷屏。</summary>
    private static bool _traderIconsWarned;

    /// <summary>还原/换活动时清掉挂起状态（与 `ResetRecipeScaled` 同位置调用）。</summary>
    public static void ResetTraderIconPending()
    {
        WantTraderIcons = false;
        TraderIconTableRef = null;
        _traderIconsWarned = false;
    }

    /// <summary>
    /// 延迟补做入口（由 `Patches/TraderIconPatch` 在 `TraderHelper.GetAllTraders` 前调用）。
    /// 只有"确实需要且还没做到位"时才真正干活 ⇒ 平时就是一次 bool 检查，开销可忽略。
    /// </summary>
    public static void ReapplyTraderIconsIfNeeded()
    {
        if (!WantTraderIcons) return;
        try
        {
            var table = TraderIconTableRef;
            if (table == null)
            {
                if (!_traderIconsWarned)
                {
                    _traderIconsWarned = true;
                    Log?.Warning("[EventRotator][商人头像] 延迟补做时拿不到商人表 ⇒ 本次头像未生效（不再重复提示）");
                }
                return;
            }
            // 幂等快检：8 个全都已是目标值 ⇒ 直接返回（不重复写、不重复登记还原）
            if (AllIconsAlreadySet(table)) return;

            // 复用**同一个** ApplyTraderIcons（它本身幂等 + 登记还原 + 回读自证）
            ApplyTraderIcons(true, table);
        }
        catch (Exception ex)
        {
            if (!_traderIconsWarned)
            {
                _traderIconsWarned = true;
                Log?.Warning("[EventRotator][商人头像] 延迟补做失败（已降级，不影响服务端，不再重复提示）: " + ex.Message);
            }
        }
    }

    /// <summary>8 个万圣节头像是否**全部**已指向目标路径（幂等快检）。</summary>
    private static bool AllIconsAlreadySet(object traderTable)
    {
        if (!(traderTable is IDictionary traders)) return false;
        int ok = 0;
        foreach (var want in HalloweenTraderIcons)
        {
            bool hit = false;
            foreach (DictionaryEntry de in traders)
            {
                if (!string.Equals(de.Key?.ToString(), want.Id, StringComparison.OrdinalIgnoreCase)) continue;
                var av = Prop(Prop(de.Value, "Base"), "Avatar")?.ToString();
                hit = string.Equals(av, "/files/trader/halloween/" + want.File, StringComparison.OrdinalIgnoreCase);
                break;
            }
            if (hit) ok++;
        }
        return ok == HalloweenTraderIcons.Length;
    }


    // ───────────────────────── 藏身处配方覆写（按配方 id）─────────────────────────
    /// <summary>
    /// ★★★ **按配方 id 覆写藏身处配方**（活动字段 `hideoutRecipeOverride`）。
    ///
    /// 用途（两处都靠它）：
    ///   · 官方 Heatwave（2021-07-29）：「**Reduced efficiency of the Hideout module Water collector**」
    ///     ⇒ 集水器（`areaType=6`，`hideout/production.json` 里 4 条配方）产量/耗时按倍率调整。
    ///   · 官方「啤酒制作 30h 且只出 1 瓶」⇒ 直接给啤酒那条配方设 `productionTime`/`count`。
    ///
    /// 为什么不能用现成的 `hideoutCraftSeconds`：`HideoutTraderDimension.cs:150-165` 的
    ///   `ClampCeiling` 是**全局上限**（遍历所有配方取 min）⇒ 既不能只改一条、也只能降不能升。
    ///
    /// 落点：`HideoutTable.Production.Recipes`（`Models.Spt.Tables/HideoutProductionData.cs:12-13`）
    ///       里按 `_id` 找条目，改 `ProductionTime`（`:59-60`）/ `Count`（`:152-153`）。
    /// `areaTypeFilter`（可选）：只处理 `AreaType == 该值` 的配方；传 0/负数 = 不过滤。
    ///   · 集水器 = `HideoutAreas.WaterCollector` = **6**（`areas.json` type 实测 6 ↔ locale `hideout_area_6_name = "Water Collector"`）。
    /// `efficiency`（可选，&gt;0 且 ≠1 时生效）：**把命中配方的 `ProductionTime` 按 `1/efficiency` 缩放**。
    ///   &lt;1 = 效率降低（更慢），&gt;1 = 效率提高（更快）。
    /// 幂等：已是目标值则跳过；`efficiency` 走 `_recipeScaled` 集合防重复累积（换活动时由
    ///   <see cref="ResetRecipeScaled"/> 清零）。可还原：每个被改字段登记原值。
    /// 自证：回读并打印新值。
    /// </summary>
    public static void ApplyRecipeOverride(Dictionary<string, HideoutRecipeOverrideDef> defs,
                                           object hideoutTable,
                                           double? efficiency = null,
                                           int areaTypeFilter = -1)
    {
        bool hasDefs = defs != null && defs.Count > 0;
        bool hasEff = efficiency.HasValue && efficiency.Value > 0 && Math.Abs(efficiency.Value - 1.0) > 1e-9;
        if (!hasDefs && !hasEff) return;
        try
        {
            var prod = Prop(hideoutTable, "Production");
            var recipes = Prop(prod, "Recipes") as IList;
            if (recipes == null)
            {
                Log?.Warning("[EventRotator][藏身处配方] 拿不到 HideoutTable.Production.Recipes，跳过");
                return;
            }

            int n = 0, skipped = 0, missing = 0;

            // ── ① 按 id 精确覆写（啤酒 30h/1 瓶走这条）──
            if (hasDefs)
            {
                foreach (var kv in defs)
                {
                    var want = kv.Value;
                    if (string.IsNullOrWhiteSpace(kv.Key) || want == null) continue;

                    object hit = null;
                    foreach (var r in recipes)
                    {
                        var id = Prop(r, "Id")?.ToString() ?? Prop(r, "_id")?.ToString();
                        if (string.Equals(id, kv.Key, StringComparison.OrdinalIgnoreCase)) { hit = r; break; }
                    }
                    if (hit == null) { missing++; Log?.Warning($"[EventRotator][藏身处配方] 找不到配方 id '{kv.Key}'，跳过"); continue; }

                    var ep = Prop(hit, "EndProduct")?.ToString();

                    // productionTime
                    if (want.ProductionTime.HasValue)
                    {
                        double old = NumOf(Prop(hit, "ProductionTime"));
                        if (Math.Abs(old - want.ProductionTime.Value) < 1e-9) skipped++;
                        else
                        {
                            var cap = hit; var capOld = old;
                            AddRestore?.Invoke(() => { try { SetProp(cap, "ProductionTime", capOld); } catch { } });
                            if (SetProp(hit, "ProductionTime", want.ProductionTime.Value))
                            {
                                n++;
                                Log?.Info($"[EventRotator][藏身处配方] {kv.Key}（产物 {ep}）productionTime：{old} → "
                                        + $"{NumOf(Prop(hit, "ProductionTime"))} 秒（回读 ✔，可还原）");
                            }
                            else { missing++; Log?.Warning($"[EventRotator][藏身处配方] {kv.Key} 写 productionTime 失败"); }
                        }
                    }

                    // count
                    if (want.Count.HasValue)
                    {
                        int oldC = (int)NumOf(Prop(hit, "Count"));
                        if (oldC == want.Count.Value) skipped++;
                        else
                        {
                            var cap = hit; var capOld = oldC;
                            AddRestore?.Invoke(() => { try { SetProp(cap, "Count", capOld); } catch { } });
                            if (SetProp(hit, "Count", want.Count.Value))
                            {
                                n++;
                                Log?.Info($"[EventRotator][藏身处配方] {kv.Key}（产物 {ep}）count：{oldC} → "
                                        + $"{(int)NumOf(Prop(hit, "Count"))} 件（回读 ✔，可还原）");
                            }
                            else { missing++; Log?.Warning($"[EventRotator][藏身处配方] {kv.Key} 写 count 失败"); }
                        }
                    }
                }
            }

            // ── ② 按 areaType 全体缩放 productionTime（集水器效率走这条）──
            int scaled = 0;
            if (hasEff)
            {
                double k = 1.0 / efficiency.Value;              // eff<1 ⇒ 时间变长（更慢）
                int scan = 0;
                foreach (var r in recipes)
                {
                    if (areaTypeFilter >= 0)
                    {
                        var at = Prop(r, "AreaType");
                        int ati = -1; try { ati = Convert.ToInt32(at); } catch { }
                        if (ati != areaTypeFilter) continue;
                    }
                    scan++;
                    var cur = Prop(r, "ProductionTime");
                    if (cur == null) continue;
                    double old; try { old = Convert.ToDouble(cur); } catch { continue; }
                    double nv = old * k;

                    var idemKey = (Prop(r, "Id")?.ToString() ?? "") + "|time";
                    if (_recipeScaled.Contains(idemKey)) continue;

                    var cap = r;
                    AddRestore?.Invoke(() => { try { SetProp(cap, "ProductionTime", old); } catch { } });
                    if (SetProp(r, "ProductionTime", nv))
                    {
                        _recipeScaled.Add(idemKey);
                        scaled++;
                        Log?.Info($"[EventRotator][藏身处配方] areaType={areaTypeFilter} 配方 {Prop(r, "Id")}"
                                + $"（产物 {Prop(r, "EndProduct")}）productionTime：{old} → "
                                + $"{NumOf(Prop(r, "ProductionTime"))} 秒（效率 ×{efficiency.Value}；回读 ✔，可还原）");
                    }
                }
                Log?.Info($"★集水器效率：areaType={areaTypeFilter} 命中 {scan} 条配方、缩放 {scaled} 条"
                        + $"（效率 ×{efficiency.Value} ⇒ 耗时 ×{k:0.###}；官方 Heatwave「Reduced efficiency of the "
                        + "Hideout module Water collector」；可还原）");
            }

            Log?.Info($"★藏身处配方覆写：改 {n} 个字段、幂等跳过 {skipped} 个、未找到 {missing} 个"
                    + (hasEff ? $"，另按 areaType 缩放 {scaled} 条" : "")
                    + "（落点 HideoutTable.Production.Recipes[].ProductionTime/Count）");
        }
        catch (Exception ex) { Log?.Warning("[EventRotator][藏身处配方] 失败（已降级）: " + ex.Message); }
    }

    /// <summary>防 `efficiency` 倍率重复累积的幂等表（还原后由 <see cref="ResetRecipeScaled"/> 清空）。</summary>
    private static readonly HashSet<string> _recipeScaled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>还原之后调用：清空配方缩放幂等表，使换活动时新倍率能重新生效。</summary>
    public static void ResetRecipeScaled() { _recipeScaled.Clear(); }

    private static double NumOf(object o)
    {
        try { return o == null ? 0 : Convert.ToDouble(o); } catch { return 0; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ★★★ v0.1.5 第 1 组补齐（用户要求：按官方组合表逐项补；僵尸相关的**不补**）
    //  依据：SPT 反编译 `SeasonalEventService`（audit/ilspy2/…SeasonalEventService.decompiled.cs）
    //  ═══════════════════════════════════════════════════════════════════════

    private const string TPL_PUMPKIN = "634959225289190e5e773b3b";                    // pumpkin_rand_loot_container
    private const string TPL_BITCOIN = "59faff1d86f7746c51718c9c";                    // item_barter_valuable_bitcoin
    private const string BUFF_HALLOWEEN = "Buffs_Halloween";

    /// <summary>
    /// ⑨ 南瓜（官方 `AddPumpkinsToScavBackpacks()`，逐字）：
    ///   `botTable.Types["assault"].BotInventory.Items.Backpack[ItemTpl.RANDOMLOOTCONTAINER_PUMPKIN_RAND_LOOT_CONTAINER] = 400.0;`
    /// 即：只给 **assault** 这一种 bot 的背包加南瓜容器、权重 400。万圣节与愚人节都用它。
    /// </summary>
    public static void ApplyPumpkinBackpack(bool on)
    {
        if (!on || BotTableRef == null) return;
        try
        {
            var types = Prop(BotTableRef, "Types");
            if (types == null) { Log?.Warning("[EventRotator][南瓜] 拿不到 botTable.Types"); return; }
            object assault = null;
            foreach (var kv in Pairs(types))
                if (string.Equals(kv.Key?.ToString(), "assault", StringComparison.OrdinalIgnoreCase)) { assault = kv.Value; break; }
            if (assault == null) { Log?.Warning("[EventRotator][南瓜] 找不到 bot 类型 'assault'"); return; }

            var inv = Prop(assault, "BotInventory") ?? Prop(assault, "Inventory");
            var items = Prop(inv, "Items");
            var backpack = Prop(items, "Backpack");
            if (backpack == null) { Log?.Warning("[EventRotator][南瓜] assault 没有 inventory.items.Backpack"); return; }

            var dict = backpack as IDictionary;
            if (dict == null) { Log?.Warning($"[EventRotator][南瓜] Backpack 不是字典（{backpack.GetType().Name}）"); return; }
            var keyType = dict.GetType().IsGenericType ? dict.GetType().GetGenericArguments()[0] : typeof(string);
            var key = Coerce(TPL_PUMPKIN, keyType);
            var had = dict.Contains(key);
            var old = had ? dict[key] : null;
            AddRestore?.Invoke(() => { try { if (had) dict[key] = old; else dict.Remove(key); } catch { } });
            dict[key] = 400.0;
            Log?.Info($"[EventRotator][南瓜] assault 背包已加南瓜容器 {TPL_PUMPKIN} = 400"
                    + $"（官方 AddPumpkinsToScavBackpacks 逐字同款；可还原；键类型 {keyType.Name}）");
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][南瓜] 失败: {ex.Message}"); }
    }

    /// <summary>
    /// ⑩ 比特币改名（官方 `RenameBitcoin()`）：
    ///   `locale["en"]["{bitcoin} Name"] = "Physical SPT Coin"; locale["en"]["{bitcoin} ShortName"] = "0.2SPT";`
    /// 官方只改 **en**；我们额外也改 **ch**（否则中文客户端看不到这个梗）—— 已在日志里注明。
    /// 用官方同款 `LazyLoad&lt;GlobalLocaleDictionary&gt;.AddTransformer(...)`（SPT 自己也这么改文案）。
    /// </summary>
    public static void ApplyRenameBitcoin(bool on)
    {
        // ★★ 修正：locale 表（含 Global 字典）与 globals 表**不是同一个对象** ——
        //   第一版误把 ExtraDimensions.GlobalTableRef 传进来 ⇒ 实测日志「拿不到 localeTable.Global」✗。
        if (!on || LocaleTableRef == null) { Log?.Warning("[EventRotator][比特币] 没有 locale 表引用"); return; }
        try
        {
            var global = Prop(LocaleTableRef, "Global");
            if (global == null) { Log?.Warning("[EventRotator][比特币] 拿不到 localeTable.Global"); return; }
            int done = 0;
            foreach (var kv in Pairs(global))
            {
                var lang = kv.Key?.ToString();
                if (!string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(lang, "ch", StringComparison.OrdinalIgnoreCase)) continue;
                var lazy = kv.Value;
                if (lazy == null) continue;
                var addT = lazy.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                .FirstOrDefault(m => m.Name == "AddTransformer" && m.GetParameters().Length == 1);
                if (addT == null) { Log?.Warning($"[EventRotator][比特币] {lang} 的 LazyLoad 没有 AddTransformer"); continue; }
                var funcType = addT.GetParameters()[0].ParameterType;
                var argType = funcType.IsGenericType ? funcType.GetGenericArguments()[0] : null;
                if (argType == null) continue;
                var mi = typeof(ExtraDimensions).GetMethod(nameof(BitcoinTransformer),
                            BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(argType);
                var del = Delegate.CreateDelegate(funcType, mi);
                addT.Invoke(lazy, new object[] { del });
                done++;
            }
            Log?.Info($"[EventRotator][比特币] 已挂 {done} 个语种的改名转换器（en 为官方行为；ch 为便于你肉眼看到）"
                    + $"：{TPL_BITCOIN} Name=\"Physical SPT Coin\" / ShortName=\"0.2SPT\"");
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][比特币] 失败: {ex.Message}"); }
    }

    /// <summary>供 `AddTransformer` 使用的转换体（与官方 RenameBitcoin 的 lambda 同语义）。</summary>
    private static T BitcoinTransformer<T>(T data)
    {
        try
        {
            if (data is IDictionary d)
            {
                d[TPL_BITCOIN + " Name"] = "Physical SPT Coin";
                d[TPL_BITCOIN + " ShortName"] = "0.2SPT";
            }
        }
        catch { }
        return data;
    }

    /// <summary>
    /// ⑪ 万圣节兴奋剂 Buff（官方 `ApplyHalloweenEvent` 第 3 行）：
    ///   `globalConfig.Health.ProfileHealthSettings.DefaultStimulatorBuff = "Buffs_Halloween";`
    /// </summary>
    public static void ApplyHalloweenStimBuff(bool on)
    {
        if (!on || GlobalTableRef == null) return;
        try
        {
            var cfg = Prop(GlobalTableRef, "Configuration");
            var health = Prop(cfg, "Health");
            var phs = Prop(health, "ProfileHealthSettings");
            if (phs == null) { Log?.Warning("[EventRotator][兴奋剂Buff] 拿不到 config.Health.ProfileHealthSettings"); return; }
            var old = Prop(phs, "DefaultStimulatorBuff");
            var cap = phs;
            AddRestore?.Invoke(() => { try { SetProp(cap, "DefaultStimulatorBuff", old); } catch { } });
            SetProp(phs, "DefaultStimulatorBuff", BUFF_HALLOWEEN);
            Log?.Info($"[EventRotator][兴奋剂Buff] DefaultStimulatorBuff: {(old ?? "(空)")} → {BUFF_HALLOWEEN}"
                    + "（官方 ApplyHalloweenEvent 逐字同款；可还原）");
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][兴奋剂Buff] 失败: {ex.Message}"); }
    }

    /// <summary>
    /// ⑫ Zryachiy 近战率（官方 `AdjustZryachiyMeleeChance()`，逐字）：
    ///   `botTable.Types["bosszryachiy"].BotChances.EquipmentChances["Scabbard"] = 100.0;`
    /// 实测本机 `bosszryachiy.json` 确有 `Scabbard` 槽（当前值 0）⇒ 官方写法可直接用。
    /// </summary>
    public static void ApplyZryachiyMeleeChance(bool on)
    {
        if (!on || BotTableRef == null) return;
        try
        {
            var types = Prop(BotTableRef, "Types");
            if (types == null) return;
            object zry = null;
            foreach (var kv in Pairs(types))
                if (string.Equals(kv.Key?.ToString(), "bosszryachiy", StringComparison.OrdinalIgnoreCase)) { zry = kv.Value; break; }
            if (zry == null) { Log?.Warning("[EventRotator][Zryachiy近战] 找不到 bot 类型 'bosszryachiy'"); return; }
            var chances = Prop(zry, "BotChances");
            var eq = Prop(chances, "EquipmentChances");
            if (eq == null) { Log?.Warning("[EventRotator][Zryachiy近战] bosszryachiy 没有 BotChances.EquipmentChances"); return; }
            var dict = eq as IDictionary;
            if (dict == null) { Log?.Warning("[EventRotator][Zryachiy近战] EquipmentChances 不是字典"); return; }
            var keyType = dict.GetType().IsGenericType ? dict.GetType().GetGenericArguments()[0] : typeof(string);
            var key = Coerce("Scabbard", keyType);
            if (!dict.Contains(key)) { Log?.Warning("[EventRotator][Zryachiy近战] 该类型没有 Scabbard 槽位（官方写法只替换已存在的槽）"); return; }
            var old = dict[key];
            AddRestore?.Invoke(() => { try { dict[key] = old; } catch { } });
            dict[key] = 100.0;
            Log?.Info($"[EventRotator][Zryachiy近战] bosszryachiy 的 Scabbard 概率 {old} → 100"
                    + "（官方 AdjustZryachiyMeleeChance 逐字同款；可还原）");
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][Zryachiy近战] 失败: {ex.Message}"); }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ★★★ v0.1.5 第 2~4 组：直接消费官方"组合表"数据（seasonalEventConfig）
    //  ═══════════════════════════════════════════════════════════════════════

    /// <summary>把官方 `eventGear[key]` / `eventLoot[key]`（形状 {botType:{槽位:{tpl:数值}}}）转成装备注入表。</summary>
    /// <summary>
    /// ★ v2.6.6：把官方 `eventGear` / `eventLoot` 表读成通用结构，供 **PBS 兼容层**复用。
    /// （`ReadSlotTable` 的实现原样暴露，避免两份读表逻辑漂移。）
    /// </summary>
    public static Dictionary<string, Dictionary<string, Dictionary<string, double>>> ReadSlotTableForCompat(
        object seasonalCfg, string rootName, List<string> keys)
    {
        var outT = new Dictionary<string, Dictionary<string, Dictionary<string, double>>>(StringComparer.OrdinalIgnoreCase);
        if (keys == null || seasonalCfg == null) return outT;
        foreach (var key in keys)
        {
            var t = ReadSlotTable(seasonalCfg, rootName, key);
            foreach (var botKv in t)
            {
                if (!outT.TryGetValue(botKv.Key, out var slots))
                    outT[botKv.Key] = slots = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
                foreach (var slotKv in botKv.Value)
                    slots[slotKv.Key] = slotKv.Value;
            }
        }
        return outT;
    }

    private static Dictionary<string, Dictionary<string, Dictionary<string, double>>> ReadSlotTable(object seasonalCfg, string rootName, string key)
    {
        var outT = new Dictionary<string, Dictionary<string, Dictionary<string, double>>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var root = Prop(seasonalCfg, rootName);
            if (root == null) { Log?.Warning("[EventRotator][官方组合表] 拿不到 " + rootName); return outT; }
            object group = null;
            foreach (var kv in Pairs(root))
                if (string.Equals(kv.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase)) { group = kv.Value; break; }
            if (group == null) { Log?.Warning("[EventRotator][官方组合表] " + rootName + " 里没有 '" + key + "'"); return outT; }
            foreach (var botKv in Pairs(group))
            {
                var botType = botKv.Key?.ToString();
                if (string.IsNullOrEmpty(botType)) continue;
                var slots = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
                foreach (var slotKv in Pairs(botKv.Value))
                {
                    var slot = slotKv.Key?.ToString();
                    if (string.IsNullOrEmpty(slot)) continue;
                    var tpls = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tplKv in Pairs(slotKv.Value))
                    {
                        var tpl = tplKv.Key?.ToString();
                        if (string.IsNullOrEmpty(tpl)) continue;
                        try { tpls[tpl] = Convert.ToDouble(tplKv.Value); } catch { }
                    }
                    if (tpls.Count > 0) slots[slot] = tpls;
                }
                if (slots.Count > 0) outT[botType] = slots;
            }
        }
        catch (Exception ex) { Log?.Warning("[EventRotator][官方组合表] 读 " + rootName + "/" + key + " 失败: " + ex.Message); }
        return outT;
    }

    /// <summary>
    /// ★★ v0.1.5（对标 SVM `CreateBasicBossWave` + `TagillaInterchange` 的实证）：
    /// **把某图的某个 boss 的随从类型改掉** —— `{地图: {bossName: "随从类型"}}`。
    /// 典型用法：立交桥 Killa 的随从改成 `bossTagilla`（= 大锤一定陪在他身边，官方"兄弟团聚"语义）。
    /// 只改**已存在**的条目（不存在就记 warning，不新建 —— 新建由 bossSpawns 追加路径负责）。
    /// </summary>
    public static void ApplyEscortType(Dictionary<string, Dictionary<string, string>> table)
    {
        if (table == null || table.Count == 0 || LocationTableRef == null) return;
        try
        {
            var getDict = LocationTableRef.GetType().GetMethod("GetDictionary", Type.EmptyTypes);
            object dictObj = null;
            if (getDict != null) { try { dictObj = getDict.Invoke(LocationTableRef, null); } catch { } }
            var dict = dictObj as IDictionary;
            if (dict == null) { Log?.Warning("[EventRotator][随从类型] 拿不到地图字典"); return; }
            int n = 0;
            foreach (var kv in table)
            {
                object loc = null;
                foreach (DictionaryEntry de in dict)
                    if (string.Equals(de.Key?.ToString(), kv.Key, StringComparison.OrdinalIgnoreCase)) { loc = de.Value; break; }
                if (loc == null) { Log?.Warning($"[EventRotator][随从类型] 地图未找到: {kv.Key}"); continue; }
                var list = Prop(Prop(loc, "Base"), "BossLocationSpawn") as IList;
                if (list == null) continue;
                foreach (var wanted in kv.Value)
                {
                    object hit = null;
                    foreach (var e in list)
                        if (string.Equals(Prop(e, "BossName")?.ToString(), wanted.Key, StringComparison.OrdinalIgnoreCase)) { hit = e; break; }
                    if (hit == null)
                    {
                        // ★ v1.1.3：这句原来是 Warning，读起来像出错，其实只是**正常时序**：
                        //   「随从类型」在活动套用阶段跑，而 boss 条目要到**进图那一刻**才由 bossSpawns 追加
                        //   ⇒ 活动开着就一定先走到这里。改成 Info，并把话说清楚。
                        Log?.Info($"[EventRotator][随从类型] {kv.Key}：boss 条目尚未追加（正常预检），"
                                + $"进图时会由 bossSpawns 追加 '{wanted.Key}' 后再套随从。");
                        continue;
                    }
                    var oldType = Prop(hit, "BossEscortType");
                    var oldAmt0 = Prop(hit, "BossEscortAmount");
                    var cap = hit;
                    AddRestore?.Invoke(() => { try { SetProp(cap, "BossEscortType", oldType); SetProp(cap, "BossEscortAmount", oldAmt0); } catch { } });
                    SetProp(hit, "BossEscortType", wanted.Value);
                    // ★ 值里含逗号 = **多种随从**（官方"每种一名"）：同时把数量设成 N 个 1（"1,1,1"）。
                    //   SPT 的 BossEscortType/BossEscortAmount 都是字符串，本体/SVM 数据里就有 "0,2,2,2,1,1,1,1,1,0" 这种逗号列表。
                    var parts = wanted.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1)
                    {
                        SetProp(hit, "BossEscortAmount", string.Join(",", Enumerable.Repeat("1", parts.Length)));
                        Log?.Info($"[EventRotator][随从类型] {kv.Key}: {wanted.Key} 设为 **每种一名**（{parts.Length} 类 ×1 ⇒ BossEscortAmount=1,1,1）");
                    }
                    // 有随从类型就得有数量，否则可能是 0 ⇒ 官方/SVM 用 "1"
                    if (Prop(hit, "BossEscortAmount") is string amt && (amt == "0" || string.IsNullOrWhiteSpace(amt)))
                        SetProp(hit, "BossEscortAmount", "1");
                    n++;
                    Log?.Info($"[EventRotator][随从类型] {kv.Key}: {wanted.Key} 的随从 {oldType ?? "(空)"} → {wanted.Value}"
                            + $"（SVM TagillaInterchange 同款做法：保证两人贴在一起；可还原）");
                }
            }
            Log?.Info($"[EventRotator][随从类型] 共改 {n} 条（可还原）");
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][随从类型] 失败: {ex.Message}"); }
    }

    /// <summary>⑬ 官方 `AddEventGearToBots`：读 `eventGear[key]` 并逐键经装备注入落地。</summary>
    public static void ApplyOfficialGear(List<string> keys, object seasonalCfg, bool keepBossIconic = false)
    {
        if (keys == null || keys.Count == 0 || seasonalCfg == null) return;
        foreach (var key in keys)
        {
            var table = ReadSlotTable(seasonalCfg, "EventGear", key);
            if (table.Count == 0) continue;
            int n = table.Sum(x => x.Value.Sum(y => y.Value.Count));
            Log?.Info("[EventRotator][官方装备] eventGear." + key + "：" + table.Count + " 种 bot / " + n + " 件（官方 AddEventGearToBots 同源数据）"
                    + (keepBossIconic ? "｜★ 已开 eventGearKeepBossIconic：首领/随从的 FaceCover/Headwear 会跳过（非官方 opt-in）" : ""));
            ApplyBotEquipment(table, keepBossIconic);
        }
    }

    /// <summary>⑭ 官方 `AddEventLootToBots`：读 `eventLoot[key]`（落点 inventory.items）。</summary>
    public static void ApplyOfficialLoot(List<string> keys, object seasonalCfg)
    {
        if (keys == null || keys.Count == 0 || seasonalCfg == null) return;
        foreach (var key in keys)
        {
            var table = ReadSlotTable(seasonalCfg, "EventLoot", key);
            if (table.Count == 0) continue;
            int n = table.Sum(x => x.Value.Sum(y => y.Value.Count));
            Log?.Info("[EventRotator][官方掉落] eventLoot." + key + "：" + table.Count + " 种 bot / " + n + " 件（官方 AddEventLootToBots 同源数据）");
            ApplyBotItems(table);       // ★ P1-① 修复：走官方落点 = BotInventory.Items（不是 equipment！）
        }
    }

    /// <summary>⑮ 官方 `AdjustBotAppearanceValues`：读 `botAppearanceChanges[key]` 写进 bot 类型 `appearance` 段。</summary>
    public static void ApplyBotAppearance(List<string> keys, object seasonalCfg)
    {
        if (keys == null || keys.Count == 0 || seasonalCfg == null || BotTableRef == null) return;
        try
        {
            var root = Prop(seasonalCfg, "BotAppearanceChanges");
            var types = Prop(BotTableRef, "Types") as IDictionary;
            if (root == null || types == null) { Log?.Warning("[EventRotator][官方外观] 拿不到 BotAppearanceChanges 或 BotTable.Types"); return; }
            foreach (var key in keys)
            {
                object group = null;
                foreach (var kv in Pairs(root))
                    if (string.Equals(kv.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase)) { group = kv.Value; break; }
                if (group == null) { Log?.Warning("[EventRotator][官方外观] BotAppearanceChanges 里没有 '" + key + "'"); continue; }
                int n = 0;
                foreach (var botKv in Pairs(group))
                {
                    var botType = botKv.Key?.ToString();
                    object bt = null;
                    foreach (DictionaryEntry de in types)
                        if (string.Equals(de.Key?.ToString(), botType, StringComparison.OrdinalIgnoreCase)) { bt = de.Value; break; }
                    if (bt == null) { Log?.Warning("[EventRotator][官方外观] 找不到 bot 类型 '" + botType + "'"); continue; }
                    // ★ SPT 前缀陷阱：JSON 里的 `appearance` 在 CLR 模型里是 **`BotAppearance`**
                    //   （与 health→BotHealth、inventory→BotInventory 同一套路）—— 实测第一版只查
                    //   `appearance` 会报「'bear' 没有 appearance 段」✗。
                    var app = Prop(bt, "appearance") ?? Prop(bt, "BotAppearance") ?? Prop(bt, "Appearance");
                    if (app == null)
                    {
                        var names = bt.GetType().GetProperties().Select(x => x.Name).Take(14);
                        Log?.Warning("[EventRotator][官方外观] '" + botType + "' 没有 appearance/BotAppearance 段；该类型成员示例: " + string.Join(",", names));
                        continue;
                    }
                    foreach (var partKv in Pairs(botKv.Value))
                    {
                        var part = partKv.Key?.ToString();
                        var partDict = Prop(app, part) as IDictionary;
                        if (partDict == null) { Log?.Warning("[EventRotator][官方外观] '" + botType + ".appearance." + part + "' 不是字典"); continue; }
                        var keyType = partDict.GetType().IsGenericType ? partDict.GetType().GetGenericArguments()[0] : typeof(string);
                        var valType = partDict.GetType().IsGenericType ? partDict.GetType().GetGenericArguments()[1] : typeof(double);
                        foreach (var tplKv in Pairs(partKv.Value))
                        {
                            var k2 = Coerce(tplKv.Key?.ToString(), keyType);
                            var had = partDict.Contains(k2);
                            var old = had ? partDict[k2] : null;
                            var capD = partDict; var capK = k2;
                            AddRestore?.Invoke(() => { try { if (had) capD[capK] = old; else capD.Remove(capK); } catch { } });
                            try { partDict[k2] = Coerce(tplKv.Value, valType); n++; } catch { }
                        }
                    }
                }
                Log?.Info("[EventRotator][官方外观] botAppearanceChanges." + key + "：改了 " + n + " 条（官方 AdjustBotAppearanceValues 同源数据；可还原）");
            }
        }
        catch (Exception ex) { Log?.Warning("[EventRotator][官方外观] 失败: " + ex.Message); }
    }

    /// <summary>
    /// ★★★ v2.6.14【按 bot 类型直接覆盖外观】**mod 自定义维度**（官方没有对应机制）。
    /// <para>形状：`{ "bot类型": { "body|feet|head|hands|voice": "外观定制 id" } }`。</para>
    /// <para>语义：**整体替换**该部位的字典内容（保留字典实例本身，只换键），
    /// 旧条目进快照，还原时精确放回（逆序回放保证多活动叠加也能回原值）。</para>
    /// <para>用途（见 <see cref="EventDef.BotAppearanceOverride"/> 的 XML 文档）：
    /// 把 `ravangezryachiyevent`（复仇小鹿）的 body/feet/head 从本体数据的
    /// **普通 SCAV 通用外观**换成小鹿外观 —— 用户 2026-10-16 明确要求。</para>
    /// </summary>
    public static void ApplyBotAppearanceOverride(
        Dictionary<string, Dictionary<string, string>> spec, object botTable)
    {
        if (spec == null || spec.Count == 0) return;
        if (botTable == null) { Log?.Warning("[EventRotator][外观覆盖] 拿不到 BotTable"); return; }
        try
        {
            var types = Prop(botTable, "Types") as IDictionary;
            if (types == null) { Log?.Warning("[EventRotator][外观覆盖] BotTable.Types 不是字典"); return; }

            int changed = 0, parts = 0;
            foreach (var botKv in spec)
            {
                var botType = botKv.Key?.ToString();
                if (string.IsNullOrWhiteSpace(botType) || botKv.Value == null || botKv.Value.Count == 0) continue;

                object bt = null;
                foreach (DictionaryEntry de in types)
                    if (string.Equals(de.Key?.ToString(), botType, StringComparison.OrdinalIgnoreCase)) { bt = de.Value; break; }
                if (bt == null) { Log?.Warning("[EventRotator][外观覆盖] 找不到 bot 类型 '" + botType + "'"); continue; }

                // ★ SPT 前缀陷阱：JSON 的 `appearance` 在 CLR 模型里是 `Appearance`/`BotAppearance`
                var app = Prop(bt, "Appearance") ?? Prop(bt, "BotAppearance") ?? Prop(bt, "appearance");
                if (app == null)
                {
                    var names0 = bt.GetType().GetProperties().Select(x => x.Name).Take(14);
                    Log?.Warning("[EventRotator][外观覆盖] '" + botType + "' 没有 Appearance 段；成员示例: " + string.Join(",", names0));
                    continue;
                }

                foreach (var partKv in botKv.Value)
                {
                    var part = partKv.Key?.ToString();
                    var newId = partKv.Value?.ToString();
                    if (string.IsNullOrWhiteSpace(part) || string.IsNullOrWhiteSpace(newId)) continue;

                    var dict = Prop(app, part) as IDictionary;
                    if (dict == null) { Log?.Warning("[EventRotator][外观覆盖] '" + botType + ".Appearance." + part + "' 不是字典"); continue; }

                    // 快照：把该部位现有全部条目拿出来（通常 1 条；极端情况可能多条）
                    var snap = new List<DictionaryEntry>();
                    foreach (DictionaryEntry de in dict) snap.Add(de);
                    var capD = dict; var capSnap = snap;
                    AddRestore?.Invoke(() =>
                    {
                        try
                        {
                            capD.Clear();
                            foreach (var de in capSnap) capD[de.Key] = de.Value;
                        }
                        catch { }
                    });

                    // 整体替换：清空 + 写入新 id（值类型跟随字典的 value 泛型参数）
                    var valType = dict.GetType().IsGenericType ? dict.GetType().GetGenericArguments()[1] : typeof(double);
                    var keyType = dict.GetType().IsGenericType ? dict.GetType().GetGenericArguments()[0] : typeof(string);
                    var beforeStr = string.Join(",", snap.Select(x => x.Key?.ToString()));
                    dict.Clear();
                    var k2 = Coerce(newId, keyType);
                    dict[k2] = Coerce(1.0, valType);

                    // 回读自证（本项目铁律：日志说改了就必须真改了）
                    var afterStr = string.Join(",", dict.Keys.Cast<object>().Select(x => x?.ToString()));
                    bool ok = dict.Count == 1 && string.Equals(afterStr, newId, StringComparison.OrdinalIgnoreCase);
                    Log?.Info("[EventRotator][外观覆盖] " + botType + ".Appearance." + part + ": {" + beforeStr + "} → {" + afterStr + "}"
                              + (ok ? " ✔" : " ⚠ 回读不符"));
                    changed++; parts++;
                }
            }
            if (parts > 0)
                Log?.Info("[EventRotator][外观覆盖] 共覆盖 " + changed + " 个部位（按 bot 类型直写 Appearance；可还原）"
                          + " —— ⚠ 这是**与本体数据不符的 mod 取值**，理由见 EventDef.BotAppearanceOverride 的文档");
        }
        catch (Exception ex) { Log?.Warning("[EventRotator][外观覆盖] 失败: " + ex.Message); }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ★★★ v0.1.5 完全还原同步 A~D
    //  ═══════════════════════════════════════════════════════════════════════

    /// <summary>A 官方 `AddGifterBotToMaps()`：逐图把 gifter 的 BossChance/BossZone 设为官方 gifterSettings 的值。</summary>
    public static void ApplyGifterOfficial(object seasonalCfg, object locationTable)
    {
        if (seasonalCfg == null || locationTable == null) return;
        try
        {
            var settings = Prop(seasonalCfg, "GifterSettings") as IEnumerable;
            var locs = Prop(locationTable, "GetDictionary") != null ? null : Prop(locationTable, "Locations");
            if (settings == null) { Log?.Warning("[EventRotator][圣诞老人] 拿不到 GifterSettings"); return; }
            var mapDict = Prop(locationTable, "Locations") as IDictionary;
            var getDict = locationTable.GetType().GetMethod("GetDictionary", Type.EmptyTypes);
            object dictObj = mapDict;
            if (getDict != null) { try { dictObj = getDict.Invoke(locationTable, null); } catch { } }
            var dict = dictObj as IDictionary;
            if (dict == null) { Log?.Warning("[EventRotator][圣诞老人] 拿不到地图字典"); return; }
            int n = 0;
            foreach (var g in settings)
            {
                var map = Prop(g, "Map")?.ToString();
                var chance = Prop(g, "SpawnChance");
                var zones = Prop(g, "Zones")?.ToString();
                if (string.IsNullOrEmpty(map)) continue;
                // ★ 官方用 `locationTable.GetMappedKey(item.Map)` 做键映射（如 factory4_day → factory4_day 的
                //   实际键名）—— 第一版直接拿官方 map 名去查，实测 `factory4_day 地图未找到` ✗。这里补上映射。
                var mapped = map;
                var gm = locationTable.GetType().GetMethod("GetMappedKey", new[] { typeof(string) });
                if (gm != null) { try { mapped = gm.Invoke(locationTable, new object[] { map })?.ToString() ?? map; } catch { } }
                object loc = null;
                foreach (DictionaryEntry de in dict)
                {
                    var k = de.Key?.ToString();
                    if (string.Equals(k, mapped, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(k, map, StringComparison.OrdinalIgnoreCase)) { loc = de.Value; break; }
                }
                if (loc == null) { Log?.Warning("[EventRotator][圣诞老人] 地图未找到: " + map); continue; }
                var baseObj = Prop(loc, "Base");
                var spawns = Prop(baseObj, "BossLocationSpawn") as IList;
                if (spawns == null) { Log?.Warning("[EventRotator][圣诞老人] " + map + " 没有 BossLocationSpawn"); continue; }
                object found = null;
                foreach (var s in spawns)
                    if (string.Equals(Prop(s, "BossName")?.ToString(), "gifter", StringComparison.OrdinalIgnoreCase)) { found = s; break; }
                if (found != null)
                {
                    var oldC = Prop(found, "BossChance"); var oldZ = Prop(found, "BossZone");
                    var cap = found;
                    AddRestore?.Invoke(() => { try { SetProp(cap, "BossChance", oldC); SetProp(cap, "BossZone", oldZ); } catch { } });
                    SetProp(found, "BossChance", chance);
                    if (!string.IsNullOrEmpty(zones)) SetProp(found, "BossZone", zones);
                    n++;
                }
                else
                {
                    // 官方新建条目的字段（逐字）：BossName/BossChance/BossZone/IsBossPlayer/BossDifficulty/BossEscortType/BossEscortDifficulty/BossEscortAmount
                    var item = Activator.CreateInstance(spawns.GetType().GetGenericArguments().Length > 0
                        ? spawns.GetType().GetGenericArguments()[0] : spawns[0].GetType());
                    SetProp(item, "BossName", "gifter");
                    SetProp(item, "BossChance", chance);
                    SetProp(item, "BossZone", zones);
                    SetProp(item, "IsBossPlayer", false);
                    SetProp(item, "BossDifficulty", "normal");
                    SetProp(item, "BossEscortType", "gifter");
                    SetProp(item, "BossEscortDifficulty", "normal");
                    SetProp(item, "BossEscortAmount", "0");
                    var capList = spawns; var capItem = item;
                    AddRestore?.Invoke(() => { try { capList.Remove(capItem); } catch { } });
                    spawns.Add(item);
                    n++;
                }
                Log?.Info("[EventRotator][圣诞老人] " + map + "：chance=" + chance + " zones=" + (zones ?? "-")
                        + "（官方 AddGifterBotToMaps 同源数据）");
            }
            Log?.Info("[EventRotator][圣诞老人] 共按官方表设置 " + n + " 张图（可还原）");
        }
        catch (Exception ex) { Log?.Warning("[EventRotator][圣诞老人] 失败: " + ex.Message); }
    }

    /// <summary>
    /// B 官方 `christmasLootBoost`：把**松散战利品（looseLoot）的概率**按官方公式
    /// `p → p + (1-p)*boost` 提升（圣诞=0.03），只作用于**模板 id 含 christmas** 的刷点
    /// （官方 `ApplyChristmasEvent` 反编译逐字条件）。
    ///
    /// <para><b>★★★ 为什么必须走 AddTransformer（v1.0.1 修复）</b></para>
    /// <para>
    /// `Location.LooseLoot` 是 `LazyLoad&lt;LooseLoot&gt;`，而 `LooseLoot` 类型**没有**
    /// `CacheLazyLoadAttribute`（已用 .NET MetadataReader 全量核对 `SPTarkov.Server.Core.dll`：
    /// 该特性的挂载点数量为 **0**）。`LazyLoad&lt;T&gt;` 的构造是
    /// `LazyLoad(Func&lt;T&gt; deserialize, bool cacheValue = false)`，`cacheValue=false` 时
    /// **每次读 `.Value` 都会重新 Build()** = 重新反序列化 looseLoot.json + 重跑 transformer。
    /// </para>
    /// <para>
    /// ⇒ 旧实现「取 `.Value` → 改 `sp.Probability`」改的是**瞬时对象**，一改就丢；
    ///   而它的"回读自证"读的是同一个瞬时引用，所以永远显示成功（**假阳性**）。
    ///   真机上表现：圣诞活动的爆率提升从来没进过游戏。
    /// </para>
    /// <para>
    /// ⇒ 正解就是官方自己的做法：`looseLoot.AddTransformer(Func&lt;LooseLoot, LooseLoot&gt;)`。
    ///   已确认签名 `AddTransformer(Func&lt;T,T&gt;) -&gt; Void`，内部字段
    ///   `_lazyLoadTransformers : List&lt;Func&lt;T,T&gt;&gt;`。
    /// </para>
    /// <para><b>★ 去重设计</b>：transformer 读的是**静态可变** `_lootBoostValue`，
    /// 所以每张图只 `AddTransformer` **一次**；活动切换/`RestoreThenApply` 重复套用
    /// 只改这个静态值，**永远不会重复叠加**（否则 p 会被变换两次：
    /// 3% 会叠成约 5.9%）。</para>
    /// </summary>
    public static void ApplyChristmasLootBoost(double boost)
    {
        LootBoost = boost;
        _lootBoostValue = boost > 0 ? boost : 0.0;      // ★ 先更新静态值（transformer 读它）
        if (boost <= 0 || LocationTableRef == null) return;
        try
        {
            var getDict = LocationTableRef.GetType().GetMethod("GetDictionary", Type.EmptyTypes);
            object dictObj = null;
            if (getDict != null) { try { dictObj = getDict.Invoke(LocationTableRef, null); } catch { } }
            var dict = dictObj as IDictionary;
            if (dict == null) { Log?.Warning("[EventRotator][圣诞爆率] 拿不到地图字典"); return; }

            var maker = typeof(ExtraDimensions).GetMethod(
                nameof(MakeLootBoostTransformer), BindingFlags.NonPublic | BindingFlags.Static);
            if (maker == null) { Log?.Warning("[EventRotator][圣诞爆率] 找不到 transformer 构造器"); return; }

            int hooked = 0, already = 0, fail = 0, noXmas = 0;
            foreach (DictionaryEntry de in dict)
            {
                var loc = de.Value;
                if (loc == null) continue;
                var loose = Prop(loc, "LooseLoot");          // LazyLoad<LooseLoot>（**不读 .Value**）
                if (loose == null) continue;

                var llType = loose.GetType();
                if (!llType.IsGenericType) continue;          // 不是 LazyLoad ⇒ 跳过（不猜结构）
                var elemType = llType.GetGenericArguments()[0];
                if (elemType == null || !elemType.IsClass) continue;

                var add = llType.GetMethod("AddTransformer");
                if (add == null) continue;

                if (_lootBoostHooked.Contains(loose)) { already++; continue; }   // ★ 去重：每图只挂一次

                // ★★★ v1.6.6【性能 · 启动最大单笔开销】"本图有没有 christmas 刷点"的结果按地图缓存。
                //   以前每次 RestoreThenApply 都会把**所有未挂 transformer 的图**重扫一遍
                //   （读 `LooseLoot.Value` 会触发整张图 Build；实测 13 图 30,286 刷点，
                //     有 10 张图 0 个 christmas 刷点却每轮都白扫 3~7 秒；
                //     因为我们有 2 个活动声明 christmasLootBoost ⇒ 每次启动要扫两轮）。
                //   现在：第一轮扫完把"没有"的图记下来，后续直接跳过、连 `.Value` 都不读。
                var mapKey = de.Key?.ToString() ?? "";
                if (mapKey.Length > 0 && _noChristmasMaps.Contains(mapKey)) { noXmas++; continue; }

                // ★ v1.0.1【性能】预判：**本图一个 christmas 刷点都没有就根本不挂 transformer**。
                //   实测 13 张图共 30,286 个刷点，其中 tarkovstreets(7,873) / laboratory(1,402) /
                //   sandbox(1,232) / sandbox_high(1,291) / factory4_day(503) / factory4_night(511) /
                //   labyrinth(341) **一个都不含 christmas** ⇒ 挂了也永远是空跑（每次 Build 白遍历）。
                //   这一读会触发一次 Build（进图时本来也要读），换来判断"以后每局都省掉整段遍历"。
                if (!HasChristmasSpawn(loose))
                {
                    if (mapKey.Length > 0) _noChristmasMaps.Add(mapKey);   // ★ v1.6.6 记住"这图没有"，以后不再扫
                    noXmas++;
                    continue;
                }

                try
                {
                    var del = (Delegate)maker.MakeGenericMethod(elemType).Invoke(null, null);
                    if (del == null) { fail++; continue; }
                    add.Invoke(loose, new object[] { del });
                    _lootBoostHooked.Add(loose);
                    hooked++;
                }
                catch (Exception exAdd) { fail++; Log?.Warning("[EventRotator][圣诞爆率] AddTransformer 失败: " + exAdd.Message); }
            }

            Log?.Info("[EventRotator][圣诞爆率] 已给 " + hooked + " 张图的 LooseLoot 挂上官方 AddTransformer"
                    + "（已挂过 " + already + " 张；本图无 christmas 刷点而跳过 " + noXmas + " 张"
                    + (fail > 0 ? "；失败 " + fail + " 张" : "") + "）"
                    + "；当前 boost=" + _lootBoostValue.ToString("0.####")
                    + "，变换在**每次读 .Value 时**生效（官方同款；不改任何文件）");
        }
        catch (Exception ex) { Log?.Warning("[EventRotator][圣诞爆率] 失败: " + ex.Message); }
    }

    /// <summary>
    /// ★ v1.0.1【性能】预判：该图的 `LooseLoot` 里**有没有**模板 id 含 "christmas" 的刷点。
    /// 没有就**不挂** transformer —— 否则每次 Build 都要把整张图的刷点遍历一遍做无用功
    /// （实测 tarkovstreets 7,873 个刷点、0 个 christmas）。
    /// 读一次 `Value` 会触发一次 Build，但进图时本来也要读，净开销为 0。
    /// </summary>
    private static bool HasChristmasSpawn(object loose)
    {
        try
        {
            var val = Prop(loose, "Value");
            if (val == null) return false;
            var spawns = Prop(val, "Spawnpoints") as IEnumerable;
            if (spawns == null) return false;
            foreach (var sp in spawns)
            {
                if (sp == null) continue;
                var tpl = Prop(sp, "Template");
                var tplId = Prop(tpl, "Id")?.ToString()
                         ?? Prop(tpl, "TemplateId")?.ToString()
                         ?? Prop(sp, "TemplateId")?.ToString();
                if (!string.IsNullOrEmpty(tplId)
                    && tplId.IndexOf("christmas", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// ★ 构造 `Func&lt;LooseLoot, LooseLoot&gt;`。**故意读静态 `_lootBoostValue` 而不是捕获参数**
    /// —— 这样每张图只需挂一次 transformer，后续改 boost 值立刻生效且**永远不会重复叠加**。
    ///
    /// <para>变换条件与官方 `ApplyChristmasEvent` 反编译**逐字一致**：
    /// 只处理 `Spawnpoint.Template.Id`（退化再看 `TemplateId`）**含 "christmas"** 的刷点；
    /// 公式 `p = min(1, p + (1-p)*boost)`。</para>
    ///
    /// <para>整个方法体包在 try/catch 里：transformer 是在 SPT **读战利品时**被调用的，
    /// 抛异常会污染 loot 生成 —— 必须保证任何情况下都原样返回对象。</para>
    /// </summary>
    private static Func<T, T> MakeLootBoostTransformer<T>() where T : class
    {
        return obj =>
        {
            try
            {
                var boost = _lootBoostValue;
                if (boost <= 0 || obj == null) return obj;
                var spawns = Prop(obj, "Spawnpoints") as IEnumerable;
                if (spawns == null) return obj;
                foreach (var sp in spawns)
                {
                    if (sp == null) continue;
                    var tpl = Prop(sp, "Template");
                    var tplId = Prop(tpl, "Id")?.ToString()
                             ?? Prop(tpl, "TemplateId")?.ToString()
                             ?? Prop(sp, "TemplateId")?.ToString();
                    if (string.IsNullOrEmpty(tplId)
                        || tplId.IndexOf("christmas", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var cur = Prop(sp, "Probability");
                    if (cur == null) continue;
                    double old;
                    try { old = Convert.ToDouble(cur); } catch { continue; }
                    var nv = Math.Min(1.0, old + (1.0 - old) * boost);
                    SetProp(sp, "Probability", nv);
                }
            }
            catch { }          // ★ 绝不把异常抛给 loot 生成
            return obj;
        };
    }

    /// <summary>
    /// ★ 已挂过 transformer 的 `LazyLoad&lt;LooseLoot&gt;` 引用集合（**去重**，防重复叠加）。
    /// 用引用相等 —— 地图表换表（MapVariants 切原版/回迁版）后是**不同实例**，会各自挂一次，
    /// 这是正确的（每个实例都需要自己的 transformer）。整局最多十几张图，开销可忽略。
    /// </summary>
    /// <summary>★ v1.6.6【性能】已确认"没有 christmas 刷点"的地图 key（避免每轮重扫整张图）。</summary>
    private static readonly HashSet<string> _noChristmasMaps = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<object> _lootBoostHooked = new(ReferenceEqualityComparer.Instance);

    /// <summary>★ 当前生效的圣诞爆率（transformer 每次被调用时读它；0 = 关闭）。</summary>
    private static double _lootBoostValue = 0.0;



    /// <summary>供进图刷新时复用（官方对 looseLoot 逐个概率做同样变换）。</summary>
    public static double LootBoost = 0;

    /// <summary>C 官方 `ChangeBtrToTarColaSkin()`：Woods / TarkovStreets 的 BTR 皮肤设为 Tarcola。</summary>
    public static void ApplyBtrTarcolaSkin(bool on)
    {
        if (!on || GlobalTableRef == null) return;
        try
        {
            var cfg = Prop(GlobalTableRef, "Configuration");
            var btr = Prop(cfg, "BTRSettings");
            var maps = Prop(btr, "MapsConfigs") as IDictionary;
            if (maps == null) { Log?.Warning("[EventRotator][BTR皮肤] 拿不到 BTRSettings.MapsConfigs"); return; }
            foreach (var wanted in new[] { "Woods", "TarkovStreets" })
            {
                object mc = null;
                foreach (DictionaryEntry de in maps)
                    if (string.Equals(de.Key?.ToString(), wanted, StringComparison.OrdinalIgnoreCase)) { mc = de.Value; break; }
                if (mc == null) { Log?.Warning("[EventRotator][BTR皮肤] 找不到地图配置 " + wanted); continue; }
                var old = Prop(mc, "BtrSkin");
                var cap = mc;
                AddRestore?.Invoke(() => { try { SetProp(cap, "BtrSkin", old); } catch { } });
                SetProp(mc, "BtrSkin", "Tarcola");
                Log?.Info("[EventRotator][BTR皮肤] " + wanted + "：BtrSkin " + (old ?? "(空)") + " → Tarcola（官方 ChangeBtrToTarColaSkin 逐字同款；可还原）");
            }
        }
        catch (Exception ex) { Log?.Warning("[EventRotator][BTR皮肤] 失败: " + ex.Message); }
    }

    /// <summary>D 官方 `EnableRunnansEvent()`：`RunddansSettings.Active = true; ActivePVE = true;`</summary>
    public static void ApplyRunddansActive(bool on)
    {
        if (!on || GlobalTableRef == null) return;
        try
        {
            var cfg = Prop(GlobalTableRef, "Configuration");
            var rd = Prop(cfg, "RunddansSettings");
            if (rd == null) { Log?.Warning("[EventRotator][圆环舞] 拿不到 RunddansSettings"); return; }
            foreach (var field in new[] { "Active", "ActivePVE" })
            {
                var old = Prop(rd, field);
                var cap = rd; var f = field;
                AddRestore?.Invoke(() => { try { SetProp(cap, f, old); } catch { } });
                SetProp(rd, field, true);
                Log?.Info("[EventRotator][圆环舞] RunddansSettings." + f + "：" + (old ?? "null") + " → true（官方 EnableRunnansEvent 逐字同款；可还原）");
            }
        }
        catch (Exception ex) { Log?.Warning("[EventRotator][圆环舞] 失败: " + ex.Message); }
    }

    /// <summary>
    /// ★★★ v2.6.8【官方「完整版万圣节模式」：**所有白图 → 电闪雷鸣的黑夜**】
    ///
    /// **客户端反编译逐条实证**（`audit/client_full/Assembly-CSharp.decompiled.cs`）：
    ///   · `:556831` `GlobalConfiguration.EventSettings`（readonly `EventGlobalSettings`）
    ///     ← 由服务端 `globals.config.EventSettings` 反序列化而来；
    ///   · `:555704` `class EventGlobalSettings` 全部字段：
    ///     `EventActive` / `EventTime`(=120) / `WeatherChangeTime`(=5) / `ExitTimeMultiplier`(=3) /
    ///     `StaminaMultiplier`(=2) / `EventWeather` / `SummonSuccessWeather` / `SummonFailedWeather` /
    ///     `LocationsToIgnore`(默认 `{factory4_day, factory4_night, hideout, laboratory}`)；
    ///   · **地图加载三处判定**（`:668717` / `:670038` / `:671607`）：
    ///     `if (EventSettings.EventActive &amp;&amp; !EventSettings.LocationsToIgnore.Contains(location._Id))`
    ///     → `Resources.Load("Prefabs/HALLOWEEN_CONTROLLER")` 并实例化
    ///     ⇒ **`LocationsToIgnore` 之外的所有地图都挂上万圣节控制器** = 视频说的「**所有白图**」；
    ///   · `:568290` `HalloweenEventController` 读值：
    ///     `_startEventWeather = eventSettings.EventWeather` / `_secondsToChange = eventSettings.WeatherChangeTime` /
    ///     `_eventTime = eventSettings.EventTime`；
    ///   · `:568394` → `EnableWeatherEvent(_startEventWeather)` → `:568447` →
    ///     `WeatherEventController.SetWeather(...)` + `Activate(true)`（`:165971`）
    ///     ⇒ 世界钟在 `WeatherChangeTime` 秒内**加速扫到 `EventWeather.Hour`**，完成后 `LockCurrentTime = true`；
    ///   · HUD 活动计时器：`:941764` `EventSettings.EventActive &amp;&amp; _eventTimerTemplate != null`。
    ///
    /// `EventWeather` 本机 globals 值 =
    ///   `Cloudness 1`（全阴）+ `Hour 21 / Minute 0`（夜）+ `Rain 0.1`（雨）
    ///   + `Wind 4 / WindDirection 8`（大风）+ `ScaterringFogDensity 0.02`
    ///   ⇒ **全阴 + 21:00 + 雨 + 大风 = 「电闪雷鸣的黑夜」**。
    ///
    /// ⚠ **本 mod 此前从未设置过 `EventActive`** —— `grep EventActive` 在 mod 源码里**只命中一行注释**；
    ///   SPT 本体也不设置（`SeasonalEventService._halloweenEventActive` 只是内部布尔、不下发客户端）
    ///   ⇒ 官方这套"完整版万圣节模式"在本机**一次都没生效过**。本维度把它补上。
    ///
    /// ⚠ **与「入夜」维度的区别（用户明确指正）**：
    ///   · 「入夜」(`forceNightRaid` → `BossWavePatch.PostfixWeatherGenerate`，只改 `/client/weather` 的 `time`)
    ///     = 给**邪教徒**类活动用的（`night_of_the_cult` / `cultist_hunting` / `cultist_pilgrimage` 等）；
    ///   · 「雷雨/万圣节模式」(`eventActive` → 本方法)
    ///     = 官方**万圣节专属**的完整模式（控制器 + 天气 + 计时器 + 撤离/体力倍率），两者不要混用。
    /// </summary>
    public static void ApplyEventWeather(bool on, string src)
    {
        if (!on || GlobalTableRef == null) return;
        try
        {
            var cfg = Prop(GlobalTableRef, "Configuration") ?? Prop(GlobalTableRef, "config");
            var es = Prop(cfg, "EventSettings");
            if (es == null) { Log?.Warning("[EventRotator][万圣节模式] 拿不到 config.EventSettings"); return; }

            var oldActive = Prop(es, "EventActive");
            var cap = es;
            AddRestore?.Invoke(() => { try { SetProp(cap, "EventActive", oldActive); } catch { } });
            SetProp(es, "EventActive", true);

            // 自证：把 EventWeather 那套参数原样读出来打日志（证明"电闪雷鸣"参数确实在）
            var ew = Prop(es, "EventWeather");
            string ewDesc = ew == null
                ? "(缺 EventWeather —— 客户端会用它的类默认值 Hour 21 / Minute 40 / Cloudness 1.3)"
                : string.Join("，", new[] { "Hour", "Minute", "Cloudness", "Rain", "RainRandomness",
                                           "Wind", "WindDirection", "ScaterringFogDensity" }
                        .Select(k => k + "=" + (Prop(ew, k) ?? "null")));
            var wct = Prop(es, "WeatherChangeTime");
            var et = Prop(es, "EventTime");
            var h = ew != null ? Prop(ew, "Hour") : 21;
            var m = ew != null ? Prop(ew, "Minute") : 40;

            Log?.Info($"[EventRotator][万圣节模式] ★EventSettings.EventActive：" + (oldActive ?? "null")
                    + " → True（来源 " + src + "；官方「完整版万圣节模式」，只写内存、可还原）");
            Log?.Info("[EventRotator][万圣节模式]   起始天气 EventWeather = " + ewDesc);
            Log?.Info($"[EventRotator][万圣节模式]   世界钟将在 WeatherChangeTime={wct} 秒内加速扫到 {h}:{m}，然后 LockCurrentTime（= 进图即入夜）");
            Log?.Info($"[EventRotator][万圣节模式]   活动计时 EventTime={et} 秒 → HUD 显示活动计时器");
            Log?.Info("[EventRotator][万圣节模式]   生效范围 = 客户端 LocationsToIgnore 之外的所有地图"
                    + "（默认忽略 factory4_day / factory4_night / hideout / laboratory）⇒ 即「所有白图」");
            Log?.Info("[EventRotator][万圣节模式]   回读自证：EventActive=" + (Prop(es, "EventActive") ?? "null") + "（期望 True）"
                    + "；EventWeather.Cloudness=" + (ew != null ? (Prop(ew, "Cloudness") ?? "null") : "null")
                    + "；Wind=" + (ew != null ? (Prop(ew, "Wind") ?? "null") : "null"));
        }
        catch (Exception ex) { Log?.Warning("[EventRotator][万圣节模式] 失败: " + ex.Message); }
    }

    /// <summary>
    /// E 官方 `GivePlayerSeasonalGifts(sessionId)`（逐字）：
    ///   Christmas ⇒ `GiveGift(sessionId, "Christmas2022")`
    ///   NewYears  ⇒ `GiveGift(sessionId, "NewYear2023")` + `GiveGift(sessionId, "NewYear2024")`
    /// `GiveGift` 内部：`giftService.GetGiftById(key)` → `PlayerHasReceivedMaxNumberOfGift(...)` → `SendGiftToPlayer(...)`。
    /// 我们把 key 列表发给**当前所有存档（profile id 即 sessionId）**。
    ///
    /// ★★★ P0/P1（簇6-C2）**语义结论：这份礼物是「每个存档每份礼物一次」，不是「每局都发」。**
    ///   证据链（全部反编译逐字）：
    ///   ① `GameController.cs:116` → `seasonalEventService.GivePlayerSeasonalGifts(sessionId)`
    ///      是**每次进图**都会调的（`StartClientRaid` 流程里）。
    ///   ② `SeasonalEventService.cs:1114-1121` `GiveGift`：
    ///      `if (!profileHelper.PlayerHasReceivedMaxNumberOfGift(playerId, giftKey, giftById.MaxToSendPlayer ?? 5)) SendGiftToPlayer(...)`
    ///      —— 官方自己在**每次进图**的调用点上就套了一层"发过没"的判断 ⇒ 语义 = 活动期内**一次**。
    ///   ③ `GiftService.SendGiftToPlayer`（`GiftService.cs:61-126`）**函数内部自带同一道闸**：
    ///      `if (profileHelper.PlayerHasReceivedMaxNumberOfGift(playerId, giftId, giftById.MaxToSendPlayer ?? 1)) return GiftSentResult.FAILED_GIFT_ALREADY_RECEIVED;`
    ///      成功才 `FlagGiftReceivedInProfile(playerId, giftId, num)`，并且**先入邮箱、后打标记**（标记在 return 之前）。
    ///   ④ `ProfileHelper.cs:411-448`：标记/S 判定读写的都是 `fullProfile.SptData.ReceivedGifts` 的 `Current` 计数，
    ///      而 `GetFullProfile` → `SaveServer.GetProfile` 返回的是 `SaveServer.cs:85-100` 的**内存活对象**，
    ///      和 `GetProfiles()`（`SaveServer.cs:111-114`，值仍是同一批 SptProfile 引用）是同一份 ⇒ 同一次服务器运行内**判定有效**。
    ///
    ///   ⇒ **结论：我们这个函数本来就不会重复发礼物**（重复调用只会在 SPT 内部返回 FAILED_GIFT_ALREADY_RECEIVED）。
    ///     所以**不做**额外 HashSet 幂等（那会与 SPT 的存档级去重逻辑打架，反而挡住"玩家删档后重发"这类正常路径）。
    ///   ⇒ 但改名前这里有**实打实的日志谎报**：`SendGiftToPlayer` 的返回值（GiftSentResult）被**整个丢掉**，
    ///     无论成功、已领过、礼物 id 不存在，都无条件打印"已向存档 … 发放礼物 '…'"。
    ///     即"没发出去"也会被记成"已发放"（每次进图刷一次，日志里看起来像无限重复发）。
    ///     现在改成**读返回值如实打印**，不改变任何发放行为。
    ///
    /// ⚠ 未验到的一点（如实标注）：跨**服务器重启**的去重依赖 `ReceivedGifts` 已被写回存档 JSON；
    ///   标记发生在内存对象上，落盘时机由 SPT 的存档保存策略决定，本次未做实机重启验证。
    /// </summary>
    public static void ApplyPlayerGift(List<string> giftKeys, object giftService, object saveServer)
    {
        if (giftKeys == null || giftKeys.Count == 0 || giftService == null || saveServer == null) return;
        try
        {
            var getProfiles = saveServer.GetType().GetMethod("GetProfiles", Type.EmptyTypes);
            var profiles = getProfiles?.Invoke(saveServer, null) as IDictionary;
            if (profiles == null) { Log?.Warning("[EventRotator][季节礼物] 拿不到 profiles"); return; }
            var send = giftService.GetType().GetMethod("SendGiftToPlayer", new[] { typeof(string), typeof(string) })
                    ?? giftService.GetType().GetMethods().FirstOrDefault(m => m.Name == "SendGiftToPlayer" && m.GetParameters().Length == 2);
            if (send == null) { Log?.Warning("[EventRotator][季节礼物] GiftService 没有 SendGiftToPlayer(string,string)"); return; }

            // ★ 用**枚举底层整数**判定结果，而不是 ToString() 字符串：
            //   反编译实证 `SendGiftToPlayer` 返回 `GiftSentResult`
            //   （`FAILED_UNKNOWN=1, FAILED_GIFT_ALREADY_RECEIVED=2, FAILED_GIFT_DOESNT_EXIST=3, SUCCESS=4`，
            //     已用 Mono.Cecil 直接读运行期 DLL 复核）。
            //   拿到枚举类型就比整数；拿不到（SPT 改名/换类型）才退回按名比较。
            var retType = send.ReturnType;
            object okVal = null, alreadyVal = null;
            if (retType != null && retType.IsEnum)
            {
                try { okVal = Convert.ToInt64(Enum.Parse(retType, "SUCCESS", true)); } catch { }
                try { alreadyVal = Convert.ToInt64(Enum.Parse(retType, "FAILED_GIFT_ALREADY_RECEIVED", true)); } catch { }
            }
            long? RetNum(object r)
            {
                if (r == null) return null;
                if (okVal == null && alreadyVal == null) return null;
                try { return Convert.ToInt64(r); } catch { return null; }
            }

            int sent = 0, skipped = 0, failed = 0, profilesTouched = 0;
            foreach (DictionaryEntry de in profiles)
            {
                var sid = de.Key?.ToString();
                if (string.IsNullOrEmpty(sid)) continue;
                var shortSid = sid.Substring(0, Math.Min(8, sid.Length));
                bool anyForProfile = false;
                foreach (var key in giftKeys)
                {
                    try
                    {
                        var p0 = send.GetParameters()[0].ParameterType; var p1 = send.GetParameters()[1].ParameterType;
                        var ret = send.Invoke(giftService, new[] { Coerce(sid, p0), Coerce(key, p1) });

                        // ★ 如实读 GiftSentResult（SPTarkov.Server.Core.Models.Enums.GiftSentResult）
                        var rn = ret?.ToString() ?? "";
                        var num = RetNum(ret);
                        bool isOk = (num != null && okVal != null) ? num.Value == (long)okVal : rn == "SUCCESS";
                        bool isAlready = (num != null && alreadyVal != null)
                            ? num.Value == (long)alreadyVal
                            : rn == "FAILED_GIFT_ALREADY_RECEIVED";
                        if (isOk)
                        {
                            sent++; anyForProfile = true;
                            Log?.Info("[EventRotator][季节礼物] ★已向存档 " + shortSid + "… 发放礼物 '" + key
                                    + "'（官方 GivePlayerSeasonalGifts/GiveGift 同款；SPT 标记为已领，下次进图不会重发）");
                        }
                        else if (isAlready)
                        {
                            // 这不是错误：官方语义就是"每个存档每个礼物只发一次"，重复调用被 SPT 自己挡掉。
                            skipped++;
                        }
                        else
                        {
                            failed++;
                            Log?.Warning("[EventRotator][季节礼物] '" + key + "' 未发放（存档 " + shortSid
                                    + "…，GiftService 返回 " + (rn == "" ? "null/void" : rn) + "）");
                        }
                    }
                    catch (Exception ex1) { failed++; Log?.Warning("[EventRotator][季节礼物] 发放 '" + key + "' 失败: " + ex1.Message); }
                }
                if (anyForProfile) profilesTouched++;
            }
            if (skipped > 0 || failed > 0)
                Log?.Info($"[EventRotator][季节礼物] 本次发放汇总：新发放 {sent} 次 / {profilesTouched} 个存档；"
                        + $"已被 SPT 判为「本存档已领过」{skipped} 次（官方语义=活动期内每存档一次，非异常）；失败 {failed} 次");
        }
        catch (Exception ex) { Log?.Warning("[EventRotator][季节礼物] 失败: " + ex.Message); }
    }
}
