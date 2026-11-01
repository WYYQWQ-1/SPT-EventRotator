using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace EventRotator;

/// <summary>
/// ★★★ v2.6.40【P0 空投崩溃修复 · 方案 B（Lead 已批准）】**preset 同根去重补丁**。
///
/// 症状（用户实测）：`/client/airdrop/loot` 抛 Critical：
///   <c>An item with the same key has already been added. Key: 6871284e9a353bb50606f3ed</c>
///   at <c>PresetHelper.GetDefaultPresetsByTplKey()</c> ← <c>LootGenerator.CreateForcedLoot()</c>
///   ← <c>AirdropService.GenerateAirdropLoot()</c>
///
/// 根因（反编译 + 全库数据核查，见 <c>audit/空投崩溃-核查.md</c>）：
///   SPT 的 <c>PresetHelper.cs:45-52</c> 用
///   <c>preset.Items.First().Template</c>（= **preset 第一件物品的 tpl**，**不是 preset id**）
///   做 <c>ToDictionary</c> 的 key，**且没有去重** ⇒ 只要有两个 preset 的第一件物品是同一个 tpl 就必抛。
///   本机冲突来自第三方 mod 的 preset 数据（不是 SPT、也不是本 mod）：
///     · 根 <c>6871284e…</c>（WTT-ContentBackport 的「AS 巨浪 MOD.4」）：ISB-Aishi 的
///       <c>db/CustomWeaponPresets/Aishi_asvalmod4_peace.json</c>（Sparxie V2）+ WTT 的 <c>nb_val_mod4_defalut</c>；
///     · 根 <c>6895bb82…</c>：**ISB-Aishi 自己**就带了 4 个同根 preset（evernight 1/2/III/IV）。
///   而 <c>GetDefaultPresetsByTplKey()</c> 在全库**只有一个调用点** = <c>LootGenerator.cs:126</c>
///   （<c>CreateForcedLoot</c> 首行）⇒ **只影响"强制掉落"空投**（radar / toiletPaper / 我们
///   <c>airdropAddLoot</c> 强制打开的那些类型），普通空投走 <c>CreateRandomLoot</c>（按 preset id，不崩）。
///
/// 修法（**语义 = 同根只留第一个**，与官方"第一个就是它"的语义最接近）：
///   prefix 自己算出"按根 tpl 去重"的字典并 <c>return false</c>（跳过原实现）——
///   **完全不动 <c>GetDefaultPresets()</c>（按 preset id 的那个）** ⇒ 跳蚤/交易/预设 UI 里的两个预设都还在 ✔
///   任何意外 ⇒ <c>catch { return true; }</c> ⇒ 退回 SPT 原实现（保持原行为，绝不因本补丁多出新的失败模式）✔
///
/// ⚠ 顺带修好的：`toiletPaper`（原版 <c>useForcedLoot:true</c>，权重 1）+ 我们
///   <c>airdropAddLoot</c> 强制 <c>useForcedLoot</c> 的 `foodMedical`(airdrops_2026) /
///   `mixed`(tarkov_mystery) / `radar`(green_flare · mortar_strike) + 任务召唤箱（映射到 radar）✔
/// </summary>
internal static class PresetDedupePatch
{
    private const string HarmonyId = "com.kirisame.eventrotator.presetdedupe";
    private static readonly object Gate = new();
    private static Harmony _harmony;
    private static bool _patched;
    private static bool _loggedDedupe;          // "第一次去重"只打一条
    private static bool _selfChecked;           // 自查只做一次
    /// <summary>
    /// 去重结果的缓存：**按 `PresetHelper` 实例**存（不跨实例复用）。
    /// ⚠ 用弱表而不是单个静态字段：SPT 正常只有一个 `PresetHelper`（Singleton），
    ///   但探针/将来的 Transient 化会让"同一个静态缓存被别的实例读到"⇒ 那就是错的（探针实测踩到过）。
    /// </summary>
    private static ConditionalWeakTable<PresetHelper, Dictionary<MongoId, Preset>> _cacheTable = new();

