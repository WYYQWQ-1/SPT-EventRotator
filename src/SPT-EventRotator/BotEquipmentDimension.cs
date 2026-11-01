using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SPTarkov.Common.Models.Logging;

using System.Reflection;
namespace EventRotator;

/// <summary>
/// ★ bot 装备池维度（v2.4.0）—— 覆盖 TODO #2 剩下的"火箭筒 SCAV"。
///
/// 实证（避免重复实现）：
///   · **大弹鼓**：本体 `bots/types/assault.json → inventory.mods` 里**已经有**
///     RPK-16 95发 / AK 73发 / AR-15 MAG5-60 / Magpul D-60 → **不需要实现**。
///   · **Tagilla 镰刀**：本体 `configs/seasonalevents.json → eventGear.halloween.bossTagilla.Scabbard`
///     已经含 `Old hand scythe (63495c500c297e20065a08b1): 10`，且 `infectedtagilla.json` 自带
///     → **只要强制万圣节季节就自动生效，不需要实现**。
///   · **火箭筒 SCAV**：`assault.json` 里**没有**任何榴弹发射器 → **需要本维度**。
///
/// 落点：`BotTable.Types[role].Inventory.Equipment[slot]` = Dictionary&lt;MongoId,int&gt;（物品 → 权重）
///       槽位名：FirstPrimaryWeapon / SecondPrimaryWeapon / Holster / Scabbard / Headwear / …
///
/// config 用法（events.json）：
/// <code>
/// "botAddItems": {
///   "assault":      { "FirstPrimaryWeapon": { "6275303a9f372d6ea97f9ec7": 150 } },
///   "assaultgroup": { "FirstPrimaryWeapon": { "5d52cc5ba4b9367408500062": 120 } }
/// }
/// </code>
/// 全部可还原。
/// </summary>
internal static class BotEquipmentDimension
{
    public static object BotTable;
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][bot装备] " + m); } catch { } }

    /// <summary>★ v1.0.1【性能·第六轮】元数据查找改走 R.GP / R.GF / R.GFs 缓存
    /// （`GetFields` 原来**每次调用都分配一个新数组**）；控制流与异常语义逐字保留。</summary>
    private static object Prop(object o, string n)
    {
        if (o == null) return null;
        var t = o.GetType();
        var p = R.GP(t, n, BindingFlags.Public | BindingFlags.Instance);
        if (p != null) return p.GetValue(o);
        var f = R.GF(t, n, BindingFlags.Public | BindingFlags.Instance);
        if (f != null) return f.GetValue(o);
        foreach (var f2 in R.GFs(t, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
            if (f2.Name.Contains(n)) return f2.GetValue(o);
        return null;
    }

    public static void Apply(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            if (ev.BotAddItems == null || ev.BotAddItems.Count == 0) return;
            if (BotTable == null) { logger?.Warning("[EventRotator][bot装备] 拿不到 BotTable"); return; }

            var types = Prop(BotTable, "Types") as IDictionary;
            if (types == null) { logger?.Warning("[EventRotator][bot装备] BotTable.Types 不是字典"); return; }

            foreach (var roleKv in ev.BotAddItems)
            {
                // 找角色（不区分大小写）
                object roleKey = null;
                foreach (var k in types.Keys)
                    if (string.Equals(k?.ToString(), roleKv.Key, StringComparison.OrdinalIgnoreCase)) { roleKey = k; break; }
                if (roleKey == null) { logger?.Warning($"[EventRotator][bot装备] 没有 bot 角色 '{roleKv.Key}'"); continue; }

                var botType = types[roleKey];
                var inv = BotInventoryOf(botType);
                var eq = Prop(inv, "Equipment") as IDictionary;
                if (eq == null) { logger?.Warning($"[EventRotator][bot装备] '{roleKv.Key}' 没有 Inventory.Equipment"); continue; }

                foreach (var slotKv in roleKv.Value)
                {
                    object slotKey = null;
                    foreach (var k in eq.Keys)
                        if (string.Equals(k?.ToString(), slotKv.Key, StringComparison.OrdinalIgnoreCase)) { slotKey = k; break; }
                    if (slotKey == null) { logger?.Warning($"[EventRotator][bot装备] '{roleKv.Key}' 没有槽位 '{slotKv.Key}'"); continue; }

                    var pool = eq[slotKey] as IDictionary;
                    if (pool == null)
                    {
                        // 槽位是空字典 → 造一个
                        var pt = eq[slotKey]?.GetType();
                        if (pt == null)
                        {
                            // 拿到泛型参数类型
                            var eqType = eq.GetType().GetGenericArguments();
                            if (eqType.Length < 2) { logger?.Warning($"[EventRotator][bot装备] 猜不出 '{slotKv.Key}' 的字典类型"); continue; }
                            try { pool = Activator.CreateInstance(eqType[1]) as IDictionary; } catch { }
                            if (pool == null) continue;
                            var capEq = eq; var capSlot = slotKey;
                            AddRestore?.Invoke(() => { try { capEq[capSlot] = null; } catch { } });
                            eq[slotKey] = pool;
                        }
                        else
                        {
                            try { pool = Activator.CreateInstance(pt) as IDictionary; } catch { }
                            if (pool == null) continue;
                            var capEq = eq; var capSlot = slotKey;
                            AddRestore?.Invoke(() => { try { capEq[capSlot] = null; } catch { } });
                            eq[slotKey] = pool;
                        }
                    }

                    // 键类型（MongoId）
                    var keyType = pool.GetType().GetGenericArguments().FirstOrDefault();

                    foreach (var itemKv in slotKv.Value)
                    {
                        // ★★★ P1-2【verify-a 审计 · 容器守卫】容器槽（Pockets / TacticalVest / Backpack /
                        //   SecuredContainer）里塞进**没有 `Properties.Grids` 的物品模板** ⇒
                        //   `BotInventoryContainerService.ContainerDetails..ctor` 的
                        //   `foreach (Grid grid in ...Grids)` 直接 NRE ⇒ 客户端 `Failed to generate bot (assault)`、
                        //   **整批 bot 刷不出来**（线上日志 + 数据实证）。守卫与 `ExtraDimensions` 共用同一实现，
                        //   命中时只跳过**这一条物品**并**只提示一次**（不影响同槽其它条目）。
                        if (!ExtraDimensions.CanPutIntoEquipmentSlot(slotKv.Key, itemKv.Key)) continue;

                        object itemKey = null;
                        foreach (var k in pool.Keys)
                            if (string.Equals(k?.ToString(), itemKv.Key, StringComparison.OrdinalIgnoreCase)) { itemKey = k; break; }
                        if (itemKey == null)
                        {
                            // 造 MongoId
                            try
                            {
                                if (keyType != null)
                                {
                                    try { itemKey = Convert.ChangeType(itemKv.Key, keyType); }
                                    catch { itemKey = Activator.CreateInstance(keyType, itemKv.Key); }
                                }
                            }
                            catch { }
                            if (itemKey == null) { logger?.Warning($"[EventRotator][bot装备] 无法构造物品键 {itemKv.Key}"); continue; }
                        }

                        int old = -1;
                        bool existed = pool.Contains(itemKey);
                        if (existed) { try { old = Convert.ToInt32(pool[itemKey]); } catch { } }
                        var capPool = pool; var capKey = itemKey; var capExisted = existed; var capOld = old;
                        AddRestore?.Invoke(() =>
                        {
                            try
                            {
                                if (capExisted) capPool[capKey] = capOld;
                                else if (capPool.Contains(capKey)) capPool.Remove(capKey);
                            }
                            catch { }
                        });
                        // ★ 值类型可能是 Double（实测报错：The value "150" is not of type "System.Double"）
                        var valType = pool.GetType().GetGenericArguments().LastOrDefault();
                        object boxed = itemKv.Value;
                        try { if (valType != null) boxed = Convert.ChangeType(itemKv.Value, valType); }
                        catch { boxed = itemKv.Value; }
                        pool[itemKey] = boxed;
                        Info($"{roleKv.Key}.{slotKv.Key} ← 物品 {itemKv.Key} 权重 {(existed ? old.ToString() : "(新增)")} → {itemKv.Value}（可还原）");
                    }
                }
            }
        }
        catch (Exception ex) { logger?.Error($"[EventRotator][bot装备] 失败: {ex.Message}"); }
    }

    /// <summary>
    /// ★ v2.4.1：往 <c>Inventory.Mods[武器id][槽位]</c> 里加配件（列表结构，无权重）。
    ///   用于「大弹鼓 SCAV」（往更多武器的 mod_magazine 里塞弹鼓）和「下挂榴弹 SCAV」（mod_launcher）。
    ///   ⚠ 列表无权重 → 通过**重复添加**提高被抽中概率（去掉时按首次记录的原列表整体还原）。
    /// </summary>
    public static void ApplyMods(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            if ((ev.BotAddMods == null || ev.BotAddMods.Count == 0)
                && (ev.BotLootAdd_ == null || ev.BotLootAdd_.Count == 0)) return;
            if (BotTable == null) return;
            var types = Prop(BotTable, "Types") as IDictionary;
            if (types == null) return;

            if (ev.BotAddMods != null)
                foreach (var roleKv in ev.BotAddMods)
                {
                    var (roleKey, botType) = FindRole(types, roleKv.Key);
                    if (roleKey == null) { logger?.Warning($"[EventRotator][bot配件] 没有角色 '{roleKv.Key}'"); continue; }
                    var mods = Prop(BotInventoryOf(botType), "Mods") as IDictionary;
                    if (mods == null) { logger?.Warning($"[EventRotator][bot配件] '{roleKv.Key}' 没有 Inventory.Mods"); continue; }

                    foreach (var wpnKv in roleKv.Value)
                    {
                        // 武器键支持 "*" = 所有已存在的武器
                        var targets = new List<object>();
                        if (wpnKv.Key == "*") foreach (var k in mods.Keys) targets.Add(k);
                        else
                        {
                            foreach (var k in mods.Keys)
                                if (string.Equals(k?.ToString(), wpnKv.Key, StringComparison.OrdinalIgnoreCase)) { targets.Add(k); break; }
                        }
                        if (targets.Count == 0) { logger?.Warning($"[EventRotator][bot配件] '{roleKv.Key}' 没有武器 '{wpnKv.Key}'"); continue; }

                        foreach (var tgt in targets)
                        {
                            var slotMap = mods[tgt] as IDictionary;
                            if (slotMap == null) continue;
                            foreach (var slotKv in wpnKv.Value)
                            {
                                object slotKey = null;
                                foreach (var k in slotMap.Keys)
                                    if (string.Equals(k?.ToString(), slotKv.Key, StringComparison.OrdinalIgnoreCase)) { slotKey = k; break; }
                                if (slotKey == null) continue;
                                // ★ 实证：内层是 HashSet<MongoId>（不是 List）→ 不能用 IList，要反射调 Add
                                var coll = slotMap[slotKey];
                                if (coll == null) { logger?.Warning($"[EventRotator][bot配件] '{slotKv.Key}' 值为 null"); continue; }
                                var addM = coll.GetType().GetMethod("Add");
                                var clearM = coll.GetType().GetMethod("Clear");
                                if (addM == null) { logger?.Warning($"[EventRotator][bot配件] '{slotKv.Key}' 类型 {coll.GetType().Name} 没有 Add"); continue; }

                                // 快照用于还原
                                var snapshot = new List<object>();
                                foreach (var x in (System.Collections.IEnumerable)coll) snapshot.Add(x);
                                var capColl = coll; var capClear = clearM; var capAdd = addM;
                                AddRestore?.Invoke(() =>
                                {
                                    try
                                    {
                                        capClear?.Invoke(capColl, null);
                                        foreach (var x in snapshot) capAdd.Invoke(capColl, new[] { x });
                                    }
                                    catch { }
                                });

                                // 键类型（MongoId）
                                var elemType = coll.GetType().GetGenericArguments().FirstOrDefault();
                                int added = 0;
                                foreach (var item in slotKv.Value)
                                {
                                    object elem = item;
                                    if (elemType != null)
                                    {
                                        try { elem = Convert.ChangeType(item, elemType); }
                                        catch { try { elem = Activator.CreateInstance(elemType, item); } catch { } }
                                    }
                                    try { addM.Invoke(coll, new[] { elem }); added++; } catch { }
                                }
                                Info($"{roleKv.Key}.Mods[{tgt}].{slotKv.Key} += {added} 种（HashSet 扩池，可还原）");
                            }
                        }
                    }
                }

            // ── Inventory.Items（背包/口袋/战术背心）加权 ──
            if (ev.BotLootAdd_ != null)
                foreach (var roleKv in ev.BotLootAdd_)
                {
                    var (roleKey, botType) = FindRole(types, roleKv.Key);
                    if (roleKey == null) continue;
                    // ★ 实证：Inventory.Items 是 ItemPools **类**（属性 Backpack/Pockets/SecuredContainer/SpecialLoot/TacticalVest），
                    //   不是字典 → 要用属性名取。
                    var itemPools = Prop(BotInventoryOf(botType), "Items");
                    if (itemPools == null) { logger?.Warning($"[EventRotator][bot携带] '{roleKv.Key}' 没有 Inventory.Items"); continue; }
                    foreach (var slotKv in roleKv.Value)
                    {
                        var pool = Prop(itemPools, slotKv.Key) as IDictionary;
                        if (pool == null) { logger?.Warning($"[EventRotator][bot携带] '{roleKv.Key}' 没有 '{slotKv.Key}' 池"); continue; }
                        var keyType = pool.GetType().GetGenericArguments().FirstOrDefault();
                        foreach (var itemKv in slotKv.Value)
                        {
                            object itemKey = null;
                            foreach (var k in pool.Keys)
                                if (string.Equals(k?.ToString(), itemKv.Key, StringComparison.OrdinalIgnoreCase)) { itemKey = k; break; }
                            if (itemKey == null && keyType != null)
                            {
                                try { itemKey = Convert.ChangeType(itemKv.Key, keyType); }
                                catch { try { itemKey = Activator.CreateInstance(keyType, itemKv.Key); } catch { } }
                            }
                            if (itemKey == null) continue;
                            bool existed = pool.Contains(itemKey);
                            int old = 0; if (existed) { try { old = Convert.ToInt32(pool[itemKey]); } catch { } }
                            var vT = pool.GetType().GetGenericArguments().LastOrDefault();
                            object boxed2 = itemKv.Value;
                            try { if (vT != null) boxed2 = Convert.ChangeType(itemKv.Value, vT); }
                            catch { boxed2 = itemKv.Value; }

                            // ★★★ v2.6.38【判据必须是**本活动**的 `onlyOnMap`】★★★
                            //   ⚠ v2.6.37 回归：这里原本用**全局**判据 `MapGateDimension.HasFilter`
                            //     ⇒ 只要**有任何一个**活动声明了 `onlyOnMap`（如 `june2022_toxin`），
                            //        **所有活动**的 `botLootAdd_` 条目都会被改道到"按图登记"，
                            //        而目标图取的是本活动的 `ev.OnlyOnMap`（对它们是 null）
                            //     ⇒ 实测：`hustle` / `instant_hideout_build` 的啤酒（`62a09f32…`）
                            //        **只在 bigmap 出现、别的图全丢** ✗（误伤回归基线 A-11）
                            //   ⇒ 现在：**只有本活动自己声明了**才改道；没声明的走下面原路（当场写池）。
                            if (!string.IsNullOrEmpty(ev.OnlyOnMap)
                                && MapGateDimension.RegisterPoolEntry(pool, itemKey, boxed2, ev.OnlyOnMap))
                            {
                                Info($"{roleKv.Key}.Items.{slotKv.Key} ← {itemKv.Key} 权重 {(existed ? old.ToString() : "新增")} → {itemKv.Value}"
                                   + $"（**按图登记**：仅 '{ev.OnlyOnMap}' 生效；其余地图该条目不进池）");
                                continue;
                            }

                            var capPool = pool; var capKey = itemKey; var capEx = existed; var capOld = old;
                            AddRestore?.Invoke(() => { try { if (capEx) capPool[capKey] = capOld; else if (capPool.Contains(capKey)) capPool.Remove(capKey); } catch { } });
                            pool[itemKey] = boxed2;
                            Info($"{roleKv.Key}.Items.{slotKv.Key} ← {itemKv.Key} 权重 {(existed ? old.ToString() : "新增")} → {itemKv.Value}（可还原）");
                        }
                    }
                }
        }
        catch (Exception ex) { logger?.Error($"[EventRotator][bot配件] 失败: {ex.Message}"); }
    }

    /// <summary>重复次数（list 结构靠重复提高概率）。</summary>
    

    private static (object, object) FindRole(IDictionary types, string name)
    {
        foreach (var k in types.Keys)
            if (string.Equals(k?.ToString(), name, StringComparison.OrdinalIgnoreCase)) return (k, types[k]);
        return (null, null);
    }

    /// <summary>
    /// ★ v0.1.3 取 bot 模板的库存对象。
    /// 实测：`BotType` 上的真实属性名是 **`BotInventory`**（JSON 名才是 `inventory`），
    /// 旧代码写 `Prop(botType, "Inventory")` 只是**靠 Prop 的"私有字段名包含匹配"兜底**
    /// （命中 `&lt;BotInventory&gt;k__BackingField`）才侥幸拿到对象，属脆弱路径。
    /// 现在优先用真名，仍保留旧名兜底。
    /// </summary>
    private static object BotInventoryOf(object botType)
    {
        return Prop(botType, "BotInventory") ?? Prop(botType, "Inventory");
    }
}
