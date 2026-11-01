using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Utils;

namespace EventRotator;

/// <summary>
/// ★★★ 补全 bot 模板缺失项（task-34）：把**本体 `bots/types/&lt;role&gt;.json` 里缺的**
/// 口径→弹药 / 装备槽→物品，补进**运行期 DB 对象**（`BotTable.Types[role].Inventory`），**不写盘**。
///
/// <para><b>为什么需要</b>：本体数据有配置缺失。实例 —— <c>ravangezryachiyevent</c>（复仇小鹿）：
/// 官方 wiki（`events_wiki.txt:1049-1097`）的 Zryachiy 装备表给出
/// **RSASS + AKS-74U + SR-1MP（9x21 P gzh）**，其中 `colspan` 横幅
/// <c>Only on Vengeful Zryachiy</c>（`:1093`）只覆盖它**下面**那行（**邪教徒之刃**）
/// ⇒ SR-1MP 属于**复仇小鹿那套**。而本机模板：</para>
/// <list type="bullet">
///   <item>`ravangezryachiyevent.json`：FirstPrimaryWeapon=RSASS / SecondPrimaryWeapon=AKS-74U /
///     **Scabbard=邪教徒之刃** / **Holster=（无）** / Ammo=`545x39,762x51,9x19PARA` —— **缺 SR-1MP 与 `Caliber9x21`**</item>
///   <item>`bosszryachiy.json`（普通小鹿）：Holster=SR-1MP、Ammo 含 `Caliber9x21`（`5a26abfa…` 9x21 SP11）—— **有**</item>
/// </list>
/// <para>⚠ <b>光补弹药是没用的</b>：模板里连 9x21 的**枪**都没有（`59f98b49…` 在整份文件里出现 **0** 次）
/// ⇒ 本维度同时支持补 <c>equipment</c> 槽。</para>
///
/// <para><b>★ 读点（关键，决定这条是不是死的）</b>：本机 SPT 的
/// <c>BotWeaponGenerator.GetWeightedCompatibleAmmo</c>（`BotWeaponGenerator.cs:446-449`）读的正是
/// <c>botTemplateInventory.Ammo</c> = `BotType.Inventory.Ammo` ⇒ **我们写它会被读到</b>。
/// 而 APBS（`acidphantasm-progressivebotsystem`）走的是**它自己的**弹药表
/// （`CustomBotWeaponGenerator…:281-282` `GetAmmoByBotRole(role, tier)` ⇒ `Data/Ammo/Tier*_ammo.json` 的
/// <c>bossAmmo/scavAmmo/pmcAmmo</c>）—— **但那要求 APBS 真的接管**这个 role。
/// 本 role **不会**被接管：APBS <c>BotActivityHelper.IsBotDisabled</c> 里
/// <c>if (_events.Contains(botType)) return true;</c> ⇒ **事件 bot 一律 disabled** ⇒
/// <c>IsBotEnabled("ravangezryachiyevent")==false</c> ⇒ APBS 的 Prefix <c>return true</c> ⇒ **回落原版 SPT**。
/// （旁证：APBS 的 `Data/Equipment/Tier0..7_equipment.json` 24 个角色键里**没有** `ravangezryachiyevent`。）</para>
///
/// <para><b>后果有多严重</b>：即使口径缺失，`GetWeightedCompatibleAmmo`
/// （`BotWeaponGenerator.cs:449-465`）也**不会给空弹匣** —— 它会退到
/// ① `weaponTemplate.Properties.DefAmmo`，② 再不行取 `Chambers[0].Filters[0].Filter[0]`。
/// ⇒ **不是"没弹"，是"拿到默认弹（通常更差）"**。本维度的价值是把弹换回**官方指定的**那种。</para>
///
/// <para><b>可还原</b>：写入前把原值快照进 <c>_restore</c>（活动关闭时按**逆序 LIFO** 回放：
/// 原来没有该键 ⇒ 删键；原来有 ⇒ 写回原值）。<b>幂等</b>：值已相同则不写、不重复登记。
/// <b>可降级</b>：role/口径/槽找不到或结构不符 ⇒ 逐项 try/catch + warning。</para>
/// </summary>
public static class EventBotTemplateFix
{
    private static ISptLogger<EventRotatorServer> _log;

    /// <summary>roleLowercase → 要补的内容。</summary>
    private static readonly Dictionary<string, BotTemplateFixDef> _fixes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>还原动作（**逆序 LIFO 回放**，与其它维度一致）。</summary>
    private static readonly List<Action> _restore = new();

