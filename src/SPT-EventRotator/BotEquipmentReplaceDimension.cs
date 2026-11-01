using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★★★【整体替换 bot 装备池】`botEquipmentReplace`（casus_belli / 战争之因 · 劫掠者 6 款彩绘 CQCM 面具
/// + 武器/护甲/插板池）。
///
/// <para><b>要解决什么</b>：<c>casus_belli</c> 活动期间，劫掠者（本机 role = <c>arenaFighterEvent</c>）
/// 只戴 6 款彩绘 CQCM 面具，<b>不再出现巴拉克拉瓦</b>。现有 <c>botAddItems</c> 是**往池子里追加**，
/// 原池里的 Momex / Ghost / Smoke / Cold Fear 巴拉克拉瓦仍然会被抽中 ⇒ 必须**整体替换**。</para>
///
/// <para><b>落点（反编译实证，不猜）</b>：</para>
/// <list type="number">
/// <item><c>BotTable.Types[role].BotInventory.Equipment</c> 的类型是
///   <c>Dictionary&lt;EquipmentSlots, Dictionary&lt;MongoId,double&gt;&gt;</c>
///   （`audit/ilspy_quest/.../BotTypeInventory.cs:14-16`）—— 键是**枚举**，值是"物品→权重"池。
///   本维度把 <c>Equipment[slot]</c> 换成**新建的池对象**（只含给定 tpl），原池对象**原样备份**；
///   还原时把**同一个原池对象**写回 ⇒ 逐字节复原（不是"再拼一个看起来一样的字典"）。</item>
/// <item><c>BotType.BotChances</c>（JSON 名 <c>chances</c>）→ <c>Chances.EquipmentChances</c>
///   （JSON 名 <c>equipment</c>，`Chances.cs:12-13`，类型 <c>Dictionary&lt;string,double&gt;</c>）。
///   把 <c>["FaceCover"]</c> 置 100。SPT 在
///   `BotInventoryGenerator.cs:417` 读 <c>SpawnChances.EquipmentChances.GetValueOrDefault(slot)</c>，
///   `:423` 交给 `RandomUtil.GetChance100(...)`（`RandomUtil.cs:142-146`：
///   <c>GetInt(1,100,exclusive:true) &lt;= chancePercent</c>，即 1..99 &lt;= 100 **恒真**）
///   ⇒ 这就是「一定戴」的实现字段，不是玄学。</item>
/// <item><b>插板（`mods`）</b>：<c>BotInventory.Mods</c> 的类型是
///   <c>Dictionary&lt;MongoId, Dictionary&lt;string, HashSet&lt;MongoId&gt;&gt;&gt;</c>
///   （`BotTypeInventory.cs` 的 <c>Mods</c>，JSON 名 <c>mods</c>）。
///   SPT `BotEquipmentModGenerator.GenerateModsForEquipment`（`:74-127`）按
///   <c>settings.ModPool[父物品.Id]</c> 取"槽位名 → 插板 tpl 集合"，再
///   `GetCompatibleWeaponModTplForSlotFromPool`（`:875`）**逐个校验**
///   <c>parentSlot.Properties.Filters.First().Filter.Contains(tpl)</c> ——
///   **不在该槽 filter 里的板会被丢掉**（所以数据必须逐槽核对）。本维度把
///   <c>Mods[vestTpl][slot]</c> 换成**新建的 HashSet**（只含给定 tpl），原 HashSet 实例备份，
///   还原时写回**同一实例**。</item>
/// <item><b>插板出现概率</b>：同一生成器的 `ShouldModBeSpawned`（`:713-740`）读
///   <c>EquipmentModsChances.GetValueOrDefault(modSlotName.ToLowerInvariant())</c>
///   ⇒ 本机 <c>arenafighterevent.json</c> 的 <c>left_side_plate / right_side_plate</c> 是 **0**
///   （`front_plate / back_plate` 是 100）。若只换池子不抬概率，侧板**永远不会出现**，
///   所以本维度对配置里出现的每个槽把该概率也置 100（原值备份、还原时写回）。</item>
/// <item><b>只对列出地图生效</b>：bot 模板是**全图共用**的，所以和 `MapGateDimension`
///   （`onlyOnMap` / `botLootAdd_`）用同一思路 —— 挂
///   <c>BotGenerator.PrepareAndGenerateBot</c> 的 prefix，在 SPT 读池子**之前**按
///   <c>BotGenerationDetails.Location</c> 把该角色槽位/插板切回"新池/原池"，概率同样切 100/原值。
///   该开关是地图的**纯函数** ⇒ 幂等、同一局每只 bot 都设成同一状态。</item>
/// </list>
///
/// <para><b>可降级</b>：角色/槽位/字典类型任一拿不到 ⇒ 只 warning + 跳过该条（其余照常）。
/// prefix 全程 try/catch 且**永远返回 true**（绝不拦截 bot 生成）。</para>
///
/// <para><b>可还原</b>：<see cref="Restore"/> 逆序把**原池实例 / 原 HashSet 实例 / 原概率**写回并清空登记；
/// 由 `Plugin.Restore()` 在每轮「还原→套用」时调用。</para>
///
/// <para><b>⚠ 共享角色的副作用</b>：替换写的是 **BotTable 里那个全局 role 模板**
/// ⇒ 活动期间**同一张图**上所有 `arenaFighterEvent`（包括别的活动声明的劫掠者波次）都会戴面具/穿这些护甲；
/// 活动没在跑时本维度不登记，巴拉克拉瓦照旧。`db` 里 `CustomBotTypes` 深拷贝出来的自定义 role
/// （bossScourge 等）是**独立模板**，不受影响。</para>
/// </summary>
internal static class BotEquipmentReplaceDimension
{
    /// <summary>`BotTable`（运行期 bot 模板表，由 Plugin 注入）。</summary>
    public static object BotTable;

