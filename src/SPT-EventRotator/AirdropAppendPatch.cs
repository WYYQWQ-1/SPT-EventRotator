using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Location;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Inventory;
using SPTarkov.Server.Core.Services.InRaid;

namespace EventRotator;

/// <summary>
/// ★★★ v2.6.41【P2 补丁 · 用户批准「做个补丁」】**让"随机池"与"我们塞的物品"共存**。
///
/// 背景（P0 修复留下的副作用）：
///   我们的 `airdropAddLoot`（`events.json`）原本靠"把目标类型强制成 <c>useForcedLoot = true</c> + 往
///   <c>ForcedLoot</c> 里塞物品"来实现"加物品"。但 SPT 的强制掉落是**整箱替换**：
///   <c>AirdropService.cs:52-59</c>（<c>UseForcedLoot ⇒ 只走 CreateForcedLoot</c>）⇒
///   随机内容被吃掉：`airdrops_2026` 的 foodMedical 从"25~45 件随机"变成"只有我们塞的 3 件"，
///   `tarkov_mystery` 的 mixed 从"15~35 件随机 + 3~5 武器预设"变成"只有加密闪存卡 1~2 件" ✗
///
/// 本补丁的语义（**保留随机 + 生成后追加我们的物品**）：
///   · `AirdropDimension` 对**本来不是 forced 的类型**不再强制 <c>useForcedLoot</c>，改为 <see cref="Register"/>；
///   · <c>GenerateAirdropLoot</c> 的 postfix 在**官方生成完之后**把我们的物品追加进箱子
///     （能塞进去就塞、塞不进就跳过 + 日志；**任何失败都不影响空投生成**）。
///
/// ⚠ **原生 forced 类型（radar / toiletPaper）绝对不动**：
///   `AirdropDimension` 只在"原值不是 true"时才改走追加 ⇒ `radar`（`mortar_strike`/`green_flare`）与
///   原版 `toiletPaper` 仍然走 forced（雷达站备用零件 ×2~4 的实战效果不回归 ✔）；这类**登记表为空**，
///   postfix 直接原样返回 ✔
/// ⚠ **与既有 `AirdropAppendDimension`（`airdropAppendLoot`）的关系**：
///   两者都是对 `AirdropService.GenerateAirdropLoot` 打 postfix，**互补、不会重复加同一件物品**：
///   <list type="bullet">
///     <item>`AirdropAppendDimension`（v0.1.5，官方 Weapon Case 那条线）：**全局**一份物品表 + `onlyOnMap` 门控，
///       <b>不区分空投类型</b>、不做容量校验 ⇒ "每次空投都追加"。</item>
///     <item>本文件（v2.6.41，`airdropAddLoot` 那条线）：**按空投类型**登记、**容量校验 + 堆叠拆分 + 塞不下跳过**。</item>
///   </list>
///   两者的物品来源分别是 `airdropAppendLoot` / `airdropAddLoot` ⇒ 不会对同一件物品加两次；
///   若将来要合并成一套（推荐），把"按类型 + 容量校验"这两点并入 `AirdropAppendDimension` 即可（P3 待办）。
/// </summary>
internal static class AirdropAppendPatch
{
    private const string HarmonyId = "com.kirisame.eventrotator.airdropappend";

    // ── 登记表：空投类型（枚举名，如 foodMedical / mixed）→ 要追加的物品 ──
    private sealed class Entry { public string Type; public string Tpl; public int Min; public int Max; public string Source; }
    private static readonly Dictionary<string, List<Entry>> Registry = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>
    /// ★ v2.6.70【性能 · G3】**不可变快照**：postfix 直接读快照（**不复制、不加锁**）；
    /// 只在 `Register/Unregister` 时重建（低频）。快照内容 = 登记表内容（同样的 `(tpl,min,max)`）⇒ 行为等价。
    /// </summary>
    private static volatile Entry[][] _snapshot = System.Array.Empty<Entry[]>();

    /// <summary>★ v2.6.70【性能 · G2】`ItemHelper` 捕获字段只解析一次
    /// （原来**每次空投**都要 `AccessTools.GetDeclaredFields` 扫一遍字段 + LINQ）。</summary>
    private static readonly Lazy<FieldInfo> _itemHelperField = new(() =>
        AccessTools.GetDeclaredFields(typeof(AirdropService)).FirstOrDefault(f => f.FieldType == typeof(ItemHelper)));

    /// <summary>`GetAirdropLootConfigByType` 前缀记下的"本次真正用的类型"（跨方法传递；AsyncLocal 以兼容并发请求）。</summary>
    private static readonly AsyncLocal<string> CurrentType = new();

    private static Harmony _harmony;
    private static bool _patched;

