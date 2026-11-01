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
/// ★★★ 清场小队装备差异化（task-33）：**只改进我们自己的产出物**，不碰任何别的 mod。
///
/// <para><b>为什么必须改写产出物</b>：清场小队的真身是 <c>WildSpawnType.arenaFighter</c>，
/// 我们刷的是有模板的 <c>arenaFighterEvent</c>（两版都有 <c>bots/types/arenafighterevent.json</c>）。
/// 但**装备生成被 APBS（`acidphantasm-progressivebotsystem`）接管**：
/// <c>ProgressiveBotSystem.Patches.GenerateInventoryPatch</c> 是
/// <c>BotInventoryGenerator.GenerateInventory</c> 的 <c>[PatchPrefix]</c>，自己 set <c>__result</c>。
/// ⇒ 改 SPT 表里的权重池不保证生效，**只能改写生成好的 <c>BotBase.Inventory.Items</c>**。</para>
///
/// <para><b>落点</b>：我们已有的 <c>BotGenerator.PrepareAndGenerateBot</c> postfix
/// （<c>Patches/BossWavePatch.cs</c> 的 <c>BotGenerated(object __1, object __result)</c>）。
/// <b>Harmony 语义</b>：别的 mod 的 prefix 返回 false 只跳过原方法体，**postfix 照常执行** ⇒
/// 我们拿到的 <c>__result</c> 就是最终产出（含 APBS 的结果）。</para>
///
/// <para><b>三项升级（判据全部来自本机 `items.json` 的数据字段，不凭名气）</b>：
/// <list type="number">
///   <item><b>更好的子弹</b>：按**同口径**在 `<c>_props.ammoType ∈ {bullet,buckshot}</c>` 且
///     `<c>StackMaxSize ≥ 10</c>` 的候选里，取 <c>PenetrationPower</c> 最高者
///     （同穿透再比 <c>Damage</c>、<c>ArmorDamage</c>）。同口径 ⇒ **必然可装**。
///     ⚠ <c>StackMaxSize ≥ 10</c> 这个下界是必须的：本机全库有 **14** 个 <c>stack=1</c> 的
///     "伪弹药"（<c>MON-50 Shrapnel</c> 穿 101/伤 440、各种破片与信号弹），不排掉就会把地雷当子弹换上去。</item>
///   <item><b>更好的插板</b>：对每块已装的板子，取**它所在那个槽的 filter**（= 该护甲允许的板子）里
///     最优者：<c>ArmorType</c>（Heavy &gt; Medium &gt; Light）→ <c>MaxDurability</c> ↓ →
///     <c>BluntThroughput</c> ↑低者优先。**从 filter 里挑 ⇒ 必然能装进那个槽**。</item>
///   <item><b>更好的枪</b>（可选，需 `events.json` 显式给 tpl）：换 <c>FirstPrimaryWeapon</c> 的 <c>_tpl</c>，
///     并按**新枪自己的 <c>mod_magazine</c> 槽 filter** 重新挑弹匣；旧枪的其他配件默认清掉
///     （它们未必能装到新枪上）。新枪 tpl 若解析不出 <c>ammoCaliber</c> ⇒ **整个换枪动作跳过**（降级）。</item>
/// </list></para>
///
/// <para><b>幂等</b>：每步都先比 <c>_tpl</c>，不相等才写 ⇒ 同一产出物重复调用结果一致。</para>
///
/// <para><b>"可还原"在这里的语义</b>：产出物是**每局一次性**的（<c>PrepareAndGenerateBot</c> 每只 bot 生成一次），
/// 没有"改回去"的对象 ⇒ 还原语义 = **活动关掉/换掉后我们就不再改**
/// （<see cref="ClearRound"/> 清空 spec）。这与 D2 首领补写、<c>EventScavSuppress</c> 完全一致，
/// 因此**不登记 `_restoreActions`**（那里也没有任何东西可回放）。</para>
///
/// <para><b>可降级</b>：role 不符 / 模板表拿不到 / tpl 找不到 / 结构不符 ⇒ 逐项 try/catch + warning，
/// **绝不影响 bot 生成，也绝不让服务端崩**。</para>
/// </summary>
public static class EventGearUpgrade
{
    private static ISptLogger<EventRotatorServer> _log;

    /// <summary>roleLowercase → 升级规格。由 Plugin 在套用活动时填充，<see cref="ClearRound"/> 清空。</summary>
    private static readonly Dictionary<string, BotGearUpgradeDef> _specs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本轮每条 role 的日志去重（避免每只 bot 刷屏；不影响幂等）。</summary>
    private static readonly HashSet<string> _logged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>条目模板表（`TemplateTable.Items`）。由 Plugin 注入。</summary>
    private static IDictionary _items;

    /// <summary>`Item` 类型（造新物品用）。</summary>
    private static Type _itemType;

    /// <summary>`Item.Upd` / `Upd.Repairable` types, resolved from `_itemType` in Init.</summary>
    private static Type _updType, _repairableType;

    /// <summary>弹药候选缓存：caliber → 最优 tpl（惰性）。</summary>
    private static readonly Dictionary<string, string> _bestAmmo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>`ArmorTask`/`ammoType` 常量</summary>
    private const int MinCartridgeStack = 10;   // ★ 排除 14 个 stack=1 的伪弹药（破片/信号弹/MON-50）

