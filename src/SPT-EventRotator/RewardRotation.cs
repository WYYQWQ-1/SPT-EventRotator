using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace EventRotator;

/// <summary>
/// ★ 奖励类轮换（v2.2.0）——《不改敌人、只对玩家有利》的那批活动，随机开 N~M 个。
///
/// 用户需求原话：「把这类不改敌人只对玩家的分类到奖励分类，也加入随机轮换里面，
///                概率开启几个到几个，注意别冲突」。
///
/// 与主轮换（Rotation）的区别：
///   · 主轮换 = 一次只开一个"大活动"（走私者/首领聚会/邪教之夜…），会改敌人、改地图。
///   · 奖励轮换 = 从 category = "reward" 的活动里，**随机挑 countMin ~ countMax 个同时开**，
///     这些活动只改玩家侧的数值/掉落/商人，不引入敌种，所以可以叠加。
///     仍然做冲突检查（比如两个都要占同一个 map 的首领位）。
///
/// config.json：
/// <code>
/// "rewardRotation": {
///   "enabled": true,
///   "countMin": 1,
///   "countMax": 2,
///   "avoidConflicts": true,
///   "seed": 0,
///   "pool": []
/// }
/// </code>
/// </summary>
internal static class RewardRotation
{
    public static bool Enabled;
    public static int CountMin = 1;
    public static int CountMax = 2;
    public static bool AvoidConflicts = true;
    public static int Seed;
    public static readonly List<string> Pool = new();

    private static void Info(string m) { try { EventEngine.Log?.Info("[EventRotator][奖励轮换] " + m); } catch { } }

