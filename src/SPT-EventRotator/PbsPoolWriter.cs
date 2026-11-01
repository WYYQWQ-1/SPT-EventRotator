using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★★★ v0.1.5 ④【大弹鼓 SCAV 的武器侧修复】把弹鼓写进 **PBS（Acid's Progressive Bot System）的运行期池子**。
///
/// 取证结论（反编译 PBS 2.3.0，行号见 audit/pbs_v416_ref/…decompiled.cs）：
///   · 池子持有者：<c>ProgressiveBotSystem.Helpers.DataLoader</c>（SPT DI 单例，TypePriority=100010）
///     属性 **<c>AllTierDataDirty</c>** → <c>.Tiers[档位].ModsData[武器 tpl]["mod_magazine"]</c> → **HashSet&lt;MongoId&gt;**（L33775/33798/33802）
///   · PBS **确实读** items.json 的武器槽位过滤表，但与自己的池**取交集**（L2643）
///     ⇒ **池 + 过滤表两边都要写**（过滤表那一半由 <see cref="DrumMagazineDimension"/> 完成）
///   · 生成期是**活引用读**（GetTierMods 只浅拷贝外层，L35902）⇒ 进图中 Add 立刻生效
///   · PBS 网页 Save/Reload 会 <c>DeepClone</c> 重建 Dirty（L39676/39741）⇒ **每次进图幂等补写**（自愈）
///   · <c>mod_magazine</c> 属于 CartridgeHolderSlots（L2228/1619）⇒ 弹匣槽**每次必填**，不掷骰
///     （数据里的 "mod_magazine: 25" 是死配置）
///
/// 原则：**只改 PBS 的内存对象，绝不碰它任何文件**；失败只记 warning。
/// </summary>
internal static class PbsPoolWriter
{
    public static ISptLogger<EventRotatorServer> Log;
    public static object DataLoader;          // ProgressiveBotSystem.Helpers.DataLoader 单例
    private static readonly List<(int Tier, string Weapon, string Drum)> _written = new();
    private static readonly HashSet<string> _createdWeaponKeys = new(StringComparer.OrdinalIgnoreCase);