    /// <summary>自举（`[ModuleInitializer]`）：不改 `Plugin.cs` 也能生效；失败 = 不补丁（回到原行为）。</summary>
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Bootstrap() { try { Enable(null); } catch { } }
#pragma warning restore CA2255

    internal static bool Enable(Harmony harmony)
    {
        lock (Gate)
        {
            if (_patched) return true;
            try
            {
                var t = typeof(AirdropService);
                var mConfig = AccessTools.Method(t, "GetAirdropLootConfigByType");
                var mGen = AccessTools.Method(t, "GenerateAirdropLoot");
                if (mConfig == null || mGen == null) return false;
                harmony ??= new Harmony(HarmonyId);
                harmony.Patch(mConfig, new HarmonyMethod(typeof(AirdropAppendPatch), nameof(PrefixRecordType)));
                harmony.Patch(mGen, null, new HarmonyMethod(typeof(AirdropAppendPatch), nameof(PostfixAppend)));
                _harmony = harmony;
                _patched = true;
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>撤销（排障/探针用）。</summary>
    internal static void Disable()
    {
        lock (Gate)
        {
            try
            {
                var t = typeof(AirdropService);
                _harmony?.Unpatch(AccessTools.Method(t, "GetAirdropLootConfigByType"), HarmonyPatchType.Prefix, _harmony.Id);
                _harmony?.Unpatch(AccessTools.Method(t, "GenerateAirdropLoot"), HarmonyPatchType.Postfix, _harmony.Id);
            }
            catch { }
            _patched = false;
        }
    }

    // ────────────────────────── 登记 / 注销（供 `AirdropDimension` 调用）──────────────────────────

    /// <summary>登记"这个类型的空投要追加这件物品"（`[min,max]` 与 `events.json` 的 `airdropAddLoot` 同义）。</summary>
    internal static string Register(string type, string tpl, int min, int max, string source = null)
    {
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(tpl)) return "";
        // ★ 2026-10-31【加固】每条登记带唯一来源标记 ⇒ 撤回时只删自己这条，不按 tpl 全删
        source ??= "src:" + Guid.NewGuid().ToString("N");
        lock (Gate)
        {
            if (!Registry.TryGetValue(type, out var list)) Registry[type] = list = new List<Entry>();
            var old = list.FindIndex(e => string.Equals(e.Tpl, tpl, StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(e.Source, source, StringComparison.Ordinal));
            if (old >= 0) list[old] = new Entry { Type = type, Tpl = tpl, Min = min, Max = max, Source = source };
            else list.Add(new Entry { Type = type, Tpl = tpl, Min = min, Max = max, Source = source });
            _snapshot = Registry.Values.Select(l => l.ToArray()).ToArray();  // ★ G3：只在登记变化时重建
        }
        return source;
    }

    /// <summary>注销（活动退出时由还原闭包调用）。</summary>
    internal static void Unregister(string type, string tpl, string source = null)
    {
        lock (Gate)
        {
            if (!Registry.TryGetValue(type, out var list)) return;
            // ★ 只删"同一个来源标记"的那几条；source 为空时退化为按 tpl（兼容旧调用）
            list.RemoveAll(e => string.Equals(e.Tpl, tpl, StringComparison.OrdinalIgnoreCase)
                             && (source == null || string.Equals(e.Source, source, StringComparison.Ordinal)));
            if (list.Count == 0) Registry.Remove(type);
            _snapshot = Registry.Values.Select(l => l.ToArray()).ToArray();  // ★ G3
        }
    }

    internal static bool HasAny => Registry.Count > 0;

    // ────────────────────────── Harmony ──────────────────────────

    /// <summary>记下"本次真正用的空投类型"（`GenerateAirdropLoot` 里已经解析过，非 null）。</summary>
    private static void PrefixRecordType(SptAirdropTypeEnum? airdropType)
    {
        try { CurrentType.Value = airdropType?.ToString(); } catch { }
    }

    /// <summary>
    /// 官方生成完之后**追加**我们的物品：能塞进箱子就塞、塞不进就跳过 + 日志；
    /// **任何异常都吞掉**（绝不能让空投生成失败）。
    /// </summary>
    private static void PostfixAppend(AirdropService __instance, ref GetAirdropLootResponse __result)
    {
        long t0 = PerfTimer.Start();          // ★ v2.6.70【Y2】仅计时（关闭时恒为 0 ⇒ 零开销、零行为变化）
        try
        {
            var type = CurrentType.Value;
            CurrentType.Value = null;                       // 用完即清，避免影响别的调用
            if (string.IsNullOrEmpty(type) || __result?.Container == null) return;

            // ★ G3：从**不可变快照**里挑出本类型那一条（不复制、不加锁；登记变化时才重建）
            var snap = _snapshot;
            Entry[] ours = null;
            foreach (var group in snap)
                if (group.Length > 0 && string.Equals(group[0].Type, type, StringComparison.OrdinalIgnoreCase)) { ours = group; break; }
            if (ours == null || ours.Length == 0) return;    // ★ 没登记 ⇒ 原样返回（radar/toiletPaper 不受影响）

            var items = new List<Item>(__result.Container);
            if (items.Count == 0) return;
            var crate = items[0];                            // 官方：箱子永远是第一个（AirdropService.cs:60-66）
            var itemHelper = _itemHelperField.Value?.GetValue(__instance) as ItemHelper;   // ★ G2：只解析一次
            if (itemHelper == null) { Log($"[EventRotator][空投追加] '{type}'：拿不到 ItemHelper ⇒ 跳过（无副作用）"); return; }

            // ① 先把"箱子里已有的物品"占位填进容器图，避免超容量
            int[,] map;
            try { map = itemHelper.GetContainerMapping(crate.Template); } catch { map = null; }
            if (map == null) { Log($"[EventRotator][空投追加] '{type}'：箱子 {crate.Template} 没有容器图 ⇒ 跳过（无副作用）"); return; }
            foreach (var it in items.Skip(1)) Fill(map, itemHelper, it);

            // ② 逐件追加
            int added = 0, skipped = 0;
            foreach (var e in ours)
            {
                int n = e.Min >= e.Max ? e.Min : Random.Shared.Next(e.Min, e.Max + 1);
                int maxStack = StackMax(itemHelper, e.Tpl);
                while (n > 0)
                {
                    int take = Math.Min(n, maxStack);
                    n -= take;
                    var one = new Item
                    {
                        Id = new MongoId(),
                        Template = new MongoId(e.Tpl),
                        Upd = new Upd { StackObjectsCount = take, SpawnedInSession = true },
                    };
                    ItemSize size = null;
                    try { size = itemHelper.GetItemSize(new List<Item> { one }, one.Id); } catch { }
                    FindSlotResult slot = null;
                    if (size != null) { try { slot = map.FindSlotForItem(size.Width, size.Height); } catch { } }
                    if (slot?.Success != true)
                    {
                        skipped++;
                        Log($"[EventRotator][空投追加] '{type}'：箱子塞不下 {e.Tpl}×{take} ⇒ 跳过（能塞的照塞，不影响生成）");
                        continue;
                    }
                    try { map.TryFillContainerMapWithItem(slot.X.Value, slot.Y.Value, size.Width, size.Height, slot.Rotation ?? false, out string _); } catch { }
                    one.ParentId = crate.Id.ToString();      // ★ 归属：箱子的 `main` 槽（与官方 :66-73 同款写法）
                    one.SlotId = "main";
                    items.Add(one);
                    added++;
                }
            }
            if (added == 0 && skipped == 0) return;
            __result = __result with { Container = items };
            Log($"[EventRotator][空投追加] '{type}'：随机内容 {items.Count - added - 1} 件 + **追加 {added} 件**"
              + (skipped > 0 ? $"（{skipped} 件塞不下已跳过）" : "")
              + $"｜登记物品 {string.Join("，", ours.Select(o => o.Tpl + $"[{o.Min}~{o.Max}]"))}");
        }
        catch (Exception ex)
        {
            try { Log($"[EventRotator][空投追加] 追加失败（已忽略，空投内容保持官方随机结果）: {ex.Message}"); } catch { }
        }
        finally { PerfTimer.End("空投追加.Postfix(每空投)", t0); }   // ★ v2.6.70【Y2】：覆盖所有 return 出口
    }

    /// <summary>把一件已有物品按尺寸占进容器图（占不了就算了，只影响"能否再塞"的保守程度）。</summary>
    private static void Fill(int[,] map, ItemHelper ih, Item one)
    {
        try
        {
            var size = ih.GetItemSize(new List<Item> { one }, one.Id);
            if (size == null) return;
            var slot = map.FindSlotForItem(size.Width, size.Height);
            if (slot?.Success == true)
                map.TryFillContainerMapWithItem(slot.X.Value, slot.Y.Value, size.Width, size.Height, slot.Rotation ?? false, out string _);
        }
        catch { }
    }

    /// <summary>该 tpl 的堆叠上限（查不到按 1 ⇒ 一件一件地放，绝不无限循环）。</summary>
    private static int StackMax(ItemHelper ih, string tpl)
    {
        try
        {
            var kv = ih.GetItem(tpl);
            var v = kv.Value?.Properties?.StackMaxSize ?? 1;
            return v < 1 ? 1 : (int)v;
        }
        catch { return 1; }
    }

    private static void Log(string msg)
    {
        try { ExtraDimensions.Log?.Info(msg); } catch { }
    }
}