    public static void Init(ISptLogger<EventRotatorServer> log, object templateTable)
    {
        _log = log;
        _items = null;
        try { _items = templateTable?.GetType().GetProperty("Items")?.GetValue(templateTable) as IDictionary; }
        catch { _items = null; }
        try
        {
            // ★ 找 `Item` 类型（造新物品用）。**不能盲取 Dictionary 的第一个泛型参数**：
            //   生产的 `TemplateTable.Items` 是 `Dictionary<MongoId, TemplateItem>` ⇒ 第一个是**键** `MongoId`，
            //   拿它 new 出来的对象没有 Id/Template 属性 ⇒ NewItem 静默返回 null
            //   ⇒ 下挂榴弹 / 追加弹药两条路会**无声失效**。所以按"能不能用"来验证：值类型优先，再全库兜底扫。
            static bool Usable(Type t) =>
                t != null && !t.IsAbstract
                && t.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance) != null
                && t.GetProperty("Template", BindingFlags.Public | BindingFlags.Instance) != null;

            var gargs = _items?.GetType().GetGenericArguments();
            _itemType = gargs != null && gargs.Length >= 2 && Usable(gargs[1]) ? gargs[1] : null;
            if (_itemType == null)
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] ts;
                    try { ts = a.GetTypes(); }
                    catch (ReflectionTypeLoadException rtle) { ts = rtle.Types.Where(x => x != null).ToArray(); }
                    catch { continue; }
                    var hit = ts.FirstOrDefault(x => Usable(x) && (x.Namespace ?? "").Contains("Eft.Common.Tables"));
                    if (hit != null) { _itemType = hit; break; }
                }
            }
            try
            {
                _updType = _itemType?.GetProperty("Upd", BindingFlags.Public | BindingFlags.Instance)?.PropertyType;
                _repairableType = _updType?.GetProperty("Repairable", BindingFlags.Public | BindingFlags.Instance)?.PropertyType;
            }
            catch { _updType = null; _repairableType = null; }
        }
        catch { _itemType = null; }
    }

    public static bool IsEmpty => _specs.Count == 0;

    /// <summary>活动套用阶段：登记某 role 的升级规格。</summary>
    public static void AddSpec(BotGearUpgradeDef spec)
    {
        if (spec?.Role == null) return;
        var role = spec.Role.Trim();
        if (role.Length == 0) return;
        _specs[role] = spec;
    }

    public static void ClearRound()
    {
        try { _specs.Clear(); _logged.Clear(); _bestAmmo.Clear(); } catch { }
    }

    /// <summary>
    /// ★ 由 <c>BotGenerator.PrepareAndGenerateBot</c> 的 postfix 调用。
    /// <paramref name="details"/> = <c>BotGenerationDetails</c>（__1），
    /// <paramref name="bot"/> = <c>BotBase</c>（__result）。
    /// </summary>
    /// <summary>★ v2.6.72【Y2b】本局"命中清场小队"的 bot 数（给每局汇总行用；`ResetHits` 在换局时调用）。</summary>
    public static int HitsThisRaid { get; private set; }

    /// <summary>换局时清零（由 `BossWavePatch.FlushSpawnSummary` 调用）。</summary>
    public static void ResetHits() { HitsThisRaid = 0; }

    public static void OnBotGenerated(object details, object bot)
    {
        if (IsEmpty || bot == null || _items == null) return;
        long t0 = PerfTimer.Start();          // ★ v2.6.70【Y2】仅计时（关闭时恒为 0 ⇒ 零开销、零行为变化）
        try
        {
            var role = FirstString(bot, "Info", "Settings", "Role")
                    ?? Prop(details, "Role")?.ToString()
                    ?? Prop(details, "RoleLowercase")?.ToString();
            if (string.IsNullOrEmpty(role)) return;
            role = role.Trim();
            if (!_specs.TryGetValue(role, out var spec) || spec == null) return;
            HitsThisRaid++;                       // ★ v2.6.72【Y2b 汇总用】本局命中清场小队的 bot 数（一个自增，忽略不计的开销）

            var items = Prop(Prop(bot, "Inventory"), "Items") as IList;
            if (items == null || items.Count == 0) return;

            var (ammoN, plateN, wepChanged, oldWep, newWep, magN, launcherN, extraN, duraN) = Upgrade(items, spec);

            if (_logged.Add(role))
            {
                var parts = new List<string>();
                if (spec.WeaponTpl != null)
                    parts.Add($"武器 {(wepChanged ? $"{Name(oldWep)}→{Name(newWep)}" : "未换(降级/无需求)")}");
                if (spec.BetterAmmo) parts.Add($"弹药升级 {ammoN} 处");
                if (spec.BetterPlates) parts.Add($"插板升级 {plateN} 处");
                if (spec.BiggestMagazine) parts.Add($"大弹鼓 {magN} 处");
                if (!string.IsNullOrEmpty(spec.AddLauncherTpl)) parts.Add($"下挂榴弹 {(launcherN > 0 ? "已加" : "不适配/已有")}");
                if (spec.ExtraAmmoTpls != null && spec.ExtraAmmoTpls.Count > 0) parts.Add($"追加弹药 {extraN} 处");
                if (spec.DurabilityPercent > 0) parts.Add($"低耐久武器 {duraN} 把（{spec.DurabilityPercent}%{(spec.WesternOnly ? "，仅西式" : "")}）");
                _log?.Info($"[清场小队] {role}: {string.Join(" / ", parts)}");
            }
        }
        catch (Exception ex)
        {
            // 绝不影响 bot 生成
            try { _log?.Warning($"[清场小队] 装备差异化失败（已跳过）: {ex.GetType().Name}"); } catch { }
        }
        PerfTimer.End("清场小队.OnBotGenerated(每bot)", t0);      // ★ v2.6.70【Y2】
    }

    // ─────────────────────────── 核心 ───────────────────────────

    private static (int ammo, int plate, bool wepChanged, string oldWep, string newWep,
                     int mag, int launcher, int extra, int dura)
        Upgrade(IList items, BotGearUpgradeDef spec)
    {
        var byId = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var it in items)
        {
            var id = Prop(it, "Id")?.ToString();
            if (!string.IsNullOrEmpty(id)) byId[id] = it;
        }

        bool wepChanged = false;
        string oldWep = null, newWep = null;
        int magN = 0, launcherN = 0, extraN = 0, duraN = 0;

        // ---- ③ 更好的枪（可选；先换枪，再统一处理弹药/弹匣）----
        if (!string.IsNullOrEmpty(spec.WeaponTpl))
        {
            try
            {
                var w = FindBySlot(items, "FirstPrimaryWeapon");
                if (w != null)
                {
                    var cur = Prop(w, "Template")?.ToString();
                    oldWep = cur;
                    var nw = spec.WeaponTpl.Trim();
                    if (!string.Equals(cur, nw, StringComparison.OrdinalIgnoreCase))
                    {
                        var np = Props(nw);
                        // 新枪必须能解析出 ammoCaliber，否则不换（降级）
                        if (np != null && !string.IsNullOrEmpty(Prop(np, "AmmoCaliber")?.ToString()))
                        {
                            SetProp(w, "Template", nw);

                            // 弹匣：显式优先；否则从新枪 mod_magazine 槽 filter 里挑容量最大的
                            var magTpl = spec.MagazineTpl;
                            var magFilter = SlotFilter(np, "mod_magazine");
                            if (string.IsNullOrEmpty(magTpl) && magFilter.Count > 0)
                                magTpl = PickBiggestMagazine(magFilter);

                            var magItem = FindChildBySlot(items, Prop(w, "Id")?.ToString(), "mod_magazine");
                            if (magItem != null && !string.IsNullOrEmpty(magTpl))
                                SetProp(magItem, "Template", magTpl);

                            // 旧枪的其他配件未必能装到新枪上 ⇒ 默认清掉（保留弹匣）
                            if (spec.ClearOldMods)
                            {
                                var wid = Prop(w, "Id")?.ToString();
                                var kill = new List<object>();
                                foreach (var it in items)
                                {
                                    if (!string.Equals(Prop(it, "parentId")?.ToString(), wid, StringComparison.Ordinal)) continue;
                                    var sl = Prop(it, "slotId")?.ToString() ?? "";
                                    if (sl.Equals("mod_magazine", StringComparison.OrdinalIgnoreCase)) continue;
                                    kill.Add(it);
                                }
                                foreach (var k in kill) items.Remove(k);
                            }
                            newWep = nw;
                            wepChanged = true;
                        }
                        else
                        {
                            try { _log?.Warning($"[清场小队] 新枪 {nw} 解析不出 ammoCaliber → 跳过换枪（降级）"); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex) { SafeWarn($"[清场小队] 换枪失败（已跳过）: {ex.GetType().Name}"); }
        }

        // ---- ① 更好的子弹（同口径取全库最优）----
        int ammoN = 0;
        if (spec.BetterAmmo)
        {
            try
            {
                foreach (var it in items.Cast<object>().ToList())
                {
                    var tpl = Prop(it, "Template")?.ToString();
                    var p = Props(tpl);
                    if (p == null) continue;
                    var at = Prop(p, "AmmoType")?.ToString();
                    if (at == null || !(at.Equals("bullet", StringComparison.OrdinalIgnoreCase)
                                     || at.Equals("buckshot", StringComparison.OrdinalIgnoreCase))) continue;
                    if (ToInt(Prop(p, "StackMaxSize")) < MinCartridgeStack) continue;      // ★ 排掉伪弹药
                    var cal = Prop(p, "Caliber")?.ToString();
                    if (string.IsNullOrEmpty(cal)) continue;
                    var best = BestAmmoOf(cal);
                    if (string.IsNullOrEmpty(best) || string.Equals(best, tpl, StringComparison.OrdinalIgnoreCase)) continue;
                    SetProp(it, "Template", best);
                    ammoN++;
                }
            }
            catch (Exception ex) { SafeWarn($"[清场小队] 弹药升级失败（已跳过）: {ex.GetType().Name}"); }
        }

        // ---- ② 更好的插板（从该槽自己的 filter 里取最优）----
        int plateN = 0;
        if (spec.BetterPlates)
        {
            try
            {
                foreach (var it in items.Cast<object>().ToList())
                {
                    var slot = Prop(it, "slotId")?.ToString();
                    if (string.IsNullOrEmpty(slot) || !IsPlateSlot(slot)) continue;
                    var parentId = Prop(it, "parentId")?.ToString();
                    if (string.IsNullOrEmpty(parentId) || !byId.TryGetValue(parentId, out var armor)) continue;
                    var ap = Props(Prop(armor, "Template")?.ToString());
                    if (ap == null) continue;
                    var filter = SlotFilter(ap, slot);
                    if (filter.Count == 0) continue;
                    var best = PickBestPlate(filter);
                    if (string.IsNullOrEmpty(best)) continue;
                    var cur = Prop(it, "Template")?.ToString();
                    if (string.Equals(best, cur, StringComparison.OrdinalIgnoreCase)) continue;
                    SetProp(it, "Template", best);
                    plateN++;
                }
            }
            catch (Exception ex) { SafeWarn($"[清场小队] 插板升级失败（已跳过）: {ex.GetType().Name}"); }
        }

        // ---- ④ 大弹鼓：把现有主武器的弹匣换成"该武器 mod_magazine filter 里容量最大"的 ----
        //      官方 `Permanent Boss Presence` 2026-07-29「high-capacity drum magazines」。
        //      从武器自己的 filter 里挑 ⇒ 必然装得上，且自动避开"没有任何枪接受"的死鼓。
        if (spec.BiggestMagazine)
        {
            try
            {
                var w = FindBySlot(items, "FirstPrimaryWeapon");
                var wp = Props(Prop(w, "Template")?.ToString());
                if (wp != null)
                {
                    var big = PickBiggestMagazine(SlotFilter(wp, "mod_magazine"));
                    var magItem = FindChildBySlot(items, Prop(w, "Id")?.ToString(), "mod_magazine");
                    if (magItem != null && !string.IsNullOrEmpty(big)
                        && !string.Equals(Prop(magItem, "Template")?.ToString(), big, StringComparison.OrdinalIgnoreCase))
                    {
                        SetProp(magItem, "Template", big);
                        magN++;
                    }
                }
            }
            catch (Exception ex) { SafeWarn($"[清场小队] 大弹鼓失败（已跳过）: {ex.GetType().Name}"); }
        }

        // ---- ⑤ 下挂榴弹：**只在武器自己的 mod_launcher filter 接受时**才加 ----
        //      ⚠ 实测：`M203` / `FN40GL Mk2` / `FN GL-40` 对绝大多数枪都不在 filter 里
        //      （FN 两件其实是独立武器；M203 只适配 2 种不在 mods 树里的 AR-15 枪管）
        //      ⇒ 命中就加、不命中就跳过并 warning，**绝不硬塞非法搭配**。
        if (!string.IsNullOrEmpty(spec.AddLauncherTpl))
        {
            try
            {
                var w = FindBySlot(items, "FirstPrimaryWeapon");
                var wp = Props(Prop(w, "Template")?.ToString());
                var want = spec.AddLauncherTpl.Trim();
                if (wp == null || !SlotFilter(wp, "mod_launcher")
                        .Any(x => string.Equals(x, want, StringComparison.OrdinalIgnoreCase)))
                {
                    SafeWarn($"[清场小队] 下挂 {want} 不在武器 '{Prop(w, "Template")}' 的 mod_launcher filter 里 → 跳过（不硬塞）");
                }
                else
                {
                    var wid = Prop(w, "Id")?.ToString();
                    var exist = FindChildBySlot(items, wid, "mod_launcher");
                    var launcher = exist;
                    if (launcher == null)
                    {
                        launcher = NewItem(NextId(items, "lch"), want, wid, "mod_launcher");
                        if (launcher != null) { items.Add(launcher); launcherN++; }
                    }
                    if (launcher != null && !string.IsNullOrEmpty(spec.LauncherAmmoTpl))
                    {
                        var lid = Prop(launcher, "Id")?.ToString();
                        var hasAmmo = false;
                        foreach (var x in items)
                            if (string.Equals(Prop(x, "ParentId")?.ToString(), lid, StringComparison.Ordinal)) { hasAmmo = true; break; }
                        if (!hasAmmo && Props(spec.LauncherAmmoTpl.Trim()) != null)
                        {
                            var am = NewItem(NextId(items, "lam"), spec.LauncherAmmoTpl.Trim(), lid, null);
                            if (am != null) { items.Add(am); extraN++; }
                        }
                    }
                }
            }
            catch (Exception ex) { SafeWarn($"[清场小队] 下挂榴弹失败（已跳过）: {ex.GetType().Name}"); }
        }

        // ---- ⑥ 追加弹药：作为新物品塞进 Pockets（火箭筒/下挂的 40mm 弹走这条）----
        if (spec.ExtraAmmoTpls != null && spec.ExtraAmmoTpls.Count > 0)
        {
            try
            {
                var pid = Prop(FindBySlot(items, "Pockets"), "Id")?.ToString();
                if (string.IsNullOrEmpty(pid)) pid = Prop(FindBySlot(items, "TacticalVest"), "Id")?.ToString();
                if (string.IsNullOrEmpty(pid))
                {
                    SafeWarn("[清场小队] 找不到 Pockets/TacticalVest 容器 → 追加弹药跳过");
                }
                else
                {
                    foreach (var t in spec.ExtraAmmoTpls)
                    {
                        var tpl = (t ?? "").Trim();
                        if (tpl.Length == 0) continue;
                        if (Props(tpl) == null) { SafeWarn($"[清场小队] 追加弹药 {tpl} 不在 items.json → 跳过"); continue; }
                        // ★ 幂等：同一个 tpl 已经在身上就不再追加（同一只 bot 的产出物可能被多次生成调用命中）
                        var dup = false;
                        foreach (var x in items)
                            if (string.Equals(Prop(x, "Template")?.ToString(), tpl, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                        if (dup) continue;
                        var it = NewItem(NextId(items, "xam"), tpl, pid, null);
                        if (it != null) { items.Add(it); extraN++; }
                    }
                }
            }
            catch (Exception ex) { SafeWarn($"[清场小队] 追加弹药失败（已跳过）: {ex.GetType().Name}"); }
        }

        // ---- 7) SCAV low-durability western weapons (official arms_race) ----
        //   Durability = MaxDurability * percent/100; MaxDurability KEEPS the original; mods NOT touched.
        //   NOTE: Item.Upd is Upd? (nullable) so we must null-check and create Upd / UpdRepairable when missing.
        //   That is this project's classic silent-failure spot, so everything is try/catch + read-back proof.
        if (spec.DurabilityPercent > 0 && spec.DurabilityPercent < 100 && _updType != null && _repairableType != null)
        {
            foreach (var it in items.Cast<object>().ToList())
            {
                try
                {
                    var wp = Props(Prop(it, "Template")?.ToString());
                    if (wp == null) continue;
                    if (Prop(wp, "WeapClass") == null) continue;          // firearms only (melee has no WeapClass)
                    if (spec.WesternOnly && !IsWesternWeapon(wp)) continue;
                    var max = ToDouble(Prop(wp, "MaxDurability"));
                    if (max <= 0) continue;                               // cannot read original -> skip, never guess
                    var target = Math.Round(max * spec.DurabilityPercent / 100.0, 5);

                    var upd = Prop(it, "Upd");
                    if (upd == null)
                    {
                        upd = NewObj(_updType);
                        if (upd == null || !SetProp(it, "Upd", upd)) { SafeWarn("[cleanup] cannot create Upd, skipping this weapon"); continue; }
                    }
                    var rep = Prop(upd, "Repairable");
                    if (rep == null)
                    {
                        rep = NewObj(_repairableType);
                        if (rep == null || !SetProp(upd, "Repairable", rep)) { SafeWarn("[cleanup] cannot create Upd.Repairable, skipping"); continue; }
                    }
                    var before = Prop(rep, "Durability")?.ToString() ?? "(null)";
                    SetProp(rep, "Durability", target);
                    SetProp(rep, "MaxDurability", max);                   // keep original max
                    duraN++;
                    if (_logged.Add("dura:" + spec.Role))
                        _log?.Info($"[cleanup] {spec.Role}: low-durability weapon {Prop(it, "Template")} durability {before} -> "
                                 + $"{Prop(rep, "Durability")}/{Prop(rep, "MaxDurability")}"
                                 + $" (= {spec.DurabilityPercent}% of original {max}, read-back OK, revert = event off stops changing)");
                }
                catch (Exception ex) { SafeWarn($"[cleanup] low-durability rewrite failed (weapon skipped): {ex.GetType().Name}"); }
            }
        }

        return (ammoN, plateN, wepChanged, oldWep, newWep, magN, launcherN, extraN, duraN);
    }

    /// <summary>
    /// Western weapon makers (the "weapon_&lt;maker&gt;_" token of _name).
    /// Hand-curated list, NOT a data field. Verified in this DB: every weapon _name is
    /// "weapon_&lt;maker&gt;_...", so the maker token cleanly separates western from eastern
    /// (izhmash / molot / toz / zmz / izhmeh / ckib / zis / kalashnikov ...).
    /// </summary>
    private static readonly HashSet<string> WesternMakers = new(StringComparer.OrdinalIgnoreCase)
    {
        "colt", "hk", "remington", "mossberg", "adar", "fn", "sig", "steyr", "bt",
        "knight", "knights", "lantac", "daniel", "noveske", "bcm", "troy", "magpul", "aero",
        "wilson", "springfield", "kac", "geissele", "midwest", "rainier", "primary",
        "barrett", "desert", "imi", "iwi", "pof", "ruger", "sako", "accuracy",
    };

    /// <summary>Is this a "western" weapon: the maker token of _name is in WesternMakers.</summary>
    private static bool IsWesternWeapon(object props)
    {
        try
        {
            var n = Prop(props, "Name")?.ToString();
            if (string.IsNullOrEmpty(n)) return false;
            if (!n.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)) return false;
            var mk = n.Substring("weapon_".Length).Split('_')[0];
            return WesternMakers.Contains(mk);
        }
        catch { return false; }
    }

    /// <summary>
    /// Create an instance (Upd / UpdRepairable are records).
    /// Try the parameterless ctor first; fall back to GetUninitializedObject (positional records have none).
    /// Returns null on failure so the caller can warn instead of silently doing nothing.
    /// </summary>
    private static object NewObj(Type t)
    {
        if (t == null) return null;
        try { return Activator.CreateInstance(t); }
        catch
        {
            try { return System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t); }
            catch { return null; }
        }
    }

    /// <summary>造一个新 `Item`（`Id`/`Template`/`ParentId`/`SlotId` 都是实测 CLR 名，无下划线）。</summary>
    private static object NewItem(string id, string tpl, string parentId, string slotId)
    {
        if (_itemType == null) return null;
        try
        {
            var it = Activator.CreateInstance(_itemType);
            if (!SetProp(it, "Id", id) || !SetProp(it, "Template", tpl)) return null;
            if (!string.IsNullOrEmpty(parentId)) SetProp(it, "ParentId", parentId);
            if (!string.IsNullOrEmpty(slotId)) SetProp(it, "SlotId", slotId);
            return it;
        }
        catch { return null; }
    }

    /// <summary>
    /// 造一个不与现有条目撞的新 id。
    ///
    /// <para>★★ <b>必须是 24 位十六进制</b>：实测 `MongoId` 的构造函数会校验
    /// <c>ObjectId must be a 24-character hex string</c>（长度不对直接抛 ArgumentException）。
    /// 早先版本用的是 <c>prefix + i.ToString("x6")</c>（9 位）⇒ <c>SetProp(it,"Id",…)</c> 抛异常
    /// 被 catch 吞掉 ⇒ <c>NewItem</c> 返回 null ⇒ **下挂榴弹 / 追加弹药两条路无声失效**。</para>
    /// </summary>
    private static string NextId(IList items, string prefix)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            var v = Prop(it, "Id")?.ToString();
            if (!string.IsNullOrEmpty(v)) used.Add(v);
        }
        for (int i = 0; i < 65536; i++)
        {
            var cand = Hex24(prefix, i);
            if (!used.Contains(cand)) return cand;
        }
        // 兜底：真随机 24 位 hex
        var bytes = Guid.NewGuid().ToByteArray();
        var sb = new System.Text.StringBuilder(24);
        for (int k = 0; k < 12; k++) sb.Append(bytes[k].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>由前缀 + 计数器拼一个合法的 24 位十六进制 id（前缀的每字符取低 4 位当 hex）。</summary>
    private static string Hex24(string prefix, int counter)
    {
        var sb = new System.Text.StringBuilder(24);
        foreach (var ch in prefix ?? "")
            sb.Append(((int)ch & 0xF).ToString("x"));      // 每个字符 → 1 位 hex
        sb.Append(counter.ToString("x8"));                  // 计数器 8 位 hex
        var body = sb.ToString();
        // 左补 0 到 24 位（补 0 仍是合法 hex，且不同前缀不会互相撞）
        return body.Length >= 24 ? body.Substring(body.Length - 24) : new string('0', 24 - body.Length) + body;
    }

    // ─────────────────────────── 反射小工具 ───────────────────────────

    /// <summary>
    /// 取 `TemplateItem` 的 `_props`（跨命名约定：`_props` / `Props` / `Properties` 都试）。
    /// 实测本机该属性的 CLR 名与 JSON 名不同 — 只按 JSON 名取会静默 null。
    /// </summary>
    // ─────────────────────────── 判据（全部数据驱动，判据来自本机 items.json 字段）───────────────────────────

    /// <summary>某口径下"更好"的弹药：穿透 → 伤害 → 甲伤，全部降序。</summary>
    private static string BestAmmoOf(string caliber)
    {
        if (_bestAmmo.TryGetValue(caliber, out var hit)) return hit;
        string best = null;
        int bp = -1, bd = -1, ba = -1;
        foreach (DictionaryEntry e in _items)
        {
            var p = TemplateProps(e.Value);
            if (p == null) continue;
            if (!string.Equals(Prop(p, "Caliber")?.ToString(), caliber, StringComparison.OrdinalIgnoreCase)) continue;
            var at = Prop(p, "AmmoType")?.ToString();
            if (at == null || !(at.Equals("bullet", StringComparison.OrdinalIgnoreCase)
                             || at.Equals("buckshot", StringComparison.OrdinalIgnoreCase))) continue;
            if (ToInt(Prop(p, "StackMaxSize")) < MinCartridgeStack) continue;
            int pen = ToInt(Prop(p, "PenetrationPower"));
            int dmg = ToInt(Prop(p, "Damage"));
            int ad = ToInt(Prop(p, "ArmorDamage"));
            if (pen > bp || (pen == bp && dmg > bd) || (pen == bp && dmg == bd && ad > ba))
            {
                bp = pen; bd = dmg; ba = ad;
                best = e.Key?.ToString();
            }
        }
        _bestAmmo[caliber] = best;
        return best;
    }

    /// <summary>是否"硬板/软衬"槽（大小写不敏感；本机两种写法都存在：`Front_plate` 与 `front_plate`）。</summary>
    /// <summary>
    /// ★★★ P1-② 修复：**显式槽名白名单**（原来只 `Contains("plate") || Contains("soft_armor")`
    /// ⇒ 漏掉 `Collar`(26) / `Groin`(26) / `Groin_back`(10) 三个**真板槽** ⇒ 护颈板、裆板不会被升级）。
    ///
    /// <para>下面是**全库护甲/头盔/胸挂物品种实测**的板槽名，比较时 `ToLowerInvariant()` 兼容本体里
    /// 两种大小写（例如 `Front_plate` 58 次 + `front_plate` 3 次；`Soft_armor_front` 与 `soft_armor_front` 并存）。</para>
    /// </summary>
    private static readonly HashSet<string> PlateSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Front_plate", "Back_plate", "Left_side_plate", "Right_side_plate",
        "Soft_armor_front", "Soft_armor_back", "Soft_armor_left", "soft_armor_right",
        "Collar", "Groin", "Groin_back",
    };

    private static bool IsPlateSlot(string slot)
    {
        if (string.IsNullOrEmpty(slot)) return false;
        // 白名单为主；再兜一层"含 plate / soft_armor"防止本体新增槽名时整片漏掉
        return PlateSlots.Contains(slot) || slot.Contains("plate", StringComparison.OrdinalIgnoreCase)
                                       || slot.Contains("soft_armor", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从 filter 里挑"更好的板子"：`ArmorType`（Heavy&gt;Medium&gt;Light）→
    /// `MaxDurability` ↓ → `BluntThroughput` ↑（越小越好）。
    /// </summary>
    private static string PickBestPlate(List<string> filter)
    {
        string best = null;
        int bType = -1, bDur = -1;
        double bBlunt = double.MaxValue;
        foreach (var t in filter)
        {
            var p = Props(t);
            if (p == null) continue;
            int ty = ArmorTypeRank(Prop(p, "ArmorType")?.ToString());
            int dur = ToInt(Prop(p, "MaxDurability"));
            double blunt = ToDouble(Prop(p, "BluntThroughput"));
            bool better = ty > bType
                       || (ty == bType && dur > bDur)
                       || (ty == bType && dur == bDur && blunt < bBlunt);
            if (better) { bType = ty; bDur = dur; bBlunt = blunt; best = t; }
        }
        return best;
    }

    private static int ArmorTypeRank(string t)
    {
        if (string.IsNullOrEmpty(t)) return -1;
        if (t.Equals("Heavy", StringComparison.OrdinalIgnoreCase)) return 3;
        if (t.Equals("Medium", StringComparison.OrdinalIgnoreCase)) return 2;
        if (t.Equals("Light", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    /// <summary>取某模板某槽的 filter（槽名大小写不敏感；走实测 CLR 名 `Slots`/`Name`/`Properties`/`Filters`/`Filter`）。</summary>
    private static List<string> SlotFilter(object props, string slotName)
    {
        var res = new List<string>();
        try
        {
            if (Prop(props, "Slots") is not IEnumerable slots) return res;
            foreach (var s in slots)
            {
                if (!string.Equals(Prop(s, "Name")?.ToString(), slotName, StringComparison.OrdinalIgnoreCase)) continue;
                if (Prop(Prop(s, "Properties"), "Filters") is not IEnumerable filters) continue;
                foreach (var f in filters)
                    if (Prop(f, "Filter") is IEnumerable fl)
                        foreach (var x in fl) { var v = x?.ToString(); if (!string.IsNullOrEmpty(v)) res.Add(v); }
            }
        }
        catch { }
        return res;
    }

    /// <summary>从弹匣候选里挑容量最大的（容量 = `Slot.MaxCount`，JSON 名 `_max_count`）。</summary>
    private static string PickBiggestMagazine(List<string> mags)
    {
        string best = null; int bestCap = -1;
        foreach (var t in mags)
        {
            var p = Props(t);
            if (p == null) continue;
            int cap = 0;
            try
            {
                if (Prop(p, "Cartridges") is IEnumerable cs)
                    foreach (var c in cs)
                    {
                        cap = ToInt(Prop(c, "MaxCount"));
                        if (cap > 0) break;
                    }
            }
            catch { }
            if (cap > bestCap) { bestCap = cap; best = t; }
        }
        return best ?? mags.FirstOrDefault();
    }

    /// <summary>按 `slotId` 找装备件。</summary>
    private static object FindBySlot(IList items, string slot)
    {
        foreach (var it in items)
            if (string.Equals(Prop(it, "SlotId")?.ToString(), slot, StringComparison.OrdinalIgnoreCase))
                return it;
        return null;
    }

    /// <summary>按 `parentId` + `slotId` 找子件。</summary>
    private static object FindChildBySlot(IList items, string parentId, string slot)
    {
        if (string.IsNullOrEmpty(parentId)) return null;
        foreach (var it in items)
        {
            if (!string.Equals(Prop(it, "ParentId")?.ToString(), parentId, StringComparison.Ordinal)) continue;
            if (slot == null) return it;                       // slot 为 null ⇒ 只要任一子件
            if (string.Equals(Prop(it, "SlotId")?.ToString(), slot, StringComparison.OrdinalIgnoreCase)) return it;
        }
        return null;
    }

    private static object TemplateProps(object templateItem)
    {
        if (templateItem == null) return null;
        return Prop(templateItem, "_props")
            ?? Prop(templateItem, "Properties")
            ?? Prop(templateItem, "Props");
    }

    private static object Props(string tpl)
    {
        if (string.IsNullOrEmpty(tpl) || _items == null) return null;
        try { return TemplateProps(_items[tpl]); } catch { return null; }
    }

    /// <summary>
    /// ★★ 属性读取（**跨命名约定**）。实测本机 SPT 的 CLR 属性名与 JSON 名**不一样**：
    /// <c>[JsonPropertyName("_id")] → Item.Id</c>、<c>[JsonPropertyName("_tpl")] → Item.Template</c>、
    /// <c>[JsonPropertyName("_props")] → Slot.Properties</c>、<c>[JsonPropertyName("_name")] → Slot.Name</c>、
    /// <c>[JsonPropertyName("ammoType")] → AmmoType</c>、<c>[JsonPropertyName("filters")] → SlotProperties.Filters</c>、
    /// <c>[JsonPropertyName("_max_count")] → Slot.MaxCount</c>。
    /// ⇒ 只按 JSON 名取会**静默全 null**（第一次写就成了空转）。这里按四档依次尝试：
    /// 原名 → 去下划线 → 加下划线 → 忽略大小写全扫。
    /// </summary>
    /// <summary>
    /// ★ v2.6.70【性能 · G1 反射成员缓存】**零行为变化**：解析顺序/`BindingFlags`/去下划线规则/
    /// 忽略大小写兜底**逐条照抄**下面的 `ResolveProp`（原内联逻辑），只是把"每次解析"降为"查一次字典"。
    /// 命中点在**每个 bot** 的 `OnBotGenerated`（`FirstString`→`Prop` 3~4 次/只）。
    /// ⚠ 值用 `(PropertyInfo)` 包装以允许缓存"解析不到 = null"的结论；类型热重载不适用于 SPT 运行期。
    /// </summary>
    private sealed class PropSlot { public PropertyInfo P; }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), PropSlot> _propCache = new();

    private static object Prop(object o, string name)
    {
        if (o == null || string.IsNullOrEmpty(name)) return null;
        var t = o.GetType();
        try
        {
            var p = _propCache.GetOrAdd((t, name), static k => new PropSlot { P = ResolveProp(k.Item1, k.Item2) }).P;
            return p?.GetValue(o);
        }
        catch { return null; }
    }

    /// <summary>原 `Prop` 的解析逻辑（**逐字照抄**，含 LINQ 兜底），供缓存工厂调用。</summary>
    private static PropertyInfo ResolveProp(Type t, string name)
    {
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p != null) return p;

        var stripped = name.TrimStart('_');
        if (stripped != name)
        {
            p = t.GetProperty(stripped, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p;
        }
        else
        {
            p = t.GetProperty("_" + name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p;
        }
        // 忽略大小写兜底（ammoType ↔ AmmoType、filters ↔ Filters）
        return t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(x.Name, stripped, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>★ v2.6.73【G5】`SetProp` 的解析缓存（与 <see cref="_propCache"/> 同款）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), PropSlot> _setPropCache = new();

    /// <summary>★ v2.6.73【G5】原 `SetProp` 的解析链（**逐字照抄**），供缓存工厂调用。</summary>
    private static PropertyInfo ResolveSetProp(Type t, string name)
    {
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
             ?? t.GetProperty(name.TrimStart('_'), BindingFlags.Public | BindingFlags.Instance)
             ?? t.GetProperty("_" + name, BindingFlags.Public | BindingFlags.Instance)
             ?? t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                 .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(x.Name, name.TrimStart('_'), StringComparison.OrdinalIgnoreCase));
        return p;   // 调用方照旧判 `p == null || !p.CanWrite`
    }

    /// <summary>同上，但写值（同样跨命名约定）。</summary>
    private static bool SetProp(object o, string name, object val)
    {
        if (o == null || string.IsNullOrEmpty(name)) return false;
        var t = o.GetType();
        try
        {
            // ★ v2.6.73【G5】解析链**逐字照抄**到 `ResolveSetProp`，只是把"每次解析"降为"查一次字典"，
            //   与 `Prop` 的缓存同一套手法（同 Type+名字 ⇒ 同 PropertyInfo ⇒ 同写入结果）。
            var p = _setPropCache.GetOrAdd((t, name), static k => new PropSlot { P = ResolveSetProp(k.Item1, k.Item2) }).P;
            if (p == null || !p.CanWrite) return false;
            var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            object v = val;
            if (pt == typeof(string)) v = val?.ToString();
            else if (val is string sv && pt != typeof(string))
            {
                // ★★ 关键：`Item.Id` / `Item.Template` 的真实类型是 **`MongoId`（struct）**，不是 string。
                //   直接 SetValue(字符串) 会抛 ArgumentException → 被本方法的 catch 吞掉 → 返回 false
                //   → `NewItem` 返回 null → **下挂榴弹 / 追加弹药两条路无声失效**。
                //   这里按"能不能构造出来"依次尝试：(string) 构造 → op_Implicit(string) → Convert。
                try { v = Activator.CreateInstance(pt, sv); }
                catch
                {
                    var mi = pt.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static,
                                          null, new[] { typeof(string) }, null);
                    if (mi != null) v = mi.Invoke(null, new object[] { sv });
                    else v = Convert.ChangeType(sv, pt);
                }
            }
            p.SetValue(o, v);
            return true;
        }
        catch { return false; }
    }

    /// <summary>取嵌套属性链上的第一个非空字符串（如 Info.Settings.Role）。</summary>
    private static string FirstString(object o, params string[] path)
    {
        try
        {
            var cur = o;
            foreach (var k in path) { cur = Prop(cur, k); if (cur == null) return null; }
            var s = cur as string ?? cur.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
    }

    private static string Name(string tpl)
    {
        if (string.IsNullOrEmpty(tpl)) return "?";
        try { return Props(tpl) is object p ? (Prop(p, "Name")?.ToString() ?? tpl) : tpl; }
        catch { return tpl; }
    }

    private static int ToInt(object v)
    {
        try { return v == null ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return 0; }
    }

    private static double ToDouble(object v)
    {
        try { return v == null ? double.MaxValue : Convert.ToDouble(v, CultureInfo.InvariantCulture); }
        catch { return double.MaxValue; }
    }

    private static void SafeWarn(string m) { try { _log?.Warning(m); } catch { } }
}