    // ★ v1.0.1【性能·第五轮】属性 / 字段两条查找各自缓存（两者 flags 不同，不能共用一份）。
    //   语义逐字保留：属性优先，取到 null 才回落字段；`GetValue` 异常照旧向上抛。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), PropertyInfo> _piCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), FieldInfo> _fiCache = new();

    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        var pi = _piCache.GetOrAdd((t, name),
            static k => k.Item1.GetProperty(k.Item2, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));
        var v = pi?.GetValue(o);
        if (v != null) return v;
        var fi = _fiCache.GetOrAdd((t, name),
            static k => k.Item1.GetField(k.Item2, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase));
        return fi?.GetValue(o);
    }

    public static IServiceProvider ServiceProvider;

    /// <summary>
    /// 定位 PBS 的 DataLoader 单例：**运行时**用 IServiceProvider 按类型解析。
    ///
    /// 为什么不注入 <c>IEnumerable&lt;IOnLoad&gt;</c>：本类自身就是一个 <c>IOnLoad</c>，
    /// 解析整个集合时会去构造本类 ⇒ **循环依赖**，服务端启动即崩
    /// （实测：`A circular dependency was detected for the service type
    /// 'System.Collections.Generic.IEnumerable&lt;SPTarkov.Server.Core.DI.IOnLoad&gt;'`）。
    /// 按**具体类型**解析则不会牵扯本类。
    /// </summary>
    public static void Locate()
    {
        try
        {
            if (DataLoader != null) return;
            if (ServiceProvider == null) return;
            Type t = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = a.GetType("ProgressiveBotSystem.Helpers.DataLoader", false); } catch { }
                if (t != null) break;
            }
            if (t == null) { Log?.Info("[EventRotator][PBS池] 未发现 PBS DataLoader 类型（未装 PBS）"); return; }
            try { DataLoader = ServiceProvider.GetService(t); } catch (Exception ex) { Log?.Warning($"[EventRotator][PBS池] 解析 DataLoader 失败: {ex.Message}"); }
            if (DataLoader != null)
                Log?.Info("[EventRotator][PBS池] 已定位 PBS DataLoader 单例 → 弹鼓可写进它的运行期池子（不改它任何文件）");
            else
                Log?.Info("[EventRotator][PBS池] 未发现 PBS DataLoader（未装 PBS 或尚未初始化）");
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][PBS池] 定位 DataLoader 失败: {ex.Message}"); }
    }

    /// <summary>每次进图幂等补写（PBS Save/Reload 会重建池子，需自愈）。</summary>
    public static void ReapplyPending()
    {
        try
        {
            Locate();                      // ★ 运行时按类型解析（幂等，解析一次后缓存）
            if (DataLoader == null) return;
            var pairs = DrumMagazineDimension.AddedPairs;
            if (pairs == null || pairs.Count == 0) return;
            int added = 0;
            foreach (var (weapon, drum) in pairs) added += WriteOne(weapon, drum);
            if (added > 0)
                Log?.Info($"[EventRotator][PBS池] 弹鼓池子补写完成：{added} 条（武器×弹鼓；PBS 与本 mod 过滤表取交集，两边都已写）");
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][PBS池] 补写失败: {ex.Message}"); }
    }

    private static int WriteOne(string weaponTpl, string drumTpl)
    {
        int n = 0;
        try
        {
            var dirty = Prop(DataLoader, "AllTierDataDirty");
            var tiers = Prop(dirty, "Tiers") as IDictionary;
            if (tiers == null) return 0;
            var mongoT = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(t => t.Name == "MongoId" && t.Namespace != null && t.Namespace.Contains("SPTarkov"));
            if (mongoT == null) return 0;
            object drumId = null;
            try { drumId = Activator.CreateInstance(mongoT, new object[] { drumTpl }); } catch { }
            if (drumId == null) { try { drumId = Convert.ChangeType(drumTpl, mongoT); } catch { } }
            if (drumId == null) return 0;

            foreach (DictionaryEntry tkv in tiers)
            {
                int tier; try { tier = Convert.ToInt32(tkv.Key); } catch { continue; }
                var modsData = Prop(tkv.Value, "ModsData") as IDictionary;
                if (modsData == null) continue;
                object weaponKey = null;
                foreach (DictionaryEntry mk in modsData)
                    if (string.Equals(mk.Key?.ToString(), weaponTpl, StringComparison.OrdinalIgnoreCase)) { weaponKey = mk.Key; break; }
                if (weaponKey == null)
                {
                    // 建键：Dictionary<MongoId, Dictionary<string, HashSet<MongoId>>>
                    try
                    {
                        var kt = modsData.GetType().IsGenericType ? modsData.GetType().GetGenericArguments()[0] : mongoT;
                        var vt = modsData.GetType().IsGenericType ? modsData.GetType().GetGenericArguments()[1] : null;
                        if (vt == null) continue;
                        object wk = kt == mongoT ? drumId.GetType() == kt ? Activator.CreateInstance(kt, new object[] { weaponTpl }) : null : null;
                        if (wk == null) { try { wk = Activator.CreateInstance(kt, new object[] { weaponTpl }); } catch { } }
                        if (wk == null) continue;
                        var slotDict = Activator.CreateInstance(vt);
                        modsData[wk] = slotDict;
                        weaponKey = wk;
                        _createdWeaponKeys.Add(tier + "|" + weaponTpl);
                    }
                    catch { continue; }
                }
                var slots = modsData[weaponKey] as IDictionary;
                if (slots == null) continue;
                object slotKey = null;
                foreach (DictionaryEntry sk in slots)
                    if (string.Equals(sk.Key?.ToString(), "mod_magazine", StringComparison.OrdinalIgnoreCase)) { slotKey = sk.Key; break; }
                if (slotKey == null)
                {
                    try
                    {
                        var vt2 = slots.GetType().IsGenericType ? slots.GetType().GetGenericArguments()[1] : null;
                        if (vt2 == null) continue;
                        slots["mod_magazine"] = Activator.CreateInstance(vt2);
                        slotKey = "mod_magazine";
                    }
                    catch { continue; }
                }
                var set = slots[slotKey];
                if (set == null) continue;
                var addM = set.GetType().GetMethod("Add", new[] { mongoT });
                var containsM = set.GetType().GetMethod("Contains", new[] { mongoT });
                bool has = false;
                try { has = containsM != null && (bool)containsM.Invoke(set, new[] { drumId }); } catch { }
                if (has) continue;
                try
                {
                    if (addM != null) { addM.Invoke(set, new[] { drumId }); n++; _written.Add((tier, weaponTpl, drumTpl)); }
                    else if (set is IList lst) { lst.Add(drumId); n++; _written.Add((tier, weaponTpl, drumTpl)); }
                }
                catch { }
            }
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][PBS池] 写 {weaponTpl}/{drumTpl} 失败: {ex.Message}"); }
        return n;
    }

    /// <summary>还原：重走路径 + HashSet.Remove（PBS 重建过池子时旧引用已失效，必须重走）。</summary>
    public static void RestoreAll()
    {
        try
        {
            if (DataLoader == null || _written.Count == 0) return;
            var dirty = Prop(DataLoader, "AllTierDataDirty");
            var tiers = Prop(dirty, "Tiers") as IDictionary;
            if (tiers == null) return;
            var mongoT = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(t => t.Name == "MongoId" && t.Namespace != null && t.Namespace.Contains("SPTarkov"));
            if (mongoT == null) return;
            int n = 0;
            foreach (var (tier, weapon, drum) in _written)
            {
                try
                {
                    object drumId = null;
                    try { drumId = Activator.CreateInstance(mongoT, new object[] { drum }); } catch { }
                    if (drumId == null) continue;
                    if (!tiers.Contains(tier)) continue;
                    var modsData = Prop(tiers[tier], "ModsData") as IDictionary;
                    if (modsData == null) continue;
                    object wkey = null;
                    foreach (DictionaryEntry mk in modsData)
                        if (string.Equals(mk.Key?.ToString(), weapon, StringComparison.OrdinalIgnoreCase)) { wkey = mk.Key; break; }
                    if (wkey == null) continue;
                    var slots = modsData[wkey] as IDictionary;
                    if (slots == null) continue;
                    object skey = null;
                    foreach (DictionaryEntry sk in slots)
                        if (string.Equals(sk.Key?.ToString(), "mod_magazine", StringComparison.OrdinalIgnoreCase)) { skey = sk.Key; break; }
                    if (skey == null) continue;
                    var set = slots[skey];
                    var remM = set?.GetType().GetMethod("Remove", new[] { mongoT });
                    if (remM != null) { remM.Invoke(set, new[] { drumId }); n++; }
                }
                catch { }
            }
            _written.Clear();
            Log?.Info($"[EventRotator][PBS池] 已还原 {n} 条池子写入");
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][PBS池] 还原失败: {ex.Message}"); }
    }
}