    /// <summary>Info 日志通道（Plugin 注入；逐字格式见需求：`[装备替换] ...`）。</summary>
    public static Action<string> Log;

    /// <summary>Warning 日志通道（Plugin 注入）。</summary>
    public static Action<string> Warn;

    /// <summary>一条"整槽替换"登记（= 一个 role × slot）。</summary>
    private sealed class Swap
    {
        public IDictionary Equipment;          // BotInventory.Equipment（Dictionary<EquipmentSlots, …>）
        public object SlotKey;                 // 真实键（运行时是 EquipmentSlots 枚举值）
        public object OriginalPool;            // 原池**实例**（还原时原样写回）
        public object ReplacedPool;            // 新池实例（只含配置里的 tpl）
        public HashSet<string> Maps = new(StringComparer.OrdinalIgnoreCase);
        public string Role;                    // 配置里的原始大小写（日志用）
        public string Slot;
        // ── 装备概率（BotChances.EquipmentChances[slot]）──
        public IDictionary Chances;            // Dictionary<string,double>
        public object ChanceKey;               // 槽位名字符串
        public bool HadChance;                 // 原来有没有这个键（没有 ⇒ 还原时删掉）
        public double OldChance;               // 原值
    }

    /// <summary>一条"某护甲某插槽"的 HashSet 替换登记。</summary>
    private sealed class ModSlotSwap
    {
        public string Slot;                    // 配置里的槽名（日志/查重用）
        public object ActualKey;               // 实际写进 VestDict 的键（大小写以模板为准）
        public bool HadSlot;                   // 该槽原本在 Mods[vest] 里存在吗
        public object OriginalSet;             // 原 HashSet **实例**（HadSlot=false 时为 null）
        public object ReplacedSet;             // 新 HashSet 实例
    }

    /// <summary>一条"某护甲的插板池替换"登记（= 一个 role × vestTpl）。</summary>
    private sealed class ModSwap
    {
        public IDictionary Mods;               // BotInventory.Mods（Dictionary<MongoId, Dictionary<string, HashSet<MongoId>>>）
        public object VestKey;                 // 该护甲在 Mods 里的真实键（MongoId）
        public string VestTpl;                 // 配置里的原始字符串（日志用）
        public bool VestWasMissing;            // 本来 Mods 里没有该护甲 ⇒ 新建，还原时整键删除
        public IDictionary VestDict;           // 该护甲的 Dictionary<string, HashSet<MongoId>> 实例
        public HashSet<string> Maps = new(StringComparer.OrdinalIgnoreCase);
        public string Role;
        public List<ModSlotSwap> Slots = new();
    }

    /// <summary>一条"插板出现概率"登记（EquipmentModsChances[slot]，与具体护甲无关 ⇒ 按槽去重）。</summary>
    private sealed class ModChanceSwap
    {
        public IDictionary Chances;            // BotChances.EquipmentModsChances
        public object Key;                     // 槽位**小写**字符串键（SPT 查的就是小写）
        public string Slot;
        public bool HadChance;
        public double OldChance;
        public HashSet<string> Maps = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly List<Swap> _swaps = new();
    private static readonly List<ModSwap> _modSwaps = new();
    private static readonly List<ModChanceSwap> _modChanceSwaps = new();

    /// <summary>当前池子是按哪张图切好的（幂等用）。null = 未切；"*" = 地图未知的兜底（全部按新池）。</summary>
    private static string _appliedFor;

    /// <summary>只提示一次的键（防每个 bot 刷屏）。</summary>
    private static readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    private static void Info(string m) { try { Log?.Invoke(m); } catch { } }
    private static void WarnOnce(string key, string m)
    {
        try { if (_warned.Add(key)) Warn?.Invoke(m); } catch { }
    }

    // ═══════════════════════════════ 套用 ═══════════════════════════════