    /// <summary>
    /// ★ 自举：**不需要改 `Plugin.cs`**（Lead 要求"若要动 Plugin.cs 先告诉我"）——
    /// 模块初始化器在本程序集被加载时执行，早于任何空投请求；失败也只是"不补丁"（回到原行为）。
    /// 若 Lead 更希望走既有 `Patches.*.Enable(harmony, logger)` 风格，把下面这段删掉、
    /// 在 `Plugin.cs:206` 那行 harmony 之后加一句 `PresetDedupePatch.Enable(harmony);` 即可（本方法幂等）。
    ///
    /// ⚠ CA2255：`ModuleInitializer` 官方不建议用于"库"，但那指的是**被别人引用的通用库**；
    ///   本程序集是 SPT 的**插件**（进程内的最终产物，等价于应用代码）⇒ 这里显式压制并说明理由。
    /// </summary>
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Bootstrap()
    {
        try { Enable(null); } catch { /* 自举失败 = 不补丁，不影响游戏 */ }
    }
#pragma warning restore CA2255

    /// <summary>应用补丁（幂等：重复调用只生效一次）。<paramref name="harmony"/> 为空时用自带实例。</summary>
    internal static bool Enable(Harmony harmony)
    {
        lock (Gate)
        {
            if (_patched) return true;
            try
            {
                var target = AccessTools.Method(typeof(PresetHelper), nameof(PresetHelper.GetDefaultPresetsByTplKey));
                if (target == null) return false;
                harmony ??= new Harmony(HarmonyId);
                harmony.Patch(target, new HarmonyMethod(typeof(PresetDedupePatch), nameof(Prefix)));
                _harmony = harmony;
                _patched = true;
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>撤销补丁（排障/探针用）——撤销后立刻回到"必崩"的原行为。</summary>
    internal static void Disable()
    {
        lock (Gate)
        {
            try { _harmony?.Unpatch(typeof(PresetHelper).GetMethod(nameof(PresetHelper.GetDefaultPresetsByTplKey)), HarmonyPatchType.Prefix, _harmony.Id); }
            catch { }
            _patched = false;
            _cacheTable = new();      // 清缓存（撤销后重新算）
            _loggedDedupe = false;
        }
    }

    /// <summary>
    /// 替换 <c>GetDefaultPresetsByTplKey()</c>：**同根 tpl 只留第一个**。
    /// 用显式 <c>foreach + ContainsKey</c>（不用 <c>ToDictionary</c>）⇒ **这个方法本身不可能因重复 key 抛** ✔
    /// </summary>
    private static bool Prefix(PresetHelper __instance, ref Dictionary<MongoId, Preset> __result)
    {
        try
        {
            __result = _cacheTable.GetValue(__instance, Build);   // 同一实例只算一次（不重复分组）；不同实例各算各的
            return false;   // 跳过原实现（它在同根时会抛 ArgumentException）
        }
        catch { return true; }   // ★ 退路：退回 SPT 原实现
    }

    private static Dictionary<MongoId, Preset> Build(PresetHelper ph)
    {
        var result = new Dictionary<MongoId, Preset>();
        var dups = new Dictionary<MongoId, int>();          // 根 tpl → 被丢弃的条数
        var keptName = new Dictionary<MongoId, string>();
        int total = 0;

        IEnumerable<Preset> all;
        try { all = ph.GetDefaultWeaponPresets().Values.Concat(ph.GetDefaultEquipmentPresets().Values); }
        catch { all = Enumerable.Empty<Preset>(); }
        foreach (var preset in all)
        {
            if (preset?.Items == null || preset.Items.Count == 0) continue;
            var root = preset.Items[0].Template;
            total++;
            if (result.ContainsKey(root)) { dups[root] = dups.TryGetValue(root, out var n) ? n + 1 : 1; continue; }
            result[root] = preset;
            keptName[root] = preset.Name;
        }

        if (!_loggedDedupe && dups.Count > 0)
        {
            _loggedDedupe = true;
            foreach (var kv in dups)
                Log($"[EventRotator][preset 去重] ★ 同根 preset 冲突已去重：根 tpl={kv.Key}"
                  + $"（保留「{keptName.GetValueOrDefault(kv.Key)}」，丢弃 {kv.Value} 条）"
                  + $" ⇒ 保留 1/{kv.Value + 1} 条（原先会让 CreateForcedLoot 抛 ArgumentException）");
            Log($"[EventRotator][preset 去重] 本轮共去重 {result.Count}/{total} 条（根 tpl 相同的只留第一条）"
              + "；**只影响强制掉落空投**（radar / toiletPaper / 我们强制打开的类型），"
              + "按 preset id 的 `GetDefaultPresets()` 未改动 ⇒ 两个武器预设照旧可用");
        }

        SelfCheckOnce(ph);      // 自查日志（只读，只做一次）
        return result;
    }

    // ───────────────────────── preset 冲突自查（只读） ─────────────────────────

    /// <summary>
    /// ★ 自查：按根 tpl 列出 `globalTable.ItemPresets` 里**所有**重复的根（含没通过过滤的那些）。
    /// 无副作用（只读 + 只打日志）；默认在**第一次** <c>GetDefaultPresetsByTplKey()</c> 调用时自动跑一次
    /// （等价于"进图前必然跑过"，且**不需要改 `Plugin.cs`**）。
    /// 若 Lead 想让它在 `OnLoadAsync` 末尾更早出现：在 `Plugin.cs` 那次 `new Harmony(...)` 附近加一句
    /// <c>PresetDedupePatch.SelfCheck(GlobalTableRef, logger);</c> 即可（本方法幂等）。
    /// </summary>
    internal static void SelfCheck(object globalTable, ISptLogger<EventRotatorServer> logger)
    {
        if (_selfChecked) return;
        _selfChecked = true;
        try
        {
            var presets = globalTable?.GetType()
                .GetProperty("ItemPresets", BindingFlags.Public | BindingFlags.Instance)?.GetValue(globalTable) as System.Collections.IDictionary;
            if (presets == null) { logger?.Info("[EventRotator][preset 自查] 拿不到 GlobalTable.ItemPresets ⇒ 跳过（无副作用）"); return; }

            var byRoot = new Dictionary<string, List<string>>();
            foreach (System.Collections.DictionaryEntry de in presets)
            {
                if (de.Value == null) continue;
                var items = de.Value.GetType().GetProperty("Items", BindingFlags.Public | BindingFlags.Instance)?.GetValue(de.Value) as System.Collections.IEnumerable;
                string root = null;
                if (items != null)
                    foreach (var it in items)
                    {
                        root = it?.GetType().GetProperty("Template", BindingFlags.Public | BindingFlags.Instance)?.GetValue(it)?.ToString();
                        break;   // 只要第一件
                    }
                if (string.IsNullOrEmpty(root)) continue;
                if (!byRoot.TryGetValue(root, out var l)) byRoot[root] = l = new List<string>();
                l.Add(de.Key?.ToString());
            }
            var dup = byRoot.Where(kv => kv.Value.Count > 1).OrderByDescending(kv => kv.Value.Count).ToList();
            if (dup.Count == 0)
            {
                logger?.Info($"[EventRotator][preset 自查] ✔ ItemPresets 共 {presets.Count} 条，**没有**同根 tpl 冲突");
                return;
            }
            logger?.Warning($"[EventRotator][preset 自查] ⚠ 发现 {dup.Count} 组同根 preset："
                          + string.Join(" · ", dup.Select(kv => kv.Key + $"(×{kv.Value.Count})"))
                          + " —— 已由 PresetDedupePatch 去重，不会崩（原先会让 `/client/airdrop/loot` 抛 Critical）");
        }
        catch (Exception ex) { logger?.Warning($"[EventRotator][preset 自查] 失败（不影响游戏）: {ex.Message}"); }
    }

    private static void SelfCheckOnce(PresetHelper ph)
    {
        if (_selfChecked) return;
        try
        {
            var gt = AccessTools.GetDeclaredFields(typeof(PresetHelper))
                .FirstOrDefault(f => f.FieldType.Name == "GlobalTable")?.GetValue(ph);
            SelfCheck(gt, ExtraDimensions.Log);
        }
        catch { }
    }

    private static void Log(string msg)
    {
        try { ExtraDimensions.Log?.Warning(msg); } catch { }
    }
}