    /// <summary>`BotTable`（拿 `Types[role]`）。</summary>
    private static object _botTable;

    /// <summary>本轮日志去重。</summary>
    private static readonly HashSet<string> _logged = new(StringComparer.OrdinalIgnoreCase);

    public static void Init(ISptLogger<EventRotatorServer> log, object botTable)
    {
        _log = log;
        _botTable = botTable;
    }

    public static bool IsEmpty => _fixes.Count == 0;

    /// <summary>活动套用阶段登记（role 由 Plugin 从字典键回填）。</summary>
    public static void AddFix(BotTemplateFixDef def)
    {
        if (def?.Role == null) return;
        var r = def.Role.Trim();
        if (r.Length == 0) return;
        _fixes[r] = def;
    }

    /// <summary>还原 + 清空（**逆序 LIFO**，与 `_restoreActions` 语义一致）。</summary>
    public static void Restore()
    {
        for (int i = _restore.Count - 1; i >= 0; i--)
        {
            try { _restore[i](); } catch (Exception ex) { SafeWarn($"[弹药补全] 还原失败（已跳过）: {ex.GetType().Name}"); }
        }
        _restore.Clear();
        _fixes.Clear();
        _logged.Clear();
    }

    /// <summary>把所有登记的补全应用到运行期 DB。可在每轮套用后调用（幂等）。</summary>
    public static void Apply()
    {
        if (IsEmpty || _botTable == null) return;
        try
        {
            var types = Prop(_botTable, "Types") as IDictionary;
            if (types == null) { SafeWarn("[弹药补全] 拿不到 BotTable.Types（已跳过）"); return; }

            foreach (var kv in _fixes)
            {
                var role = kv.Key;
                var def = kv.Value;
                object botType = null;
                foreach (DictionaryEntry e in types)
                    if (string.Equals(e.Key?.ToString(), role, StringComparison.OrdinalIgnoreCase)) { botType = e.Value; break; }
                if (botType == null) { SafeWarn($"[弹药补全] BotTable.Types 里找不到 role '{role}'（已跳过）"); continue; }

                // ★ `BotType` 的 CLR 真名是 **`BotInventory`**（`[JsonPropertyName("inventory")]`），
                //   不是 `Inventory` —— 这里先按 CLR 名、再按 JSON 名走四档反查，并把"试过什么"打出来。
                var tried = new List<string>();
                var inv = Prop(botType, "BotInventory", tried) ?? Prop(botType, "inventory", tried);
                if (inv == null)
                {
                    SafeWarn($"⚠ [弹药补全] '{role}'.BotInventory 取不到"
                           + $"（试过：{string.Join(" / ", tried.Distinct())}）⇒ 本维度未生效");
                    continue;
                }

                if (def.Ammo != null && def.Ammo.Count > 0) FixAmmo(role, inv, def.Ammo);
                if (def.Equipment != null && def.Equipment.Count > 0) FixEquipment(role, inv, def.Equipment);
            }
        }
        catch (Exception ex) { SafeWarn($"[弹药补全] 应用失败（已跳过）: {ex.GetType().Name}"); }
    }

    // ─────────────────────────── 口径 → 弹药 ───────────────────────────