    /// <summary>
    /// 套用本活动的 `botEquipmentReplace`：登记 + **立刻**把运行期模板换成新池（概率置 100）。
    /// 之后每只 bot 生成前由 <see cref="OnBotGeneratingPrefix"/> 按地图切"新池/原池"。
    /// 全部逐条 try/catch，失败只 warning。
    /// </summary>
    public static void Apply(EventDef ev)
    {
        try
        {
            var spec = ev?.BotEquipmentReplace;
            if (spec == null || spec.Roles == null || spec.Roles.Count == 0) return;
            if (BotTable == null) { WarnOnce("notable", "[装备替换] 拿不到 BotTable ⇒ 跳过"); return; }

            var types = R.Prop(BotTable, "Types") as IDictionary;
            if (types == null) { WarnOnce("notypes", "[装备替换] BotTable.Types 不是字典 ⇒ 跳过"); return; }

            var maps = (spec.Maps ?? new List<string>())
                       .Where(m => !string.IsNullOrWhiteSpace(m))
                       .Select(m => m.Trim().ToLowerInvariant())
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();

            foreach (var roleKv in spec.Roles)
            {
                try
                {
                    if (roleKv.Value == null) continue;
                    var slots = roleKv.Value.Slots ?? new Dictionary<string, Dictionary<string, double>>();
                    var roleMods = roleKv.Value.Mods ?? new Dictionary<string, Dictionary<string, Dictionary<string, double>>>();
                    if (slots.Count == 0 && roleMods.Count == 0) continue;

                    object roleKey = null;
                    foreach (var k in types.Keys)
                        if (string.Equals(k?.ToString(), roleKv.Key, StringComparison.OrdinalIgnoreCase)) { roleKey = k; break; }
                    if (roleKey == null) { WarnOnce("role:" + roleKv.Key, $"[装备替换] 没有 bot 角色 '{roleKv.Key}'"); continue; }

                    var botType = types[roleKey];
                    var inv = R.Prop(botType, "BotInventory") ?? R.Prop(botType, "Inventory");
                    if (inv == null) { WarnOnce("inv:" + roleKv.Key, $"[装备替换] '{roleKv.Key}' 没有 BotInventory"); continue; }

                    // ★ 概率落点 = BotType.BotChances.EquipmentChances（JSON: chances.equipment）
                    var chances = FindChances(botType);

                    // ── ① 装备槽整体替换（原有行为，一字不变）──
                    var eq = R.Prop(inv, "Equipment") as IDictionary;
                    if (eq == null)
                    {
                        if (slots.Count > 0)
                            WarnOnce("eq:" + roleKv.Key, $"[装备替换] '{roleKv.Key}' 没有 Inventory.Equipment");
                    }
                    else
                    {
                        if (slots.Count > 0 && chances == null)
                            WarnOnce("chance:" + roleKv.Key,
                                $"[装备替换] '{roleKv.Key}' 拿不到 BotChances.EquipmentChances ⇒ 只换池子，概率维持原值");

                        foreach (var slotKv in slots)
                        {
                            try
                            {
                                if (slotKv.Value == null || slotKv.Value.Count == 0) continue;

                                object slotKey = FindKey(eq, slotKv.Key);
                                if (slotKey == null)
                                {
                                    WarnOnce("slot:" + roleKv.Key + "." + slotKv.Key,
                                        $"[装备替换] '{roleKv.Key}' 没有槽位 '{slotKv.Key}'"
                                      + $"（现有键=[{string.Join(",", eq.Keys.Cast<object>().Select(x => x?.ToString()))}]）");
                                    continue;
                                }

                                // 同一 role+slot 重复套用 ⇒ 保留**最早**的原池，只刷新新池/地图（防止把已替换的池当原池）
                                var existing = _swaps.FirstOrDefault(x =>
                                    ReferenceEquals(x.Equipment, eq) && Equals(x.SlotKey, slotKey));
                                object original = existing != null ? existing.OriginalPool : eq[slotKey];
                                int oldCount = original is IDictionary od ? od.Count : 0;

                                var replaced = BuildPool(original, eq, slotKv.Value);
                                if (replaced == null)
                                {
                                    WarnOnce("build:" + roleKv.Key + "." + slotKv.Key,
                                        $"[装备替换] '{roleKv.Key}.{slotKv.Key}' 造不出新池（类型未知）⇒ 跳过");
                                    continue;
                                }

                                // ── 概率：备份原值 → 置 100 ──
                                object chanceKey = null;
                                bool had = false; double oldC = 0;
                                if (existing != null)
                                {
                                    // 同一 role+slot 的第二次套用：沿用**最早**那份原概率基线（此时现值可能已是 100）
                                    chanceKey = existing.ChanceKey;
                                    had = existing.HadChance;
                                    oldC = existing.OldChance;
                                    chances ??= existing.Chances;
                                }
                                else if (chances != null)
                                {
                                    try
                                    {
                                        var ckt = GenericArg(chances.GetType(), 0);
                                        chanceKey = R.Coerce(slotKv.Key, ckt) ?? slotKv.Key;
                                        had = chances.Contains(chanceKey);
                                        if (had) oldC = Convert.ToDouble(chances[chanceKey]);
                                    }
                                    catch (Exception ex) { WarnOnce("chancekey", $"[装备替换] 概率键构造失败（已跳过概率）：{ex.Message}"); chances = null; }
                                }

                                // ── 写入（先登记再写；写失败不留下半套用）──
                                var sw = existing ?? new Swap();
                                sw.Equipment = eq;
                                sw.SlotKey = slotKey;
                                sw.OriginalPool = original;
                                sw.ReplacedPool = replaced;
                                sw.Maps = new HashSet<string>(maps, StringComparer.OrdinalIgnoreCase);
                                sw.Role = roleKv.Key;
                                sw.Slot = slotKv.Key;
                                sw.Chances = chances;
                                sw.ChanceKey = chanceKey;
                                sw.HadChance = had;
                                sw.OldChance = oldC;

                                eq[slotKey] = replaced;
                                if (chances != null && chanceKey != null)
                                    chances[chanceKey] = R.Coerce(100d, GenericArg(chances.GetType(), 1));

                                if (existing == null) _swaps.Add(sw);
                                _appliedFor = null;   // 下一只 bot 重新按图切

                                // ★ 需求指定的日志格式（逐图一行）：
                                //   [装备替换] woods arenaFighterEvent.FaceCover：6 项（原 4 项）｜装备概率 100%
                                if (maps.Count == 0)
                                    Info($"[装备替换] (全图) {roleKv.Key}.{slotKv.Key}：{replaced.Count} 项（原 {oldCount} 项）｜装备概率 100%");
                                else
                                    foreach (var m in maps)
                                        Info($"[装备替换] {m} {roleKv.Key}.{slotKv.Key}：{replaced.Count} 项（原 {oldCount} 项）｜装备概率 100%");
                            }
                            catch (Exception ex)
                            {
                                WarnOnce("slot-ex:" + roleKv.Key + "." + slotKv.Key,
                                    $"[装备替换] 写 '{roleKv.Key}.{slotKv.Key}' 失败（已跳过）: {ex.GetType().Name}: {ex.Message}");
                            }
                        }
                    }

                    // ── ② 插板池替换（mods）──
                    if (roleMods.Count > 0)
                        ApplyMods(roleKv.Key, botType, inv, roleMods, maps);
                }
                catch (Exception ex)
                {
                    WarnOnce("role-ex:" + roleKv.Key,
                        $"[装备替换] 处理角色 '{roleKv.Key}' 失败（已跳过）: {ex.Message}");
                }
            }
        }
        catch (Exception ex) { WarnOnce("apply", $"[装备替换] 套用失败（已降级）: {ex.Message}"); }
    }

