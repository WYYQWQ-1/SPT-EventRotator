using System;

namespace EventRotator;

/// <summary>
/// ★★★ v2.6.39【opt-in · 用户裁决 2026-10-16】官方装备注入的「**保住首领标志性装备**」过滤器。
///
/// 背景（核查结论见 `audit/骑士面罩-核查.md`）：
///   官方 `SeasonalEventService.AddEventGearToBots`（`SeasonalEventService.cs:948`）是
///   **往装备池里直接赋值、且不清理原有条目**：
///   <code>botType.BotInventory.Equipment[key][tpl] = 官方权重;</code>
///   于是 `bossknight.FaceCover` 会从 `{Death Knight面具: 1}` 变成 `{面具: 1, 假白胡子: 1500}`
///   ⇒ 面具命中率掉到 1/1501 ≈ **0.067%**（官方数据 `eventGear.christmas` 原文就是 1500）。
///   **这是官方行为**，所以默认必须照官方 —— 本开关是**显式的、默认关**的非官方选项：
///   `eventGearKeepBossIconic: true` ⇒ 对 `boss*` / `follower*` 角色的 `FaceCover` / `Headwear` 槽
///   **整槽跳过写入**（连"概率抬到 75"一起跳），从而保住那件标志性装备。
///
/// 两条注入路径**共用本过滤器**：`ExtraDimensions.ApplyBotEquipment`（走 bots/types 数据层）
/// 与 `PbsCompat.ApplyEventGear`（走 PBS 运行期池，装了 PBS 的机器实际吃这条）。
/// </summary>
internal static class EventGearSlotFilter
{
    /// <summary>该 bot 角色是不是「首领 / 随从」（官方 22 个角色里的 <c>boss*</c> / <c>follower*</c>）。</summary>
    public static bool IsBossOrFollower(string botKey)
    {
        var k = (botKey ?? "").Trim();
        return k.StartsWith("boss", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("follower", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>是不是「标志性单件槽」—— 面具 / 头饰（官方节日装备会把它们顶掉）。</summary>
    public static bool IsIconicSlot(string slot)
    {
        var s = (slot ?? "").Trim();
        return string.Equals(s, "FaceCover", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "Headwear", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 要不要**跳过这个槽的写入**：只有 <paramref name="keepBossIconic"/> 打开 + 首领/随从 + 面具/头饰
    /// 三条**同时满足**才跳过（其余一律照官方原样写）。
    /// </summary>
    public static bool Skip(string botKey, string slot, bool keepBossIconic)
        => keepBossIconic && IsBossOrFollower(botKey) && IsIconicSlot(slot);
}
