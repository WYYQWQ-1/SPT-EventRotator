using System;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★★★ v2.6.42【**常驻** · 用户裁决「常驻b」= 倍率 B（×3）+ 方案 ①（常驻）】
///
/// 用户原话：「查一下每一局掉落的空投上限,太低了就给他改高」⇒「常驻b」
///
/// 做什么（**与活动无关，永远生效**）：
///   · 每张图的 `locations/&lt;map&gt;/base.json → AirdropParameters[*].PlaneAirdropMax` **×3**
///     （实机原值：bigmap/interchange/lighthouse/rezervbase/shoreline/woods/sandbox_high 都是 **1**，
///      sandbox/tarkovstreets 是 **2**，develop 是 0 ⇒ **1→3、2→6、0 保持 0**）；
///   · `PlaneAirdropChance` **抬高到 ≥ 0.5**（**只升不降**：原 0.10/0.125/0.20/0.25 → 0.5；
///      已经 ≥0.5 的不动）。理由：上限从 1 抬到 3、而首轮掷骰只有 10~25% ⇒ **不改概率的话上限基本用不上**
///      （80% 的局一架都不来）⇒ 这条与"改高上限"是同一个诉求的两半，**不是另一件事**。
///
/// 为什么落在这里（而不是某个活动里）：
///   · 「常驻」= **无活动时也要生效**（像 `ItemSupply` 那样）⇒ 由 `Plugin.cs` 在**每次套用**时调用
///     （含"无活动"分支），见 `Plugin.cs` 的两处 `AirdropBaseline.Ensure(logger);`；
///   · **幂等**：`_applied` 标记 ⇒ 反复调用只生效一次（不会 ×3 变成 ×9、×27）；
///   · **就绪重试**：`LocationTable` 在启动早期可能是 `null` ⇒ 这时**不标记已生效**、打**明确的未生效警告**
///     并**留待下次套用重试**（**绝不静默放弃**）；
///   · **不登记还原**（常驻）⇒ `Restore()` 不会把它撤掉；而活动的 `ApplyFrequency(mult)` 会在这条基线上
///     **再乘一次**（活动自己登记还原 ⇒ 活动结束回到基线值）⇒ 两者叠加且互不破坏 ✔
///
/// ⚠ 关于"活动倍率会不会叠加到过大"（Lead 的疑问，实测推理）：
///   每张图的 `PlaneAirdropCooldownMin/Max` 是 **600~1000 秒**、`PlaneAirdropStartMin/Max` 是 300~1200 秒，
///   一局 20~45 分钟 ⇒ **实际最多也就 2~3 架**（冷却才是真正的瓶颈）⇒
///   灯塔（基线 ×3 后再叠活动 ×4 = 名义 12）与原版 ×4（名义 4）在**实际架次上基本无差别**，
///   只是把"名义上限"抬高了；**不会变成 12 架飞机**。
/// </summary>
internal static class AirdropBaseline
{
    /// <summary>单局空投上限倍率（用户裁决的"倍率 B" ⇒ ×3）。</summary>
    public const double MaxMultiplier = 3.0;

    /// <summary>★v2.6.69【用户裁决「封顶 6」】基线阶段单图 <c>PlaneAirdropMax</c> 的**硬上限**。
    /// 原因：整份替换地图数据的 mod（ManimalInterchange / ManimalLighthouse）会在加载期把这两张图的
    /// <c>PlaneAirdropMax</c> 设成 <b>5</b>（磁盘 base.json 是 1）⇒ 基线 ×3 会变成 **15 架/局**，明显过多。
    /// ⇒ 基线阶段封顶 **6**：普通图 1×3=3、sandbox/tarkovstreets 2×3=6、Manimal 两图 5→封顶 6。
    /// ⚠ 只约束**基线**；活动倍率（×4/×6）仍在其上叠加（活动期间可超出 6，活动结束回到基线）。</summary>
    public const int MaxCap = 6;

    /// <summary>概率下限（只升不降）。</summary>
    public const double ChanceFloor = 0.5;

    private static readonly object Gate = new();
    private static bool _applied;
    private static int _misses;

    /// <summary>是否已经生效（探针/日志用）。</summary>
    internal static bool Applied => _applied;

    /// <summary>未就绪的失败次数（探针用；真机日志里也会体现）。</summary>
    internal static int Misses => _misses;

    /// <summary>
    /// **幂等 + 就绪重试**：在每次套用（含"无活动"分支）时调用一次。
    /// 已生效 ⇒ 直接返回；`LocationTable` 还没就绪 ⇒ 打未生效警告并留待下次。
    /// </summary>
    public static void Ensure(ISptLogger<EventRotatorServer> logger)
    {
        if (_applied) return;
        lock (Gate)
        {
            if (_applied) return;
            try
            {
                if (AirdropDimension.LocationTable == null)
                {
                    _misses++;
                    logger?.Warning($"[EventRotator][空投·常驻] ⚠ 拿不到 LocationTable（第 {_misses} 次）⇒ "
                                  + "**单局空投上限 ×3 / 概率下限 0.5 这次没生效**，将在下次套用时重试（未静默放弃）");
                    return;                                  // ★ 不标记已生效 ⇒ 下次重试
                }
                bool ok = AirdropDimension.ApplyBaseline(MaxMultiplier, ChanceFloor, MaxCap, logger, out var detail);
                if (!ok)
                {
                    _misses++;
                    logger?.Warning($"[EventRotator][空投·常驻] ⚠ 这张 LocationTable 里没读到任何 AirdropParameters"
                                  + $"（第 {_misses} 次）⇒ **未生效**，下次重试");
                    return;
                }
                _applied = true;
                logger?.Info($"[EventRotator][空投·常驻] ★ 单局空投上限 ×{MaxMultiplier:0.##} + 概率下限 {ChanceFloor:0.##} "
                           + $"已生效（常驻，无活动也生效）：{detail}｜活动倍率在其上叠加（活动结束回到本基线）");
            }
            catch (Exception ex)
            {
                _misses++;
                logger?.Warning($"[EventRotator][空投·常驻] 应用失败（第 {_misses} 次，下次重试）: {ex.Message}");
            }
        }
    }

    /// <summary>排障/探针用：清掉"已生效"标记（真机不需要）。</summary>
    internal static void ResetForProbe() { lock (Gate) { _applied = false; _misses = 0; } }
}