    /// <summary>
    /// 插板池替换：对配置里的每个护甲 tpl，把 <c>Mods[vestTpl][槽位]</c> 换成**新建的 HashSet**
    /// （只含给定插板 tpl），原 HashSet **实例**备份、还原时写回同一实例。
    ///
    /// <para><b>三种降级</b>（都只 warning，继续其它护甲/槽）：</para>
    /// <list type="bullet">
    /// <item><c>BotInventory.Mods</c> 不是字典 ⇒ 整段跳过</item>
    /// <item>护甲 tpl 不在 <c>Mods</c> 里 ⇒ **新建**该护甲的插槽池（例：本机
    ///   <c>arenafighterevent.json</c> 的 <c>mods</c> 里没有 <c>5f5f41476bdad616ad46d631</c>（Korund-VM），
    ///   因为它原本不在该角色的 ArmorVest 池里 —— 我们既然把 Korund-VM 加进了池子，
    ///   就必须给它建插槽池，否则 SPT 的 <c>GenerateModsForEquipment</c> 会 warning
    ///   "lacks a mod slot pool" 并且**一块板都不装**；还原时整键删除）</item>
    /// <item>槽名在 <c>Mods[vest]</c> 里不存在 ⇒ 视为新槽（写进去；还原时删掉），
    ///   槽名匹配**大小写不敏感**（本机 items.json 的槽名有 <c>Front_plate</c> 与 <c>front_plate</c>
    ///   两种写法，SPT 自己也是 <c>OrdinalIgnoreCase</c>）</item>
    /// </list>
    /// </summary>
    private static void ApplyMods(string role, object botType, object inv,
        Dictionary<string, Dictionary<string, Dictionary<string, double>>> vestPools, List<string> maps)
    {
        var mods = R.Prop(inv, "Mods") as IDictionary;
        if (mods == null)
        {
            WarnOnce("mods-missing:" + role,
                $"[装备替换] '{role}' 的 BotInventory.Mods 不是字典 ⇒ 插板池替换跳过");
            return;
        }

        var modChances = FindModChances(botType);
        if (modChances == null)
            WarnOnce("modchance:" + role,
                $"[装备替换] '{role}' 拿不到 BotChances.EquipmentModsChances ⇒ 只换插板池，插板出现概率维持原值");

        var vestDictType = R.DictValueType(mods);   // Dictionary<string, HashSet<MongoId>>
        var vestKeyType = R.DictKeyType(mods);      // MongoId

        foreach (var vestKv in vestPools)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(vestKv.Key) || vestKv.Value == null || vestKv.Value.Count == 0) continue;
                var vestTpl = vestKv.Key.Trim();

                // ① 找到该护甲在 Mods 里的插槽池；没有就新建（登记 VestWasMissing）
                var mv = _modSwaps.FirstOrDefault(x => ReferenceEquals(x.Mods, mods)
                        && string.Equals(x.VestKey?.ToString(), vestTpl, StringComparison.OrdinalIgnoreCase));
                IDictionary vestDict;
                bool wasMissing;
                object vestKey;
                if (mv != null)
                {
                    vestKey = mv.VestKey;
                    vestDict = mv.VestDict;
                    wasMissing = mv.VestWasMissing;
                }
                else
                {
                    vestKey = FindKey(mods, vestTpl);
                    if (vestKey != null)
                    {
                        vestDict = mods[vestKey] as IDictionary;
                        wasMissing = false;
                    }
                    else
                    {
                        var created = NewInstance(vestDictType) as IDictionary;
                        vestKey = vestKeyType != null ? R.Coerce(vestTpl, vestKeyType) : vestTpl;
                        if (created == null || vestKey == null || !vestKeyType.IsInstanceOfType(vestKey))
                        {
                            WarnOnce("vest-new:" + vestTpl,
                                $"[装备替换] mods '{vestTpl}' 不在 '{role}' 的 Mods 池里，且造不出插槽池 ⇒ 跳过");
                            continue;
                        }
                        try { mods[vestKey] = created; }
                        catch (Exception ex)
                        {
                            WarnOnce("vest-new-set:" + vestTpl,
                                $"[装备替换] mods '{vestTpl}' 新建插槽池写不进去（已跳过）: {ex.Message}");
                            continue;
                        }
                        vestDict = created;
                        wasMissing = true;
                        WarnOnce("vest-missing:" + role + ":" + vestTpl,
                            $"[装备替换] mods '{vestTpl}' 不在 '{role}' 的 Mods 池里 ⇒ **新建**该护甲的插槽池"
                          + "（原来没有 ⇒ 还原时整键删除）");
                    }
                }
                if (vestDict == null)
                {
                    WarnOnce("vest-dict:" + role + ":" + vestTpl,
                        $"[装备替换] mods '{vestTpl}' 的插槽池不是字典 ⇒ 跳过该护甲");
                    continue;
                }