    private static void FixAmmo(string role, object inv, Dictionary<string, string> want)
    {
        try
        {
            var ammo = Prop(inv, "Ammo") as IDictionary;
            if (ammo == null) { SafeWarn($"⚠ [弹药补全] '{role}'.BotInventory.Ammo 取不到（试过 Ammo / ammo / JsonPropertyName 反查）⇒ 本维度未生效"); return; }

            // ★★ 真实类型是 `Dictionary<string, Dictionary<MongoId, double>>` ——
            //    **内层字典的键是 `MongoId`（struct），不是 string**！
            //    直接 `pool[tpl] = 1.0`（string 键）会抛 ArgumentException 被 catch 吞掉
            //    ⇒ **整个维度静默失效**（这正是第一版实跑降级的第二个原因）。
            //    所以内外两层键都必须按"真实键类型"构造。
            var outKeyT = ammo.GetType().GetGenericArguments()[0];
            var poolT = ammo.GetType().GetGenericArguments()[1];
            var inKeyT = poolT.GetGenericArguments()[0];

            foreach (var kv in want)
            {
                var caliber = (kv.Key ?? "").Trim();
                var tpl = (kv.Value ?? "").Trim();
                if (caliber.Length == 0 || tpl.Length == 0) continue;

                var ok = Key(inKeyT, tpl, out var inKey);
                if (!ok) { SafeWarn($"[弹药补全] {role}: 造不出 {inKeyT.Name} 键（tpl={tpl}）⇒ 跳过"); continue; }

                if (!Key(outKeyT, caliber, out var outKey)) { SafeWarn($"[弹药补全] {role}: 造不出 {outKeyT.Name} 键（caliber={caliber}）⇒ 跳过"); continue; }
                if (ammo.Contains(outKey))
                {
                    if (ammo[outKey] is IDictionary pool)
                    {
                        if (pool.Contains(inKey)) { if (_logged.Add("ammo:" + role + ":" + caliber)) _log?.Info($"[弹药补全] {role}: {caliber} 已存在且已指向 {tpl}（无需改动，幂等）"); continue; }
                        var before = Describe(pool);
                        pool[inKey] = 1.0;
                        var self = pool; var k = inKey;
                        _restore.Add(() => { try { self.Remove(k); } catch { } });
                        _log?.Info($"[弹药补全] {role}: {caliber} 池子 [{before}] → 追加 {tpl}（回读命中 {CountOf(self, k)} 条 ✔，可还原）");
                        continue;
                    }
                    SafeWarn($"[弹药补全] {role}: {caliber} 已存在但结构不是字典（已跳过）");
                    continue;
                }

                var newPool = Activator.CreateInstance(poolT);
                if (newPool is not IDictionary np) { SafeWarn($"[弹药补全] {role}: 造不出 {caliber} 的池子（已跳过）"); continue; }
                np[inKey] = 1.0;
                ammo[outKey] = np;
                _restore.Add(() => { try { ammo.Remove(outKey); } catch { } });
                _log?.Info($"[弹药补全] {role}: {caliber} → {tpl}（回读命中 {CountOf(np, inKey)} 条 ✔，可还原）");
            }
        }
        catch (Exception ex) { SafeWarn($"[弹药补全] '{role}' 弹药补全失败（已跳过）: {ex.GetType().Name}"); }
    }

    // ─────────────────────────── 装备槽 → 物品 ───────────────────────────

    private static void FixEquipment(string role, object inv, Dictionary<string, Dictionary<string, double>> want)
    {
        try
        {
            var eq = Prop(inv, "Equipment") as IDictionary;
            if (eq == null) { SafeWarn($"⚠ [弹药补全] '{role}'.BotInventory.Equipment 取不到（试过 Equipment / equipment / JsonPropertyName 反查）⇒ 本维度未生效"); return; }

            var outKeyT = eq.GetType().GetGenericArguments()[0];
            var poolT = eq.GetType().GetGenericArguments()[1];
            var inKeyT = poolT.GetGenericArguments()[0];

            foreach (var kv in want)
            {
                var slot = (kv.Key ?? "").Trim();
                if (slot.Length == 0 || kv.Value == null || kv.Value.Count == 0) continue;

                // ★★ 外层键的真实类型是**枚举** `SPTarkov.Server.Core.Models.Enums.EquipmentSlots`（实测），
                //    不是 string ⇒ 必须先把槽名转成枚举值再 Contains/索引/Remove，
                //    否则：① `Contains("Holster")` 恒 false（"空槽"被误判成"槽不存在"）
                //          ② 新建会**覆盖**本体原有的空槽
                //          ③ 还原时 `eq.Remove(枚举值)` 会把**整个槽删掉** —— 破坏本体数据。
                if (!Key(outKeyT, slot, out var slotKey)) { SafeWarn($"[弹药补全] {role}: 造不出 {outKeyT.Name} 键（slot={slot}）⇒ 跳过"); continue; }

                // ★★★ P1-3【verify-a 审计 · 容器守卫】本函数**不校验槽名**（当前唯一使用者 halloween_2023 用的是
                //   `Holster` 武器槽 ⇒ 现状安全），但将来若有人写 `"Backpack"`/`"Pockets"`/`"TacticalVest"` 就会踩：
                //   容器槽里塞进没有 `Properties.Grids` 的 tpl ⇒ `BotInventoryContainerService.ContainerDetails..ctor`
                //   的 `foreach (Grid grid in ...Grids)` NRE ⇒ `Failed to generate bot`、整批 bot 刷不出来。
                //   ⇒ 这里**逐条过滤**（两条写入路径——已有槽追加 / 新建槽——都走这份过滤后的清单）。
                var safeItems = kv.Value.Where(it => ExtraDimensions.CanPutIntoEquipmentSlot(slot, it.Key)).ToList();
                if (safeItems.Count == 0)
                {
                    SafeWarn($"[弹药补全] {role}: 槽 {slot} 的条目全被容器守卫拦下（模板无 Grids）⇒ 跳过该槽");
                    continue;
                }

                if (eq.Contains(slotKey))
                {
                    if (eq[slotKey] is IDictionary pool)
                    {
                        if (pool.Count == 0)
                            _log?.Info($"[弹药补全] {role}: 槽 {slot} 本来是**空槽**（本体给了槽但没给东西）⇒ 补入");
                        foreach (var it in safeItems)
                        {
                            if (!Key(inKeyT, it.Key, out var ik)) continue;
                            if (pool.Contains(ik)) continue;
                            pool[ik] = it.Value;
                            var self = pool; var k = ik;
                            _restore.Add(() => { try { self.Remove(k); } catch { } });
                            _log?.Info($"[弹药补全] {role}: 槽 {slot} 追加 {it.Key}（回读命中 {CountOf(self, k)} 条 ✔，可还原）");
                        }
                    }
                    else SafeWarn($"[弹药补全] {role}: 槽 {slot} 结构不是字典（已跳过）");
                    continue;
                }

                var np2 = Activator.CreateInstance(poolT);
                if (np2 is not IDictionary n2) { SafeWarn($"[弹药补全] {role}: 造不出槽 {slot}（已跳过）"); continue; }
                foreach (var it in safeItems)
                    if (Key(inKeyT, it.Key, out var ik2)) n2[ik2] = it.Value;
                eq[slotKey] = n2;
                _restore.Add(() => { try { eq.Remove(slotKey); } catch { } });
                _log?.Info($"[弹药补全] {role}: 槽 {slot} → {safeItems[0].Key}（回读命中 {CountOf(n2, Key(inKeyT, safeItems[0].Key, out var fk) ? fk : null)} 条 ✔，可还原）");
            }
        }
        catch (Exception ex) { SafeWarn($"[弹药补全] '{role}' 装备补全失败（已跳过）: {ex.GetType().Name}"); }
    }