    public static void Load(JsonElement root, SPTarkov.Common.Models.Logging.ISptLogger<EventRotatorServer> log)
    {
        try
        {
            if (!root.TryGetProperty("rewardRotation", out var r) || r.ValueKind != JsonValueKind.Object)
                return;

            // ★ v1.3.8【热重载修正】同 Rotation：pool 先清空，避免删键后热重载仍用旧名单。
            Pool.Clear();
            if (r.TryGetProperty("enabled", out var en) && (en.ValueKind == JsonValueKind.True || en.ValueKind == JsonValueKind.False))
                Enabled = en.GetBoolean();
            if (r.TryGetProperty("countMin", out var a) && a.ValueKind == JsonValueKind.Number)
                CountMin = Math.Max(0, a.GetInt32());
            if (r.TryGetProperty("countMax", out var b) && b.ValueKind == JsonValueKind.Number)
                CountMax = Math.Max(0, b.GetInt32());
            if (CountMax < CountMin) CountMax = CountMin;
            if (r.TryGetProperty("avoidConflicts", out var ac) && (ac.ValueKind == JsonValueKind.True || ac.ValueKind == JsonValueKind.False))
                AvoidConflicts = ac.GetBoolean();
            if (r.TryGetProperty("seed", out var sd) && sd.ValueKind == JsonValueKind.Number)
                Seed = sd.GetInt32();
            if (r.TryGetProperty("pool", out var pl) && pl.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var x in pl.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String) { var s = x.GetString(); if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim()); }
                if (list.Count > 0) { Pool.Clear(); Pool.AddRange(list); }
            }
            log?.Warning($"[EventRotator] ★奖励类轮换已启用：每局随机开 {CountMin}~{CountMax} 个『奖励类』活动"
                         + $"（避冲突={AvoidConflicts}，池子={(Pool.Count > 0 ? string.Join(",", Pool) : "全部 reward 分类")}）");
        }
        catch (Exception ex) { log?.Warning($"[EventRotator] rewardRotation 配置解析失败: {ex.Message}"); }
    }

    /// <summary>是否是"奖励类"活动（category = reward，或没有敌种维度的 tweak）。</summary>
    public static bool IsReward(EventDef e)
    {
        var c = (e.Category ?? "").Trim().ToLowerInvariant();
        if (c == "reward") return true;
        return false;
    }

    /// <summary>
    /// 本档位要开的奖励活动。
    /// ★ v1.0.1：从"按天"改成"按档位" —— 跟随 `rotation.intervalDays` / `slotRebaseHour`，
    ///   与主轮换同档同换；同一档位内重启不变。
    /// </summary>
    public static List<EventDef> PickForToday(List<EventDef> all, List<EventDef> already)
    {
        var res = new List<EventDef>();
        try
        {
            if (!Enabled) return res;

            // ★ v0.1.3 修正：原来是 `(e.Enabled || true)` —— **恒真条件**，等于没过滤（显然是笔误）。
            //   而 `Enabled` 字段本身已在 v0.1.3 整体删除（它在轮换/日期窗上语义不一致、属无效数据）。
            //   奖励轮换本来就应该覆盖**全部** reward 类活动（要不要开由 rewardRotation.enabled 决定），
            //   所以这里直接去掉该条件，语义更清楚。
            var pool = all.Where(e => e != null && IsReward(e)).ToList();

            // ★★★ v1.5.0【A2 修 · 用户裁决】奖励活动的**开启依据**要和主轮换一致：
            //   `seasonRotation.mode = rotate`（季节轮换开启）⇒ **不看日期窗**，完全由轮换决定；
            //   否则（fixed/off/跟活动）⇒ **按现实日期窗**，窗外的奖励活动不开
            //   （否则 8 月也可能开出 `thermal_for_treats`(12/24–25)、`softcore`(10/8–15) 这种专属奖励）。
            if (!SeasonRotation.IsRotating)
            {
                var today = EventEngine.TodayOverride ?? DateTime.Today;
                pool = pool.Where(e => EventEngine.InWindowOrTimeless(e, today)).ToList();
            }
            if (Pool.Count > 0)
                pool = pool.Where(e => Pool.Any(p => string.Equals(p, e.Id, StringComparison.OrdinalIgnoreCase))).ToList();
            // 去掉已经生效的（比如手动强制开的）
            pool = pool.Where(e => !already.Any(a => string.Equals(a.Id, e.Id, StringComparison.OrdinalIgnoreCase))).ToList();
            if (pool.Count == 0) { Info("奖励池为空（没有 category=reward 的活动，或都被排除了）"); return res; }

            // ★★★ v1.0.1【轮换周期统一 P0 修复】
            //   旧写法用 `(DateTime.Today - 2020-01-01).TotalDays` = **按天**换
            //   ⇒ 奖励活动每天都不一样，与玩家设定的 `rotation.intervalDays`（默认 7 天）**脱节**。
            //   用户规格：「随机轮换确实轮换活动，除了季节活动时间不定外，
            //            其他的活动就都该跟着玩家自己设定的轮换时间来的」
            //   ⇒ 改成跟随**玩家设定的档位**（`Rotation.CurrentSlot()` 已经含
            //     `intervalDays` 与 `slotRebaseHour`）⇒ 与主轮换**同档同换**。
            //   ⚠️ seed=0 时原来用 `day`（会变）；这里同样改成按档位派生的稳定值，
            //      避免「种子没设 ⇒ 每局都不同」的老问题。
            int slot = Rotation.CurrentSlot();
            var rnd = new Random(Seed != 0 ? Seed * 100003 + slot : unchecked(slot * 1103515245 + 12345));

            int want = rnd.Next(CountMin, CountMax + 1);

            // ★★★ v1.3.6【奖励活动超量 bug】用户实测提问引出的排查：
            //   主轮换 `Rotation.PickCore()` 内部也会 `TakeRewards(rotation.rewardCountMax)`（最多 2 个），
            //   而这里又独立抽 1~2 个 ⇒ 两边都生效时**总共可能出现 4 个奖励活动**，
            //   与 F12 文案「每局随机开 1~2 个奖励活动」不符。
            //   ⇒ 这里先扣掉「已经在跑」的奖励活动数量，保证总量不超过 CountMax。
            int alreadyRewards = already != null ? already.Count(IsReward) : 0;
            want = Math.Min(want, Math.Max(0, CountMax - alreadyRewards));
            if (want <= 0)
            {
                Info($"已有 {alreadyRewards} 个奖励活动在跑（上限 {CountMax}）⇒ 本次不再追加");
                return res;
            }
            want = Math.Min(want, pool.Count);

            // 打乱后按顺序取，边取边做冲突检查
            var shuffled = pool.OrderBy(_ => rnd.Next()).ToList();
            var touch = new List<EventDef>(already);
            foreach (var e in shuffled)
            {
                if (res.Count >= want) break;
                if (AvoidConflicts && touch.Any(t => Rotation.Conflicts(t, e))) continue;
                res.Add(e);
                touch.Add(e);
            }
            Info($"今日奖励活动（{res.Count}/{want} 个）：{(res.Count == 0 ? "(无)" : string.Join(" + ", res.Select(x => x.Id)))}");
        }
        catch (Exception ex) { Info("选奖励活动失败: " + ex.Message); }
        return res;
    }
}