                var sw = mv ?? new ModSwap
                {
                    Mods = mods,
                    VestKey = vestKey,
                    VestTpl = vestTpl,
                    VestWasMissing = wasMissing,
                    VestDict = vestDict,
                    Role = role,
                };
                sw.Maps = new HashSet<string>(maps, StringComparer.OrdinalIgnoreCase);

                var setType = GenericArg(vestDict.GetType(), 1);   // HashSet<MongoId>
                var setElem = GenericArg(setType, 0);             // MongoId
                if (setType == null || setElem == null)
                {
                    WarnOnce("vest-settype:" + role + ":" + vestTpl,
                        $"[装备替换] mods '{vestTpl}' 的插槽池值类型不是 HashSet ⇒ 跳过该护甲");
                    continue;
                }

                foreach (var slotKv in vestKv.Value)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(slotKv.Key) || slotKv.Value == null || slotKv.Value.Count == 0) continue;
                        var slotName = slotKv.Key.Trim();

                        // 找该槽在 VestDict 里的真实键（大小写不敏感）；没有 ⇒ 视为新槽
                        var existing = sw.Slots.FirstOrDefault(x =>
                            string.Equals(x.Slot, slotName, StringComparison.OrdinalIgnoreCase));
                        object actualKey = existing?.ActualKey ?? FindKey(vestDict, slotName);
                        bool had = existing?.HadSlot ?? (actualKey != null);
                        object original = existing?.OriginalSet ?? (actualKey != null ? vestDict[actualKey] : null);
                        actualKey ??= slotName;

                        var replaced = NewSet(setType, setElem, slotKv.Value, out int count);
                        if (replaced == null)
                        {
                            WarnOnce("mods-set:" + vestTpl + ":" + slotName,
                                $"[装备替换] mods '{vestTpl}' {slotName} 造不出新 HashSet ⇒ 跳过该槽");
                            continue;
                        }

                        var msl = existing ?? new ModSlotSwap { Slot = slotName };
                        msl.ActualKey = actualKey;
                        msl.HadSlot = had;
                        msl.OriginalSet = original;
                        msl.ReplacedSet = replaced;
                        if (existing == null) sw.Slots.Add(msl);

                        vestDict[actualKey] = replaced;

                        // ── 插板出现概率：备份原值 → 置 100（SPT 用槽名小写查表）──
                        if (modChances != null)
                        {
                            try
                            {
                                var lower = (actualKey?.ToString() ?? slotName).ToLowerInvariant();
                                var ckt = GenericArg(modChances.GetType(), 0);
                                object ck = FindKey(modChances, lower) ?? (ckt != null ? R.Coerce(lower, ckt) : lower);
                                var mcs = _modChanceSwaps.FirstOrDefault(x =>
                                    ReferenceEquals(x.Chances, modChances) && Equals(x.Key, ck));
                                if (mcs == null)
                                {
                                    mcs = new ModChanceSwap { Chances = modChances, Key = ck, Slot = lower };
                                    try
                                    {
                                        mcs.HadChance = modChances.Contains(ck);
                                        if (mcs.HadChance) mcs.OldChance = Convert.ToDouble(modChances[ck]);
                                    }
                                    catch (Exception ex)
                                    {
                                        WarnOnce("modchance-key:" + lower,
                                            $"[装备替换] 插板概率键 '{lower}' 读取失败（按「原来没有」处理）: {ex.Message}");
                                        mcs.HadChance = false;
                                    }
                                    _modChanceSwaps.Add(mcs);
                                }
                                mcs.Maps = new HashSet<string>(maps, StringComparer.OrdinalIgnoreCase);
                                modChances[ck] = R.Coerce(100d, GenericArg(modChances.GetType(), 1));
                            }
                            catch (Exception ex)
                            {
                                WarnOnce("modchance-set:" + vestTpl + ":" + slotName,
                                    $"[装备替换] mods '{vestTpl}' {slotName} 概率置 100 失败（插板可能不出现）: {ex.Message}");
                            }
                        }