    // ─────────────────────────── 小工具 ───────────────────────────

    private static string Describe(IDictionary d)
    {
        try { return string.Join(",", d.Keys.Cast<object>().Select(k => k?.ToString())); } catch { return "?"; }
    }

    private static int CountOf(IDictionary d, object key) { try { return d != null && key != null && d.Contains(key) ? 1 : 0; } catch { return 0; } }

    /// <summary>
    /// 把字符串转成**该字典真实的键类型**（`string` 原样；`MongoId` 之类走 (string) 构造 → op_Implicit → Convert）。
    /// ⚠ `MongoId` 还要求 **24 位十六进制**，否则构造抛异常 ⇒ 返回 false 让调用方跳过（不崩）。
    /// </summary>
    private static bool Key(Type keyType, string s, out object key)
    {
        key = null;
        if (keyType == null || string.IsNullOrEmpty(s)) return false;
        if (keyType == typeof(string)) { key = s; return true; }
        // ★★ **枚举键必须先单独处理**：`Equipment` 的外层键实测是枚举
        //    `SPTarkov.Server.Core.Models.Enums.EquipmentSlots`，而
        //    `Convert.ChangeType("Holster", 枚举类型)` **不支持 string→enum**（会抛 InvalidCastException），
        //    `Activator.CreateInstance(枚举类型, "Holster")` 也不支持 ⇒ 两档都会失败
        //    ⇒ 整个装备槽补全静默失效。这里用 `Enum.Parse`。
        if (keyType.IsEnum)
        {
            try { key = Enum.Parse(keyType, s, ignoreCase: true); return key != null; }
            catch { return false; }
        }
        try { key = Activator.CreateInstance(keyType, s); return key != null; }
        catch
        {
            try
            {
                var mi = keyType.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (mi != null) { key = mi.Invoke(null, new object[] { s }); return key != null; }
                key = Convert.ChangeType(s, keyType);
                return key != null;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// ★★★ 跨命名约定读属性（**含 `[JsonPropertyName]` 反查**）。
    ///
    /// <para>本项目已经**四次**撞上"CLR 属性名 ≠ JSON 名"，每次都是**编译过、跑不崩、功能完全失效**：</para>
    /// <list type="bullet">
    ///   <item>`Item.Id` vs JSON `_id`、`Item.Template` vs `_tpl`</item>
    ///   <item>`Slot.Properties` vs `_props`、`Slot.Name` vs `_name`、`SlotProperties.Filters` vs `filters`</item>
    ///   <item>`TemplateItemProperties.AmmoType` vs `ammoType`</item>
    ///   <item>★★ **`BotType.BotInventory` vs `inventory`**（`BotType` 的 10 个属性**全都带 `Bot` 前缀**：
    ///     <c>BotAppearance/BotChances/BotDifficulty/BotExperience/BotGeneration/BotHealth/BotInventory/BotSkills</c>，
    ///     只有 `FirstNames`/`LastNames` 例外）—— 这正是 `botAmmoAdd` 实跑降级的原因</item>
    /// </list>
    ///
    /// <para>所以这里按**四档**依次尝试，最后一档是**按 `[JsonPropertyName]` 反查 CLR 名**（治本的那一档）：
    /// ① 原名 → ② 去/加下划线 → ③ `[JsonPropertyName(name)]` 反查 → ④ 忽略大小写（含去下划线归一）。</para>
    /// </summary>
    private static object Prop(object o, string name, List<string> tried = null)
    {
        if (o == null || string.IsNullOrEmpty(name)) return null;
        var t = o.GetType();
        var stripped = name.TrimStart('_');
        var norm = Normalize(name);
        try
        {
            // ① 原名（含加/去下划线）
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                 ?? (stripped != name ? t.GetProperty(stripped, BindingFlags.Public | BindingFlags.Instance)
                                      : t.GetProperty("_" + name, BindingFlags.Public | BindingFlags.Instance));
            if (p != null) { tried?.Add(p.Name + " (CLR 直配)"); return p.GetValue(o); }
            tried?.Add(name);

            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            // ③ ★ 按 [JsonPropertyName] 反查（治本档）
            foreach (var q in props)
            {
                var jn = q.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name;
                if (jn != null && string.Equals(jn, name, StringComparison.OrdinalIgnoreCase))
                { tried?.Add(q.Name + " ([JsonPropertyName(" + jn + ")] 反查)"); return q.GetValue(o); }
            }
            // ③b 按 JsonPropertyName 归一化（去下划线）反查，兜住 _max_count ↔ max_count 这类
            foreach (var q in props)
            {
                var jn = q.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name;
                if (jn != null && Normalize(jn) == norm)
                { tried?.Add(q.Name + " ([JsonPropertyName(" + jn + ")] 归一化反查)"); return q.GetValue(o); }
            }

            // ④ 忽略大小写 / 去下划线归一
            var hit = props.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(x.Name, stripped, StringComparison.OrdinalIgnoreCase)
                                             || Normalize(x.Name) == norm);
            if (hit != null) { tried?.Add(hit.Name + " (忽略大小写/归一化)"); return hit.GetValue(o); }

            tried?.Add("✗ 四档都没命中");
            return null;
        }
        catch { return null; }
    }

    /// <summary>归一化：去下划线 + 小写（让 `_max_count` ↔ `MaxCount`、`BotInventory` ↔ `botinventory` 可比）。</summary>
    private static string Normalize(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("_", "").ToLowerInvariant();

    /// <summary>同 <see cref="Prop"/>，但写值（同样四档定位）。</summary>
    private static bool SetProp(object o, string name, object val)
    {
        if (o == null || string.IsNullOrEmpty(name)) return false;
        var t = o.GetType();
        var stripped = name.TrimStart('_');
        var norm = Normalize(name);
        try
        {
            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var p = props.FirstOrDefault(x => x.Name == name || x.Name == stripped || x.Name == "_" + name)
                 ?? props.FirstOrDefault(x =>
                    {
                        var jn = x.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name;
                        return jn != null && (string.Equals(jn, name, StringComparison.OrdinalIgnoreCase) || Normalize(jn) == norm);
                    })
                 ?? props.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(x.Name, stripped, StringComparison.OrdinalIgnoreCase)
                                           || Normalize(x.Name) == norm);
            if (p == null || !p.CanWrite) return false;
            var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            object v = val;
            if (pt == typeof(string)) v = val?.ToString();
            else if (val is string sv && pt != typeof(string))
            {
                try { v = Activator.CreateInstance(pt, sv); }
                catch
                {
                    var mi = pt.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                    if (mi != null) v = mi.Invoke(null, new object[] { sv });
                    else v = Convert.ChangeType(sv, pt);
                }
            }
            p.SetValue(o, v);
            return true;
        }
        catch { return false; }
    }

    private static void SafeWarn(string m) { try { _log?.Warning(m); } catch { } }
}