                        // ★ 需求指定的日志格式（逐槽一行）：
                        //   [装备替换] mods '5c0e51be86f774598e797894' Front_plate：2 块插板
                        Info($"[装备替换] mods '{vestTpl}' {actualKey}：{count} 块插板");
                    }
                    catch (Exception ex)
                    {
                        WarnOnce("mods-slot-ex:" + vestTpl + ":" + slotKv.Key,
                            $"[装备替换] mods '{vestTpl}' 写槽 '{slotKv.Key}' 失败（已跳过）: {ex.Message}");
                    }
                }

                if (sw.Slots.Count > 0)
                {
                    // 上一张图可能把这个"原本不存在"的护甲整键删掉了 ⇒ 套用时要确保它回来
                    if (sw.VestWasMissing) mods[sw.VestKey] = sw.VestDict;
                    if (mv == null) _modSwaps.Add(sw);
                    _appliedFor = null;
                }
            }
            catch (Exception ex)
            {
                WarnOnce("vest-ex:" + vestKv.Key,
                    $"[装备替换] 处理 mods 护甲 '{vestKv.Key}' 失败（已跳过）: {ex.Message}");
            }
        }
    }

    // ═══════════════════════════════ 还原 ═══════════════════════════════

    /// <summary>
    /// 完整还原：把**原池实例**写回 <c>Equipment[slot]</c>、把**原 HashSet 实例**写回
    /// <c>Mods[vest][slot]</c>（原本没有的整键/整槽删除），并把装备/插板概率写回原值
    /// （原来没有这个键 ⇒ 删除）；然后清空登记。逆序处理（LIFO）。
    /// </summary>
    public static void Restore()
    {
        if (_swaps.Count == 0 && _modSwaps.Count == 0 && _modChanceSwaps.Count == 0)
        {
            _appliedFor = null;
            return;
        }
        try
        {
            int n = 0, m = 0, c = 0;

            // ── 装备槽（原有行为）──
            for (int i = _swaps.Count - 1; i >= 0; i--)
            {
                var sw = _swaps[i];
                try
                {
                    sw.Equipment[sw.SlotKey] = sw.OriginalPool;
                    if (sw.Chances != null && sw.ChanceKey != null)
                    {
                        if (sw.HadChance) sw.Chances[sw.ChanceKey] = R.Coerce(sw.OldChance, GenericArg(sw.Chances.GetType(), 1));
                        else if (sw.Chances.Contains(sw.ChanceKey)) sw.Chances.Remove(sw.ChanceKey);
                    }
                    n++;
                }
                catch (Exception ex)
                {
                    WarnOnce("restore:" + sw.Role + "." + sw.Slot,
                        $"[装备替换] 还原 '{sw.Role}.{sw.Slot}' 失败（已跳过）: {ex.Message}");
                }
            }

            // ── 插板池（**先**于概率还原，保持与套用相反的顺序）──
            for (int i = _modSwaps.Count - 1; i >= 0; i--)
            {
                var mv = _modSwaps[i];
                try
                {
                    if (mv.VestWasMissing)
                    {
                        if (mv.Mods.Contains(mv.VestKey)) mv.Mods.Remove(mv.VestKey);
                    }
                    else
                    {
                        foreach (var s in mv.Slots)
                        {
                            try
                            {
                                if (s.HadSlot) mv.VestDict[s.ActualKey] = s.OriginalSet;
                                else if (mv.VestDict.Contains(s.ActualKey)) mv.VestDict.Remove(s.ActualKey);
                            }
                            catch (Exception ex)
                            {
                                WarnOnce("restore-slot:" + mv.VestTpl + ":" + s.Slot,
                                    $"[装备替换] 还原 mods '{mv.VestTpl}' {s.Slot} 失败（已跳过）: {ex.Message}");
                            }
                        }
                    }
                    m++;
                }
                catch (Exception ex)
                {
                    WarnOnce("restore-vest:" + mv.VestTpl,
                        $"[装备替换] 还原 mods '{mv.VestTpl}' 失败（已跳过）: {ex.Message}");
                }
            }

            // ── 插板出现概率 ──
            for (int i = _modChanceSwaps.Count - 1; i >= 0; i--)
            {
                var mc = _modChanceSwaps[i];
                try
                {
                    if (mc.HadChance)
                        mc.Chances[mc.Key] = R.Coerce(mc.OldChance, GenericArg(mc.Chances.GetType(), 1));
                    else if (mc.Chances.Contains(mc.Key)) mc.Chances.Remove(mc.Key);
                    c++;
                }
                catch (Exception ex)
                {
                    WarnOnce("restore-modchance:" + mc.Slot,
                        $"[装备替换] 还原插板概率 '{mc.Slot}' 失败（已跳过）: {ex.Message}");
                }
            }

            Info($"[装备替换] 已还原 {n} 条整槽替换 + {m} 条插板池替换（原池/原 HashSet 实例写回 + 概率回原值，含 {c} 条插板概率）");
        }
        catch (Exception ex) { WarnOnce("restore", $"[装备替换] 还原失败: {ex.Message}"); }
        finally
        {
            _swaps.Clear();
            _modSwaps.Clear();
            _modChanceSwaps.Clear();
            _appliedFor = null;
        }
    }

    // ═════════════════════ 按地图切换（bot 生成前）═════════════════════

    /// <summary>
    /// `BotGenerator.PrepareAndGenerateBot` 的 **prefix**（`__1` = `BotGenerationDetails`）。
    /// 在 SPT 读装备池之前，把每条替换（装备槽 + 插板池 + 两者的概率）切成"本图该有的样子"。
    /// **永远返回 true（不拦截）**。
    ///
    /// <para>地图未知 ⇒ **按新池照旧生效 + warning**（与 `MapGateDimension` 的兜底一致：
    /// 漏掉"一定戴面具"比"别的图也戴"更违背活动语义；且不这么做会让已替换的池子在非目标图残留）。</para>
    /// </summary>
    public static bool OnBotGeneratingPrefix(object __1)
    {
        if (_swaps.Count == 0 && _modSwaps.Count == 0 && _modChanceSwaps.Count == 0) return true;
        try
        {
            var map = R.Prop(__1, "Location")?.ToString();
            if (string.IsNullOrWhiteSpace(map))
            {
                WarnOnce("noloc",
                    "[装备替换] ⚠拿不到 `BotGenerationDetails.Location` ⇒ **按新池照常生效**（不做按图过滤）。");
                ApplyForMap(null);
                return true;
            }
            ApplyForMap(map);
        }
        catch (Exception ex)
        {
            WarnOnce("prefix-ex", $"[装备替换] 按图判定失败（已忽略，不影响 bot 生成）: {ex.Message}");
        }
        return true;
    }

    /// <summary>按本局地图把每条登记切成"新池"或"原池"（概率同步 100/原值）。幂等。</summary>
    private static void ApplyForMap(string map)
    {
        string key = string.IsNullOrWhiteSpace(map) ? "*" : map.Trim().ToLowerInvariant();
        if (_appliedFor == key) return;
        try
        {
            bool unknown = key == "*";
            var mapTrim = map?.Trim() ?? "";

            // ── 装备槽（原有行为）──
            foreach (var sw in _swaps)
            {
                // ★ `maps` 为空 = **不按图过滤**（全图生效）—— 不能当成"哪张图都不命中"。
                bool hit = unknown || sw.Maps.Count == 0 || sw.Maps.Contains(mapTrim);
                sw.Equipment[sw.SlotKey] = hit ? sw.ReplacedPool : sw.OriginalPool;
                if (sw.Chances != null && sw.ChanceKey != null)
                {
                    if (hit) sw.Chances[sw.ChanceKey] = R.Coerce(100d, GenericArg(sw.Chances.GetType(), 1));
                    else if (sw.HadChance) sw.Chances[sw.ChanceKey] = R.Coerce(sw.OldChance, GenericArg(sw.Chances.GetType(), 1));
                    else if (sw.Chances.Contains(sw.ChanceKey)) sw.Chances.Remove(sw.ChanceKey);
                }
            }

            // ── 插板池 ──
            foreach (var mv in _modSwaps)
            {
                bool hit = unknown || mv.Maps.Count == 0 || mv.Maps.Contains(mapTrim);
                if (hit)
                {
                    if (mv.VestWasMissing && !mv.Mods.Contains(mv.VestKey)) mv.Mods[mv.VestKey] = mv.VestDict;
                    foreach (var s in mv.Slots) mv.VestDict[s.ActualKey] = s.ReplacedSet;
                }
                else if (mv.VestWasMissing)
                {
                    // 原本不存在该护甲 ⇒ 非目标图把整键摘掉（等价于"原样"）
                    if (mv.Mods.Contains(mv.VestKey)) mv.Mods.Remove(mv.VestKey);
                }
                else
                {
                    foreach (var s in mv.Slots)
                    {
                        if (s.HadSlot) mv.VestDict[s.ActualKey] = s.OriginalSet;
                        else if (mv.VestDict.Contains(s.ActualKey)) mv.VestDict.Remove(s.ActualKey);
                    }
                }
            }

            // ── 插板出现概率 ──
            foreach (var mc in _modChanceSwaps)
            {
                bool hit = unknown || mc.Maps.Count == 0 || mc.Maps.Contains(mapTrim);
                if (hit) mc.Chances[mc.Key] = R.Coerce(100d, GenericArg(mc.Chances.GetType(), 1));
                else if (mc.HadChance) mc.Chances[mc.Key] = R.Coerce(mc.OldChance, GenericArg(mc.Chances.GetType(), 1));
                else if (mc.Chances.Contains(mc.Key)) mc.Chances.Remove(mc.Key);
            }

            _appliedFor = key;
            var slotTotal = _swaps.Count + _modSwaps.Sum(x => x.Slots.Count);
            Info(unknown
                ? $"[装备替换] 本局地图未知 ⇒ {_swaps.Count} 条整槽 + {_modSwaps.Count} 条插板池（{slotTotal} 个槽）**照常生效**（新池）"
                : $"[装备替换] 本局地图='{map}' ⇒ {_swaps.Count} 条整槽 + {_modSwaps.Count} 条插板池已按图切换（目标图={string.Join("/", _swaps.SelectMany(x => x.Maps).Concat(_modSwaps.SelectMany(x => x.Maps)).Distinct())}）");
        }
        catch (Exception ex) { WarnOnce("applymap", $"[装备替换] 按图切换池子失败（已忽略）: {ex.Message}"); }
    }

    // ═══════════════════════════════ 工具 ═══════════════════════════════

    /// <summary>取 `BotType.BotChances.EquipmentChances`（兼容 `Chances` / `Equipment` 等旧命名）。</summary>
    private static IDictionary FindChances(object botType)
    {
        foreach (var holder in new[] { "BotChances", "Chances", "chances" })
        {
            var c = R.Prop(botType, holder);
            if (c == null) continue;
            var d = R.Prop(c, "EquipmentChances") ?? R.Prop(c, "Equipment") ?? R.Prop(c, "equipment");
            if (d is IDictionary dict) return dict;
        }
        return null;
    }

    /// <summary>取 `BotType.BotChances.EquipmentModsChances`（JSON 名 `chances.equipmentMods`）。</summary>
    private static IDictionary FindModChances(object botType)
    {
        foreach (var holder in new[] { "BotChances", "Chances", "chances" })
        {
            var c = R.Prop(botType, holder);
            if (c == null) continue;
            var d = R.Prop(c, "EquipmentModsChances") ?? R.Prop(c, "EquipmentMods") ?? R.Prop(c, "equipmentMods");
            if (d is IDictionary dict) return dict;
        }
        return null;
    }

    /// <summary>按原池的**同一种字典类型**造一个新池（键类型/值类型都照抄，MongoId 用 R.Coerce 构造）。</summary>
    private static IDictionary BuildPool(object original, IDictionary equipment, Dictionary<string, double> entries)
    {
        Type poolType = original?.GetType();
        if (poolType == null || !typeof(IDictionary).IsAssignableFrom(poolType))
        {
            var ga = equipment.GetType().IsGenericType ? equipment.GetType().GetGenericArguments() : Type.EmptyTypes;
            if (ga.Length == 2 && typeof(IDictionary).IsAssignableFrom(ga[1])) poolType = ga[1];
        }
        if (poolType == null) return null;

        IDictionary dict;
        try { dict = Activator.CreateInstance(poolType) as IDictionary; } catch { dict = null; }
        if (dict == null) return null;

        var kt = GenericArg(poolType, 0) ?? typeof(string);
        var vt = GenericArg(poolType, 1) ?? typeof(double);
        foreach (var kv in entries)
        {
            try
            {
                var k = R.Coerce(kv.Key, kt);
                if (k == null) continue;
                dict[k] = R.Coerce(kv.Value, vt);
            }
            catch (Exception ex) { WarnOnce("entry:" + kv.Key, $"[装备替换] 构造池条目 {kv.Key} 失败（已跳过）: {ex.Message}"); }
        }
        return dict.Count > 0 ? dict : null;
    }

    /// <summary>
    /// 按<b>原 HashSet 的类型</b>造一个新集合，并把配置里的 tpl 逐个 <c>Add</c> 进去。
    /// <paramref name="count"/> 返回成功加入的块数；一块都没加进去 ⇒ 返回 null（调用方跳过该槽）。
    /// </summary>
    private static object NewSet(Type setType, Type elemType, Dictionary<string, double> entries, out int count)
    {
        count = 0;
        if (setType == null || elemType == null) return null;

        object set;
        try { set = Activator.CreateInstance(setType); } catch { set = null; }
        if (set == null) return null;

        MethodInfo add = null;
        try { add = setType.GetMethod("Add", new[] { elemType }); } catch { }
        if (add == null) return null;

        foreach (var kv in entries)
        {
            try
            {
                if (kv.Value <= 0) continue;                 // HashSet 无权重：≤0 视为"不要这块"
                var v = R.Coerce(kv.Key, elemType);
                if (v == null || !elemType.IsInstanceOfType(v))
                {
                    WarnOnce("mods-tpl:" + kv.Key,
                        $"[装备替换] 插板 tpl '{kv.Key}' 转不成 {elemType.Name}（已跳过）");
                    continue;
                }
                add.Invoke(set, new[] { v });
                count++;
            }
            catch (Exception ex)
            {
                WarnOnce("mods-tpl-ex:" + kv.Key,
                    $"[装备替换] 插板 tpl '{kv.Key}' 写入 HashSet 失败（已跳过）: {ex.Message}");
            }
        }
        return count > 0 ? set : null;
    }

    private static object NewInstance(Type t)
    {
        try { return t == null ? null : Activator.CreateInstance(t); } catch { return null; }
    }

    /// <summary>按字符串等价（大小写不敏感）找字典里的真实键；没有返回 null。</summary>
    private static object FindKey(IDictionary d, string s)
    {
        if (d == null || string.IsNullOrEmpty(s)) return null;
        try
        {
            foreach (var k in d.Keys)
                if (string.Equals(k?.ToString(), s, StringComparison.OrdinalIgnoreCase)) return k;
        }
        catch { }
        return null;
    }

    private static Type GenericArg(Type t, int i)
    {
        try
        {
            if (t != null && t.IsGenericType && t.GetGenericArguments().Length > i) return t.GetGenericArguments()[i];
        }
        catch { }
        return null;
    }

    // ═══════════════════════════════ 挂载 ═══════════════════════════════

    /// <summary>
    /// 挂 `BotGenerator.PrepareAndGenerateBot` 的 prefix（与 `MapGateDimension` 各挂各的，
    /// Harmony 允许多个 prefix；本 prefix 只按图切自己登记的池子）。挂不上只 warning。
    /// </summary>
    public static void Enable(Harmony harmony, ISptLogger<EventRotatorServer> log)
    {
        try
        {
            if (log != null)
            {
                Log ??= m => { try { log.Info(m); } catch { } };
                Warn ??= m => { try { log.Warning(m); } catch { } };
            }
            var gen = RuntimeTypes.Find("SPTarkov.Server.Core.Generators.Bot.BotGenerator");
            var prep = gen != null ? AccessTools.Method(gen, "PrepareAndGenerateBot") : null;
            if (prep != null)
            {
                harmony.Patch(prep, new HarmonyMethod(typeof(BotEquipmentReplaceDimension), nameof(OnBotGeneratingPrefix)),
                              null, null, null, null);
                log?.Info("[EventRotator][装备替换] 按图开关已挂载（BotGenerator.PrepareAndGenerateBot prefix）");
            }
            else log?.Warning("[EventRotator][装备替换] 找不到 BotGenerator.PrepareAndGenerateBot ⇒ 按图开关未挂载"
                            + "（池子仍是整体替换，但会全图生效）");
        }
        catch (Exception ex) { log?.Warning("[EventRotator][装备替换] 挂载失败（已忽略）: " + ex.Message); }
    }
}
