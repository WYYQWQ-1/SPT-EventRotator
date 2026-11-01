using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★ 填充活动轮换：季节活动的日期窗**之外**，按固定间隔轮换"填充活动"，
///   让玩家在两个季节活动之间也有内容可玩。
///
/// config.json：
/// <code>
/// "rotation": {
///   "enabled": true,
///   "mode": "sequential",      // sequential（顺序轮换）| random（随机轮换）
///   "intervalDays": 3,         // 每个填充活动持续几天
///   "count": 1,                // 每个档位同时开几个（默认 1）
///   "seed": 20261014,          // random 模式的种子（0 = 每次启动都不同）
///   "avoidConflicts": true,    // ★ 自动避开互斥活动（默认开，强烈建议别关）
///   "pool": []                 // 留空 = 全部非季节活动；也可手写 id 列表指定池子
/// }
/// </code>
///
/// ★ 冲突保护（avoidConflicts）：
///   两个活动只要**动过同一张图的同一个首领**，就判为互斥（硬冲突）。
///   判据完全从 events.json 自己算出来，不硬编码任何组名 ——
///   以后改配置、加活动都自动生效。
///   另外季节活动（halloween 等）也算进互斥判定，所以填充活动不会跟当季活动打架。
/// </summary>
internal static class Rotation
{
    public static bool Enabled;
    public static string Mode = "sequential";
    public static int IntervalDays = 3;
    public static int Count = 1;
    public static int Seed;
    public static bool AvoidConflicts = true;

    /// <summary>
    /// ★ 手动强制（force.txt）的活动持续天数。0 = 不限制（永久，旧行为）。
    /// 用法：force.txt 写一个活动 id 后，天数一到就自动结束（回到"无活动"或轮换）。
    /// 状态记在 mod 目录的 <c>rotation_state.json</c>（记录 forcedId + 起始时间），重启服务端也认。
    /// 自动轮换模式下用的是 intervalDays（每档天数），两者语义一致。
    /// </summary>
    public static int DurationDays;

    /// <summary>
    /// true（默认）= 严格只在**没有季节活动时**轮换（= 你原话"季节活动之间的填充"）；
    /// false = 季节活动期间也叠加填充（靠 avoidConflicts 保证不打架）。
    /// </summary>
    public static bool OnlyWhenNoSeasonal = true;
    /// <summary>
    /// ★★★ v2.6.39【用户规格·开关】大季节活动开启时，**是否也轮换"不冲突的" AI / 限时带任务活动**。
    /// 用户原话：「大季节活动开启时只轮换奖励活动,**不过做了有开关,开启开关可正常轮换不冲突的
    /// ai活动/限时带任务的活动**」。
    /// <para><b>默认 false = 完全保持现状</b>（大季节只叠奖励）⇒ **不改变已测行为**。
    /// 打开后：在"只叠奖励"之前，再用 `ok()`（= `avoidConflicts` + `already` 冲突检查 + 去重）
    /// 挑 **1 个**不冲突的 AI 或带任务活动。</para>
    /// </summary>
    public static bool RotateAiAndTaskDuringSeasonal;

    public static readonly List<string> Pool = new();

    /// <summary>
    /// ★ v1.3.8【轮换排除】`rotation.exclude` = 不参与**自动轮换**的活动 id 列表。
    /// <para>和 <see cref="Pool"/>（白名单：只有这些能轮换）互补，exclude 是黑名单：写进去的活动
    /// 永远不会被轮换选中、也不会被季节阶段自动开启；但**手动强制（force.txt）仍然有效**。</para>
    /// </summary>
    public static readonly List<string> Exclude = new();

    /// <summary>该 id 是否被用户排除在自动轮换之外。</summary>
    public static bool IsExcluded(string id)
        => id != null && Exclude.Count > 0 && Exclude.Contains(id.Trim(), StringComparer.OrdinalIgnoreCase);

    private static void Info(string m) { try { EventEngine.Log?.Info(m); } catch { } }
    private static void Warn(string m) { try { EventEngine.Log?.Warning(m); } catch { } }

    /// <summary>从 config.json 的 rotation 节点读配置。</summary>
    public static void Load(JsonElement root, ISptLogger<EventRotatorServer> log)
    {
        try
        {
            // ★ 2026-10-29【周/周末抽签】独立节点 `weeklyDraw`，必须**在 rotation 早退之前**读
            //   （和 SeasonRotation 的 eventSeason 同一个坑：配置里没有 `rotation` 节点时不能连它一起跳过）。
            LoadWeeklyDraw(root, log);
            if (!root.TryGetProperty("rotation", out var r) || r.ValueKind != JsonValueKind.Object)
                return;

            // ★ v1.3.8【热重载修正】Pool/Exclude 先清空再按文件重建。
            //   旧写法只在"键存在"时 Clear ⇒ 用户从 config 里删掉 pool/exclude 后热重载，
            //   内存里还留着上一份名单，表现为"删了没生效，重启才好"。
            Pool.Clear();
            Exclude.Clear();

            if (r.TryGetProperty("enabled", out var en) && (en.ValueKind == JsonValueKind.True || en.ValueKind == JsonValueKind.False))
                Enabled = en.GetBoolean();
            if (r.TryGetProperty("mode", out var md) && md.ValueKind == JsonValueKind.String)
                Mode = md.GetString() ?? "sequential";
            if (r.TryGetProperty("intervalDays", out var iv) && iv.ValueKind == JsonValueKind.Number)
                IntervalDays = Math.Max(1, iv.GetInt32());
            // ★ v0.1.3 换档时间基准（0~23 整点；默认 0，客户端默认写 4 = 凌晨 4 点换档）
            if (r.TryGetProperty("slotRebaseHour", out var srh) && srh.ValueKind == JsonValueKind.Number)
                SlotRebaseHour = Math.Clamp(srh.GetInt32(), 0, 23);
            if (r.TryGetProperty("count", out var ct) && ct.ValueKind == JsonValueKind.Number)
                Count = Math.Max(1, ct.GetInt32());
            if (r.TryGetProperty("seed", out var sd) && sd.ValueKind == JsonValueKind.Number)
                Seed = sd.GetInt32();
            if (r.TryGetProperty("avoidConflicts", out var ac) && (ac.ValueKind == JsonValueKind.True || ac.ValueKind == JsonValueKind.False))
                AvoidConflicts = ac.GetBoolean();
            if (r.TryGetProperty("onlyWhenNoSeasonal", out var owns) && (owns.ValueKind == JsonValueKind.True || owns.ValueKind == JsonValueKind.False))
                OnlyWhenNoSeasonal = owns.GetBoolean();
            // ★★★ v2.6.39【用户规格·开关】大季节活动开启时，除奖励外**是否也正常轮换"不冲突的" AI / 限时带任务活动**。
            //   用户原话：「大季节活动开启时只轮换奖励活动,**不过做了有开关,开启开关可正常轮换不冲突的
            //              ai活动/限时带任务的活动**」
            //   ⚠ **默认 false = 完全保持现状**（大季节只叠奖励）⇒ 不改变已测行为。
            if (r.TryGetProperty("rotateAiAndTaskDuringSeasonal", out var rai)
                && (rai.ValueKind == JsonValueKind.True || rai.ValueKind == JsonValueKind.False))
                RotateAiAndTaskDuringSeasonal = rai.GetBoolean();
            if (r.TryGetProperty("durationDays", out var dd) && dd.ValueKind == JsonValueKind.Number)
                DurationDays = Math.Max(0, dd.GetInt32());
            // ★ v0.1.4 组合规则
            if (r.TryGetProperty("rewardCountMin", out var rmn) && rmn.ValueKind == JsonValueKind.Number)
                RewardMin = Math.Clamp(rmn.GetInt32(), 0, 4);
            if (r.TryGetProperty("rewardCountMax", out var rmx) && rmx.ValueKind == JsonValueKind.Number)
                RewardMax = Math.Clamp(rmx.GetInt32(), RewardMin, 4);
            if (r.TryGetProperty("smallCountMax", out var smx) && smx.ValueKind == JsonValueKind.Number)
                SmallMax = Math.Clamp(smx.GetInt32(), 1, 2);
            if (r.TryGetProperty("pool", out var pl) && pl.ValueKind == JsonValueKind.Array)
            {
                Pool.Clear();
                foreach (var x in pl.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String) { var s = x.GetString(); if (!string.IsNullOrWhiteSpace(s)) Pool.Add(s.Trim()); }
            }
            // ★ v1.3.8：排除列表（黑名单）—— 玩家反馈「不想让跳蚤关闭/弹药荒进轮换」
            if (r.TryGetProperty("exclude", out var ex) && ex.ValueKind == JsonValueKind.Array)
            {
                Exclude.Clear();
                foreach (var x in ex.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String) { var s = x.GetString(); if (!string.IsNullOrWhiteSpace(s)) Exclude.Add(s.Trim()); }
            }

            if (Enabled)
                log?.Info($"[EventRotator] 轮换已启用：mode={Mode} 间隔={IntervalDays}天 每档={Count}个 避冲突={AvoidConflicts}"
                          + $" 仅限季节间隙={OnlyWhenNoSeasonal} 换档基准={SlotRebaseHour}:00"
                          + (Pool.Count > 0 ? $" 池子={Pool.Count}个(手写)" : " 池子=全部非季节活动")
                          + $"（本次启动已按时间预先算好：当前档位 #{CurrentSlot()}，下次换档 {NextSlotStart():yyyy-MM-dd HH:mm}）");
        }
        catch (Exception ex) { log?.Warning($"[EventRotator] rotation 配置解析失败: {ex.Message}"); }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ★ 2026-10-29【周 / 周末抽签】（对齐 PPES 的 weekly rotation，按本 mod 结构实现）
    //
    // config.json：
    //   "weeklyDraw": { "mode": "off", "count": 1, "onlyNoRestart": true, "seed": 0 }
    //
    // 规则：
    //   · weekly  = 每个 ISO 周（**周一起算**）抽 count 个，持续整周（周一 00:00 → 周日 23:59）
    //   · weekend = 每周六 00:00 → 周日 23:59 生效（同一周的周六/周日抽同一组）
    //   · 抽签是**纯函数**：`seed + 周序号 + 模式` 做稳定的 FNV 排序取前 N ⇒ 同一周同一种子永远同一组，
    //     与"今天周几""当前轮换档位""是否重启"全都无关（可离线复算）。
    //   · 候选过滤（全部与时间无关，保证可复现）：强制(force.txt) / `rotation.exclude` / 季节活动 /
    //     **日期窗与本周有重叠**（≈"日期窗已生效"）/ onlyNoRestart 时的 `uiTag=experimental`。
    //   · mode=off ⇒ 所有新逻辑**短路**：不挑、不加、不改日志、不改状态文件的任何一行（逐字节一致）。
    // ═══════════════════════════════════════════════════════════════════════════
    /// <summary>off（默认）| weekly | weekend。off 时本机制完全不存在（行为与改动前逐字节一致）。</summary>
    public static string WeeklyDrawMode = "off";
    /// <summary>每次抽几个（1~3）。</summary>
    public static int WeeklyDrawCount = 1;
    /// <summary>只抽「不需要重启就生效」的活动 ⇒ 排除 `uiTag=experimental`。</summary>
    public static bool WeeklyDrawOnlyNoRestart = true;
    /// <summary>抽签种子（0 = 就用 0，**不是随机**：同一周同一种子必须得到同一组）。</summary>
    public static int WeeklyDrawSeed;

    /// <summary>本周抽签实际加进 Actives 的活动（用于下一周撤下、以及状态文件"来源"行的标注）。</summary>
    private static readonly List<EventDef> _weeklyAdded = new();
    private static int _weeklyLoggedWeek = int.MinValue;
    private static string _weeklyLoggedSig = "";

    /// <summary>状态文件"来源"行用的只读快照。</summary>
    public static IReadOnlyList<EventDef> WeeklyAdded => _weeklyAdded;

    /// <summary>抽签是否真的开着（非法值一律当 off）。</summary>
    public static bool WeeklyDrawEnabled =>
        string.Equals(WeeklyDrawMode, "weekly", StringComparison.OrdinalIgnoreCase)
        || string.Equals(WeeklyDrawMode, "weekend", StringComparison.OrdinalIgnoreCase);

    /// <summary>中文标签（写进状态文件"来源"行）：周抽签 / 周末抽签；off 时返回空串。</summary>
    public static string WeeklyDrawTag()
        => !WeeklyDrawEnabled ? ""
           : (string.Equals(WeeklyDrawMode, "weekend", StringComparison.OrdinalIgnoreCase) ? "周末抽签" : "周抽签");

    /// <summary>该时刻属于哪一周（**以周一为界**，自 2020-01-06 起算；weekend 沿用同一周序号）。</summary>
    public static int WeekIndex(DateTime now) => WeekIndexOf(now.Date);

    private static int WeekIndexOf(DateTime date)
    {
        int dowFromMon = ((int)date.DayOfWeek + 6) % 7;          // 周一=0 … 周日=6
        var monday = date.AddDays(-dowFromMon);
        return (int)Math.Floor((monday - new DateTime(2020, 1, 6)).TotalDays / 7.0);
    }

    private static DateTime WeekMonday(DateTime now)
    {
        int dowFromMon = ((int)now.Date.DayOfWeek + 6) % 7;
        return now.Date.AddDays(-dowFromMon);
    }

    /// <summary>该时刻抽签是否处于生效时段：weekly 整周；weekend 只在周六/周日。</summary>
    public static bool WeeklyDrawActiveNow(DateTime now)
    {
        if (!WeeklyDrawEnabled) return false;
        if (string.Equals(WeeklyDrawMode, "weekend", StringComparison.OrdinalIgnoreCase))
            return now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday;
        return true;
    }

    /// <summary>读 `weeklyDraw` 节点（缺失 ⇒ 全默认 = off，老配置零影响）。</summary>
    private static void LoadWeeklyDraw(JsonElement root, ISptLogger<EventRotatorServer> log)
    {
        // ★ 每次都先复位：热重载时把节点删掉也要回到 off，不能残留上一份
        WeeklyDrawMode = "off";
        WeeklyDrawCount = 1;
        WeeklyDrawOnlyNoRestart = true;
        WeeklyDrawSeed = 0;
        try
        {
            if (!root.TryGetProperty("weeklyDraw", out var w) || w.ValueKind != JsonValueKind.Object) return;
            if (w.TryGetProperty("mode", out var md) && md.ValueKind == JsonValueKind.String)
            {
                var s = (md.GetString() ?? "off").Trim().ToLowerInvariant();
                WeeklyDrawMode = s is "off" or "weekly" or "weekend" ? s : "off";
            }
            if (w.TryGetProperty("count", out var ct) && ct.ValueKind == JsonValueKind.Number)
                WeeklyDrawCount = Math.Clamp(ct.GetInt32(), 1, 3);
            if (w.TryGetProperty("onlyNoRestart", out var onr) && (onr.ValueKind == JsonValueKind.True || onr.ValueKind == JsonValueKind.False))
                WeeklyDrawOnlyNoRestart = onr.GetBoolean();
            if (w.TryGetProperty("seed", out var sd) && sd.ValueKind == JsonValueKind.Number)
                WeeklyDrawSeed = sd.GetInt32();
            if (WeeklyDrawEnabled)
                log?.Info($"[EventRotator] ★{WeeklyDrawTag()}已启用：每 {(WeeklyDrawMode == "weekend" ? "周末（周六~周日）" : "周（周一~周日）")}"
                          + $"抽 {WeeklyDrawCount} 个（只抽免重启={WeeklyDrawOnlyNoRestart}，seed={WeeklyDrawSeed}）");
        }
        catch (Exception ex) { log?.Warning("[EventRotator] weeklyDraw 配置解析失败: " + ex.Message); }
    }

    /// <summary>
    /// **纯函数**：算出"本周/本周末"抽哪几个（不读、不写任何状态）。
    /// 同一 (模式, 周序号, seed) ⇒ 同一结果，与调用时刻在周内哪一天**无关**。
    /// </summary>
    public static List<EventDef> WeeklyDrawPicks(List<EventDef> all, IEnumerable<string> forcedIds, DateTime now)
    {
        var picked = new List<EventDef>();
        try
        {
            if (!WeeklyDrawEnabled || all == null) return picked;
            int n = Math.Clamp(WeeklyDrawCount, 1, 3);
            int week = WeekIndex(now);
            var forced = new HashSet<string>(forcedIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var monday = WeekMonday(now);
            var weekEnd = monday.AddDays(6);
            var mode = string.Equals(WeeklyDrawMode, "weekend", StringComparison.OrdinalIgnoreCase) ? "weekend" : "weekly";

            var cands = new List<EventDef>();
            foreach (var e in all)
            {
                if (e == null || string.IsNullOrEmpty(e.Id)) continue;
                if (forced.Contains(e.Id)) continue;                       // 强制的不抽（会被 force 覆盖）
                if (IsExcluded(e.Id)) continue;                            // rotation.exclude 黑名单
                if (IsSeasonal(e)) continue;                               // 季节活动只走日期窗/季节阶段/手动
                if (WindowOverlapsWeek(e, monday, weekEnd)) continue;      // 日期窗本周已生效（≈"已生效"）
                if (WeeklyDrawOnlyNoRestart
                    && string.Equals(e.UiTag ?? "", "experimental", StringComparison.OrdinalIgnoreCase)) continue;
                cands.Add(e);
            }
            var ordered = cands
                .OrderBy(e => StableHash($"{WeeklyDrawSeed}|{week}|{mode}|{e.Id}"))
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .ToList();
            foreach (var e in ordered)
            {
                if (picked.Count >= n) break;
                picked.Add(e);
            }
        }
        catch (Exception ex) { Warn("[EventRotator][周抽签] 计算失败: " + ex.Message); }
        return picked;
    }

    /// <summary>活动的日期窗是否与 [from, to] 这一周有重叠（无窗 = 不重叠）。</summary>
    private static bool WindowOverlapsWeek(EventDef e, DateTime from, DateTime to)
    {
        if (e.StartMonth <= 0 || e.EndMonth <= 0) return false;
        int s = e.StartMonth * 100 + e.StartDay, t = e.EndMonth * 100 + e.EndDay;
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
        {
            int cur = d.Month * 100 + d.Day;
            if (s <= t ? (cur >= s && cur <= t) : (cur >= s || cur <= t)) return true;
        }
        return false;
    }

    /// <summary>从 `force.txt` 的当前值拿强制 id 集合（与 EventEngine 同一份数据，不另读文件）。</summary>
    private static List<string> ForcedIdsNow()
    {
        var res = new List<string>();
        try
        {
            var raw = EventEngine.ForcedId ?? "";
            foreach (var x in raw.Split(new[] { ',', ';', '|', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var s = x.Trim();
                if (s.Length > 0 && !string.Equals(s, "none", StringComparison.OrdinalIgnoreCase)) res.Add(s);
            }
        }
        catch { }
        return res;
    }

    /// <summary>
    /// **落库**（唯一有副作用的新方法）：把上一轮抽签加进去的活动撤掉，再把"本周"抽中的加进 `actives`。
    /// 幂等：同一周反复调用结果一致；`mode=off` 时只做"撤掉旧抽签"（保证关掉后立刻回到原样，不改别的）。
    /// <para>只在 <c>RestoreThenApply</c> 里调一次（启动 / 每次进图前），不影响 EventEngine 的活动判定。</para>
    /// </summary>
    public static List<EventDef> SyncWeeklyDraw(List<EventDef> actives, DateTime now)
    {
        var added = new List<EventDef>();
        try
        {
            if (actives == null) return added;
            if (!WeeklyDrawEnabled)
            {
                if (_weeklyAdded.Count > 0)
                {
                    foreach (var e in _weeklyAdded) actives.Remove(e);   // 引用相等；Actives 被重建过则已不在
                    _weeklyAdded.Clear();
                }
                return added;
            }

            var picks = WeeklyDrawPicks(EventEngine.All, ForcedIdsNow(), now);
            var target = WeeklyDrawActiveNow(now) ? picks : new List<EventDef>();

            // ① 先判断"要不要动"：目标集合与上一轮完全一致、且对象都还在 actives 里 ⇒ **什么都不做**。
            //    （同一周内每次进图都重复调用；不重排 Actives ⇒ 谁在最后被套用整周都稳定，
            //      否则"后套用者覆盖"的结果会在两次进图之间变化。）
            bool same = target.Count == _weeklyAdded.Count;
            if (same)
                for (int i = 0; i < target.Count; i++)
                    if (!ReferenceEquals(target[i], _weeklyAdded[i]) || !actives.Contains(target[i])) { same = false; break; }
            if (same) return added;

            // ② 撤掉上一轮抽签（避免同周叠 2 份 / 跨周残留；Actives 被 EventEngine 重建过则已不在，Remove 无副作用）
            foreach (var e in _weeklyAdded) actives.Remove(e);
            _weeklyAdded.Clear();
            // ③ 按本时段加（weekend 模式在周一~周五不生效）
            foreach (var e in target)
                if (!actives.Any(x => string.Equals(x.Id, e.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    actives.Add(e);
                    _weeklyAdded.Add(e);
                    added.Add(e);
                }

            // ④ 日志：同一周同一组只打一次（避免每次进图刷屏）；口径用"抽中"（picks），不是"新增"
            int week = WeekIndex(now);
            var tag = WeeklyDrawTag();
            var sig = tag + "|" + week + "|" + string.Join(",", picks.Select(x => x.Id));
            bool shouldLog = picks.Count > 0 || (tag == "周抽签" && _weeklyLoggedWeek != week);
            if (shouldLog && !string.Equals(_weeklyLoggedSig, sig, StringComparison.Ordinal))
            {
                _weeklyLoggedWeek = week;
                _weeklyLoggedSig = sig;
                Info($"[EventRotator][{tag}] {(tag == "周末抽签" ? "本周末" : "本周")}抽中 {picks.Count} 个："
                     + (picks.Count == 0 ? "(无可用候选)" : string.Join(",", picks.Select(x => x.Id)))
                     + (added.Count < picks.Count ? $"（其中 {picks.Count - added.Count} 个本来就在生效集合里）" : "")
                     + (WeeklyDrawOnlyNoRestart ? "（onlyNoRestart=true ⇒ 已排除 experimental）" : ""));
            }
        }
        catch (Exception ex) { Warn("[EventRotator][周抽签] 应用失败: " + ex.Message); }
        return added;
    }

    // ═══════════ ★★★ v0.1.4 轮换组合规则 ═══════════
    /// <summary>
    /// 每次轮换要凑出的组合（用户定义）：
    /// <list type="bullet">
    /// <item>先看**有没有开季节活动**：开了季节活动 → 季节活动 + 奖励活动 1~2 个（不再叠加别的）</item>
    /// <item>没开季节活动（或已经开过）→ 「限时大活动(带任务) 1 个」+「奖励活动 1~2 个」</item>
    /// <item>或者 → 「限时小活动(改敌人) 1~2 个」+「奖励活动 1~2 个」</item>
    /// </list>
    /// <b>保证只开一个带任务的活动</b>；组合之间必须不冲突。
    /// </summary>
    public static int RewardMin = 1;
    public static int RewardMax = 2;
    /// <summary>小活动数量上限（1~2）。</summary>
    public static int SmallMax = 2;

    /// <summary>季节活动（category = seasonal）。</summary>
    public static bool IsSeasonal(EventDef e) =>
        string.Equals(e?.Category, "seasonal", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(e?.SeasonalEvent);

    /// <summary>限时大活动（带任务）。</summary>
    public static bool IsTaskEvent(EventDef e) => string.Equals(e?.Category, "event_task", StringComparison.OrdinalIgnoreCase);

    /// <summary>限时小活动（改敌人 / 整活）。</summary>
    public static bool IsSmallEvent(EventDef e) => string.Equals(e?.Category, "event_small", StringComparison.OrdinalIgnoreCase);

    /// <summary>奖励活动（对玩家有收益加成）。</summary>
    public static bool IsRewardEvent(EventDef e) => string.Equals(e?.Category, "reward", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ★★★ v2.6.39【用户规格】AI 活动（`event_ai`，25 个）。
    /// 用户原话：「**ai活动也在轮换里面**」⇒ 本类必须能参与轮换。
    ///
    /// <para>⚠ **注意 `standalone` / `noquest` 故意不在任何轮换类里** ——
    /// 用户要它们**不被自动勾走**（`standalone` 是"单独活动"，得手动开）。<b>不要把它们加回来</b>。</para>
    ///
    /// <para>⚠ 现实约束：25 个 `event_ai` 里**只有 1 个**（`bosses_home_generic`）没有日期窗
    /// ⇒ 默认候选池（`StartMonth &lt;= 0 &amp;&amp; !IsSeasonal`）里只有它一个 AI
    /// ⇒ **加这一类，实际只多出 1 个可轮换的 AI 活动**（不是 25 个）。
    /// 其余 24 个走**日期窗**开启（或写进 `rotation.pool` 白名单 —— 那条分支会绕过日期窗）。</para>
    /// </summary>
    public static bool IsAiEvent(EventDef e) => string.Equals(e?.Category, "event_ai", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ★ v2.6.42 安全读 `SeasonRotation.IsRotating`（= `config.seasonRotation.mode == "rotate"`）。
    /// **拿不到/异常 ⇒ false**（= "未开启季节轮换"）⇒ 调用方退回**现状**（按现实时间），不会误放开 ✔
    /// </summary>
    private static bool IsRotatingSafe()
    {
        try { return SeasonRotation.IsRotating; } catch { return false; }
    }

    /// <summary>
    /// ★★★ 2026-10-24【轮换候选池 —— **单一真源**，`rotation.pool` 为空时用】
    ///
    /// <para>规则（与 `PickCore` 里原来的内联写法**逐条等价**，只是抽出来便于自检/探针复跑）：</para>
    /// <list type="number">
    /// <item>`!IsSeasonal(e)` —— **季节活动永不进池**（季节活动只走日期窗/季节阶段/手动三条路）。</item>
    /// <item>`e.StartMonth &lt;= 0` —— 无日期窗 = 常驻 ⇒ 进池（旧行为，占候选的绝大多数）。</item>
    /// <item>`!taskFollowsRealTime &amp;&amp; IsTaskEvent(e)` —— v2.6.42【用户规格 ②】：
    ///    `seasonRotation.mode == "rotate"`（= 实机现状）时，**带任务的活动不看日期窗**，一律进池。
    ///    `taskFollowsRealTime = !SeasonRotation.IsRotating`（拿不到季节模式 ⇒ true ⇒ 退回旧行为，不会误放开）。</item>
    /// <item>`e.AlwaysInRotation` —— 2026-10-24 新增：**显式标注"有窗也要进轮换"**的活动。
    ///    `event_ai` 没有第 3 条那种整体豁免（否则 22 个带窗 AI 会一起进池、把老 AI 活动稀释掉），
    ///    所以 `meeting_of_the_bosses_2026`（event_ai，3/12~3/19）走这一条。当前生效的只有 4 个新活动
    ///    （其中 3 个 `event_task` 本来就已经由第 3 条进池，标它只是显式化 + 防以后改窗）。</item>
    /// </list>
    ///
    /// <para>⚠ **`rotation.pool` 非空时不走这里**（白名单优先，`PickCore` 里另一个分支）。</para>
    /// <para>确定性：按 Id 排序输出（`Ordinal`），同一份 events.json + 同一档位结果可复现。</para>
    /// </summary>
    public static List<EventDef> CandidatePool(List<EventDef> all)
    {
        var list = new List<EventDef>();
        try
        {
            if (all == null) return list;
            bool taskFollowsRealTime = !IsRotatingSafe();
            foreach (var e in all)
            {
                if (e == null) continue;
                if (IsSeasonal(e)) continue;                                  // ① 季节活动永不进池
                if (e.StartMonth <= 0) { list.Add(e); continue; }              // ② 无日期窗 = 常驻
                if (!taskFollowsRealTime && IsTaskEvent(e)) { list.Add(e); continue; }   // ③ rotate 时带任务豁免
                // ★ v1.5.0【B2 修】小活动(16 个 event_small)以前全被"有日期窗"挡在池外 ⇒
                //   smalls 桶恒空 ⇒「小活动方案」和 smallCountMax 是死代码，轮换永远只在 带任务/AI 之间二选一。
                //   现在 rotate 时小活动与带任务同等豁免（同样由轮换决定开不开）。
                if (!taskFollowsRealTime && IsSmallEvent(e)) { list.Add(e); continue; }
                // ★ v1.6.0【R2】AI 活动同样豁免：v1.5.1 起 rotate 模式跳过日期窗，
                //   而候选池原本只给 task/small 豁免 ⇒ 24 个 AI 里 20 个**永久进不来**（只能手写 force.txt）。
                if (!taskFollowsRealTime && IsAiEvent(e)) { list.Add(e); continue; }
                if (e.AlwaysInRotation) list.Add(e);                           // ④ 显式标注（有窗也进）
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        }
        catch (Exception ex) { Warn("[EventRotator] 候选池计算失败: " + ex.Message); }
        return list;
    }

    /// <summary>
    /// 今天该开哪些活动。already = 已经确定生效的（日期窗 / 季节活动 / 季节阶段自动开的），参与冲突判定。
    /// ★ v0.1.4 按用户定义的组合规则选，而不是"随便凑 N 个"。
    /// </summary>
    public static List<EventDef> PickForToday(List<EventDef> all, List<EventDef> already)
        => PickCore(CurrentSlot(), all, already, true);

    /// <summary>
    /// ★ v0.1.4 抽出核心：按**指定档位**算组合（PickForToday 与 PreviewSlot 共用，
    /// 保证"面板上显示的下一档活动"与真正换档后开的活动**完全一致**）。
    /// </summary>
    public static List<EventDef> PickCore(int slot, List<EventDef> all, List<EventDef> already, bool verbose)
    {
        // ★ v0.1.5 轮换同样排除往年变体（seasonAutoPick=all 时恢复旧行为）
        // ★ v1.3.9【D3 修】候选池也改成"只排除**过期**的往年场次"：
        //   以前 `YearVariant == true` 一刀切 ⇒ `night_of_the_cult`（2024，同族最新）永远进不了轮换池，
        //   它自己的官方窗口也开不出来。现在同族最新年份的场次可以正常参与轮换。
        if (!string.Equals(EventEngine.SeasonAutoPick, "all", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = all.ToList();
            all = all.Where(x => !EventEngine.IsOutdatedYearVariant(x, snapshot)).ToList();
        }
        var picked = new List<EventDef>();
        // ★ 日志用委托 + 直接访问 static 字段，避免 JIT 去解析 SPT 的日志类型
        //   （干跑/离线自检环境没有 Microsoft.Extensions.Logging 程序集，
        //    直接调用 Info()/Warn() 会在 JIT 阶段抛 FileNotFoundException —— 实测踩到）
        var lg = EventEngine.Log;
        void LInfo(string m) { if (verbose && lg != null) { try { lg.Info(m); } catch { } } }
        void LWarn(string m) { if (verbose && lg != null) { try { lg.Warning(m); } catch { } } }
        try
        {
            if (!Enabled) return picked;

            // ★ 严格"季节活动之间"：当季已有季节活动生效时完全不轮换（onlyWhenNoSeasonal=true，默认）
            // ★★★ v1.4.0【P1-4 方案 B · 用户裁决】语义修正：
            //   键名叫「仅限季节间隙」，注释也写"严格只在没有季节活动时轮换"，
            //   但旧实现是 `already.Count > 0` ⇒ **只要有任何一个日期窗活动就不轮换**（全年 64% 的天被挡）。
            //   ⇒ 改成只在"已有**季节**活动在跑"时暂停（这才是字面意思，也是用户选的 B）。
            // ★ v1.6.0【R5】`rotateAiAndTaskDuringSeasonal=true` 时不该被早退吃掉
            //   （否则"季节里叠加 1 个 AI/任务"这个开关在默认配置下永不生效）。
            if (OnlyWhenNoSeasonal && !RotateAiAndTaskDuringSeasonal && already.Any(IsSeasonal))
                return picked;

            IEnumerable<EventDef> candidates;
            // ★ v2.6.42【用户规格 ②】"带任务按现实时间开"的前提 = **季节轮换未开启**（方法级，下面日志也要用）
            bool taskFollowsRealTime = !IsRotatingSafe();
            if (Pool.Count > 0)
            {
                var want = new HashSet<string>(Pool, StringComparer.OrdinalIgnoreCase);
                var before = all.Where(e => want.Contains(e.Id)).ToList();
                // ★ v1.3.8【P1-8 修】白名单以前绕过"季节活动永不进池"：
                //   `pool:["halloween"]` ⇒ 四桶全空 ⇒ 兜底直接开 halloween（任意日期强开季节活动）。
                candidates = before.Where(e => !IsSeasonal(e));
                var dropped = before.Where(IsSeasonal).Select(e => e.Id).ToList();
                if (dropped.Count > 0)
                    LWarn($"[EventRotator][轮换池] 白名单里的季节活动被忽略（季节活动只由日期窗/季节阶段开）：{string.Join(",", dropped)}");
            }
            else
            {
                // 无日期窗 = 填充活动；排除季节活动（季节活动由日期窗/季节阶段/手动单独管）
                //
                // ★★★ v2.6.42【用户规格 ②】「按现实时间开」的**前提 = 季节轮换【未开启】**
                //   <para>用户原话：「带任务的活动保留了按现实时间开启,但是这个按现实时间开启都有一个前提,
                //   就是**未开启季节轮换选项**」</para>
                //   <list type="bullet">
                //   <item><b>未开启</b>（`seasonRotation.mode != "rotate"`，用户当前 = `fixed`）⇒
                //         带任务的**按现实时间（日期窗）** ⇒ 判据仍是 `StartMonth &lt;= 0` ⇒ **与改前逐字一致** ✔</item>
                //   <item><b>已开启</b>（`mode == "rotate"`）⇒ 带任务的**不按现实时间** ⇒
                //         **不管日期窗都进轮换池**，由轮换决定开不开 ✔
                //         （这正是"带任务档全年 0 档"的解：15 个 `event_task` 全有日期窗，
                //           旧判据把它们全挡在池外 ⇒ 那一档永远选不到）</item>
                //   </list>
                //   ⚠ 可降级：拿不到季节模式 ⇒ `IsRotatingSafe()` 返回 false ⇒ `taskFollowsRealTime = true`
                //     ⇒ **按现状（日期窗）** ⇒ 不会误放开 ✔
                // ★★★ 2026-10-24【用户要求：新做的 4 个活动进轮换】规则抽成**单一真源** `CandidatePool()`：
                //   无日期窗 ∪（rotate 时的 event_task）∪（显式 alwaysInRotation 的活动）—— 见该方法注释。
                candidates = CandidatePool(all);
            }
            var pool = candidates.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();

            // ★ v1.3.8【轮换排除】用户排除掉的活动不参与自动轮换（force.txt 手动强制不受影响）
            if (Exclude.Count > 0)
            {
                var beforeCount = pool.Count;
                var dropped = pool.Where(e => IsExcluded(e.Id)).Select(e => e.Id).ToList();
                if (dropped.Count > 0)
                {
                    pool = pool.Where(e => !IsExcluded(e.Id)).ToList();
                    LInfo($"[EventRotator][轮换排除] 已跳过 {dropped.Count} 个被排除的活动：{string.Join(",", dropped)}（池 {beforeCount} → {pool.Count}）");
                }
            }
            if (pool.Count == 0) return picked;

            // slot 由参数传入（PickForToday 传当前档位，PreviewSlot 传指定档位）

            // 确定性排序：random 用 (seed,slot) 洗牌；sequential 按档位推进
            if (string.Equals(Mode, "random", StringComparison.OrdinalIgnoreCase))
            {
                // ★★★ v1.0.1【轮换稳定性 P0 修复】
                //   旧写法 `Seed != 0 ? Seed : Environment.TickCount` 是个**危险默认值**：
                //   `Environment.TickCount` = 系统开机毫秒数 ⇒ **每次调用都不同**
                //   ⇒ seed=0（config 默认值 / 用户没设）时，同一个 7 天档位
                //     每次重启服务端都会算出完全不同的活动顺序 ⇒
                //     玩家实测「之前玩是一个活动，刚才上号又是另一个活动了」。
                //   而奖励轮换的 fallback 是"天"（稳定）⇒ 两者行为不一致，
                //     玩家看到的就是「主活动乱跳、奖励活动正常」。
                //   ⇒ 改成从 slot 派生的**稳定**值：同一个档位内永远同一组活动，
                //     换档才变。`2654435761` 是 Knuth 乘数（只是让相邻档位分布散开）。
                int useSeed = Seed != 0 ? Seed : unchecked(slot * 1103515245 + 12345);
                pool = pool.OrderBy(e => StableHash(useSeed + ":" + slot + ":" + e.Id)).ToList();
            }
            else
            {
                int start = ((slot % pool.Count) + pool.Count) % pool.Count;
                pool = Enumerable.Range(0, pool.Count).Select(i => pool[(start + i) % pool.Count]).ToList();
            }

            // 候选分成四桶（★ v2.6.39：新增 `ais` —— 用户规格「ai活动也在轮换里面」）
            var tasks = pool.Where(IsTaskEvent).ToList();
            var smalls = pool.Where(IsSmallEvent).ToList();
            var rewards = pool.Where(IsRewardEvent).ToList();
            var ais = pool.Where(IsAiEvent).ToList();

            // ★★★ v1.3.8【P0-1/P0-2 修】⑤AI×带任务硬互斥、⑥带任务 ≤1，
            //   以前只靠"选方案前的两道闸"，而闸只看对面那一类、兜底路径完全没守卫 ⇒
            //   一旦 onlyWhenNoSeasonal=false（文档支持的模式）就会同时开「AI + 带任务」或「2 个带任务」。
            //   现在把这两条做成 ok() 的**硬约束**（already 已生效的也算），所有取用点自动受保护。
            bool ok(EventDef e)
            {
                if (e == null) return false;
                // ★ v1.5.0【B4 修】`avoidConflicts=false` 只该放过"与已在跑的活动"的冲突，
                //   **本次产出内部**的冲突必须始终检查（否则同批会开两个改同一批 boss 刷点的活动，后写覆盖）。
                if (picked.Any(p => Conflicts(p, e))) return false;
                // ★ v1.6.0【R4】恢复开关语义：`avoidConflicts=false` 只放过"与已在跑的活动"的冲突，
                //   本批产出内部仍必须互斥（否则两个活动改同一批刷点会互相覆盖）。
                if (AvoidConflicts && already.Any(a => Conflicts(a, e))) return false;
                if (picked.Contains(e)) return false;

                bool taskOn = already.Any(IsTaskEvent) || picked.Any(IsTaskEvent);
                bool aiOn = already.Any(IsAiEvent) || picked.Any(IsAiEvent);
                if (IsTaskEvent(e) && aiOn) return false;                 // ⑤ 已有 AI ⇒ 不再加带任务
                if (IsAiEvent(e) && taskOn) return false;                 // ⑤ 已有带任务 ⇒ 不再加 AI
                if (IsTaskEvent(e) && taskOn) return false;               // ⑥ 带任务 ≤ 1（含已生效）
                return true;
            }

            void TakeRewards(int n)
            {
                foreach (var r in rewards)
                {
                    if (picked.Count(x => IsRewardEvent(x)) >= n) break;
                    if (ok(r)) picked.Add(r);
                }
            }

            // ═══════════════════════════════════════════════════════════════════════════
            // ★★★ v2.6.42【用户规格 ④⑤⑥】轮换产出与**硬互斥**规则
            //
            //  ④ 产出 = **1~2 个「活动」 + 1~2 个「奖励活动」**
            //  ⑤ **硬互斥**：绝不能出现「AI 活动 + 限时带任务的活动」；只允许「AI+奖励」或「带任务+奖励」
            //  ⑥ **带任务的活动 ≤ 1**（用户原话：「带限时任务的活动轮换到了之后**只开启一个**带任务的」）
            //
            //  ★ 关于 `event_small`（小活动）：用户那条"补充规则"针对的是 **AI vs 带任务** 这一维
            //    （原话「**补充规则是**不能出现ai活动+限时带任务的活动」）⇒ **小活动仍算一种「活动」**、
            //    不与 AI 互斥。若把小活动也排掉，池子里就只剩 **1 个** AI 活动（`bosses_home_generic`）
            //    ⇒ 轮换会永远是「它 + 奖励」，极重复，且会废掉现在能轮换的 3 个活动
            //    （`unlocked_traders` / `labs_free_generic` / `all_bosses_shoreline`）。
            //    ⇒ **本实现保留小活动**；若要严格只有两种形状，删掉 `plans.Add("small")` 一行即可。
            // ═══════════════════════════════════════════════════════════════════════════
            bool AnyAiActive() => already.Any(IsAiEvent) || picked.Any(IsAiEvent);
            bool AnyTaskActive() => already.Any(IsTaskEvent) || picked.Any(IsTaskEvent);

            bool seasonalOn = already.Any(IsSeasonal);
            bool taskOn = already.Any(IsTaskEvent);
            if (seasonalOn || taskOn)
            {
                // ★★★ v2.6.39【用户规格·开关】大季节活动开启时，开关打开 ⇒ 先挑 1 个**不冲突的** AI/带任务活动。
                //   ⚠ 默认 false ⇒ 这段整段不执行 ⇒ 与改前**逐字一致**（只叠奖励）。
                //   ★ 用 `ok()` 保证冲突规则照旧生效；★ 加上 ⑤⑥ 的硬约束。
                if (RotateAiAndTaskDuringSeasonal && seasonalOn)
                {
                    // ⑤ 有带任务的在 ⇒ 不许加 AI；有 AI 在 ⇒ 不许加带任务
                    var extra = (!AnyTaskActive() ? ais.FirstOrDefault(ok) : null)
                             ?? (!AnyAiActive() ? tasks.FirstOrDefault(ok) : null);
                    if (extra != null)
                    {
                        picked.Add(extra);
                        LInfo($"[EventRotator] 轮换[{Mode}] 档位#{slot}：本季已有季节活动，"
                              + $"但开关 `rotateAiAndTaskDuringSeasonal` 已开 ⇒ 先挑 1 个**不冲突的** "
                              + $"{(IsAiEvent(extra) ? "AI" : "带任务")}活动『{extra.Id}』（冲突检查已过；⑤ 互斥已守）");
                    }
                    else LInfo($"[EventRotator] 轮换[{Mode}] 档位#{slot}：开关已开，但没有可挑的"
                             + $"（AI 候选 {ais.Count} / 带任务候选 {tasks.Count}；⑤ AI×带任务互斥、冲突检查后无可选）⇒ 只叠奖励");
                }
                TakeRewards(RewardMax);
                if (picked.Count > 0)
                    LInfo($"[EventRotator] 轮换[{Mode}] 档位#{slot}："
                          + (seasonalOn ? "本季已有季节活动" : "已有带任务的活动")
                          + " → 只叠加奖励活动 "
                          + $"{string.Join(" + ", picked.Select(p => p.Id))}（带任务/小活动不参与）");
            }
            else
            {
                // ★★★ ⚠ 本节**刻意保留批2（v2.6.39）的档位映射**，只**追加** ⑤⑥ 硬约束 ——
                //   因为"`fixed` 下行为一行不改"是硬要求。批2 的映射是：
                //     AI 方案 ⇒ `slot % 3 == 2`；带任务方案 ⇒ `slot % 2 == 0`（且未走 AI）；其余 ⇒ 小活动
                //   （实测：若改成 `plans[slot % plans.Count]`，AI/小活动的档位比例会从 1/3 : 2/3 变 1/2 : 1/2
                //     ⇒ **改变 `fixed` 下的既有产出** ✗ 故回退成批2映射。）
                // ⑤ 在**选方案时**就把互斥排掉：有 AI 在 ⇒ 不放带任务方案；有带任务在 ⇒ 不放 AI 方案
                // ★ v1.3.8【P0-2 修】原 `wantTaskPlan0 = !AnyAiActive()` 从不检查"已有带任务"
                //   ⇒ 已有 task 时偶数档还会再挑一个 task。这里把"已有带任务"也纳入闸。
                bool wantTaskPlan0 = !AnyAiActive() && !AnyTaskActive();
                bool wantAiPlan0 = !AnyTaskActive();

                // ★★★ v2.6.50【🚨 修「smalls 桶空 ⇒ AI 霸占全部档位、带任务方案永不可达」2026-10-16】
                //   【bug 实证】自检 `rotate 下「带任务档」可达` 报：**带任务档 0 / AI 档 365 / 小活动档 0** ✗
                //     `(smalls.Count == 0 || slot % N == …)` 这种写法**在桶空时退化成恒真** ⇒
                //       `wantAiPlan` 对**每一档**都为 true ⇒ `useTaskPlan` 第一道就是 `!wantAiPlan` ⇒ **恒 false**
                //       ⇒ **用户规格②（"带任务的活动轮换了之后只开启一个带任务的"）被彻底废掉** ✗✗
                //     【触发链】无日期窗的 `event_small` 归零（分类修正把 `unlocked_traders`/`all_bosses_shoreline`/
                //       `labs_free_generic` 分别改成 reward/ai/reward）⇒ `smalls==0` ⇒ 短路恒真 ⇒ 首档即 AI ✗
                //     【同时的隐性伤害】`fixed` 下 `smalls` 也是 0 ⇒ 同样"AI 档 365" ⇒ 轮换永远
                //       `bosses_home_generic + 奖励`，**极其重复** ✗
                //   【修法】**按"可用计划集合"确定性轮转**，**不再把"桶空不空"混进档位信号** ✗
                //     顺序固定 task → ai → small（同一 slot 永远同结果 ⇒ **可复现**）✔
                //     ⚠ 硬约束 ⑤⑥ 照旧由 `wantTaskPlan0`/`wantAiPlan0` 两道闸 + 下方 `else` 兜底守住 ✔
                var availPlans = new List<int>();
                if (tasks.Count > 0) availPlans.Add(0);      // 0 = 带任务
                if (ais.Count > 0) availPlans.Add(1);        // 1 = AI
                if (smalls.Count > 0) availPlans.Add(2);     // 2 = 小活动
                int want = availPlans.Count > 0
                    ? availPlans[((slot % availPlans.Count) + availPlans.Count) % availPlans.Count]
                    : 2;                                     // 三个桶全空 ⇒ 走 else（最后由 :399 兜底）
                bool wantAiPlan = wantAiPlan0 && want == 1;
                bool useTaskPlan = wantTaskPlan0 && !wantAiPlan && want == 0;

                if (wantAiPlan)
                {
                    foreach (var a in ais)                                        // ④ 活动数 = rotation.count
                    {
                        if (picked.Count >= Math.Max(1, Count)) break;
                        if (ok(a)) picked.Add(a);
                    }
                    if (picked.Any(IsAiEvent))
                        LInfo($"[EventRotator] 轮换[{Mode}] 档位#{slot}：走 **AI 方案** ⇒ "
                              + $"{string.Join(" + ", picked.Where(IsAiEvent).Select(p => p.Id))} + 奖励活动（AI 候选 {ais.Count} 个）");
                    TakeRewards(RewardMax);
                    if (!picked.Any(IsAiEvent))                                   // AI 全冲突 ⇒ 退回带任务/小活动
                    {
                        var t2 = tasks.FirstOrDefault(ok);                        // ⑥ 只取 1 个
                        if (t2 != null)
                        {
                            picked.Add(t2);
                            LInfo($"[EventRotator] 轮换[{Mode}] 档位#{slot}：AI 方案无可选 ⇒ 退回带任务『{t2.Id}』×1（⑥ 只开 1 个）");
                        }
                        else foreach (var s2 in smalls)
                        {
                            if (picked.Count(IsSmallEvent) >= Math.Max(1, Math.Min(Count, SmallMax))) break;   // ★ v1.6.0【R1】小活动也受 count 约束
                            if (ok(s2)) picked.Add(s2);
                        }
                        TakeRewards(RewardMax);
                    }
                }
                else if (useTaskPlan)
                {
                    var t = tasks.FirstOrDefault(ok);                             // ⑥ 只取 1 个
                    if (t != null)
                    {
                        picked.Add(t);
                        LInfo($"[EventRotator] 轮换[{Mode}] 档位#{slot}：走 **带任务方案** ⇒ 限时带任务活动『{t.Id}』×1"
                              + $"（⑥ 只开 1 个）+ 奖励活动（候选 {tasks.Count} 个；"
                              + (taskFollowsRealTime ? "未开季节轮换 ⇒ 只从无日期窗里挑" : "已开季节轮换 ⇒ 不看日期窗") + "）");
                    }
                    TakeRewards(RewardMax);
                    if (t == null)                                                // 带任务全冲突 ⇒ 退回小活动
                    {
                        foreach (var s in smalls)
                        {
                            if (picked.Count(IsSmallEvent) >= Math.Max(1, Math.Min(Count, SmallMax))) break;   // ★ v1.6.0【R1】小活动也受 count 约束
                            if (ok(s)) picked.Add(s);
                        }
                        TakeRewards(RewardMax);
                    }
                }
                else
                {
                    foreach (var s in smalls)
                    {
                        if (picked.Count(IsSmallEvent) >= Math.Max(1, Math.Min(Count, SmallMax))) break;   // ★ v1.6.0【R1】小活动也受 count 约束
                        if (ok(s)) picked.Add(s);
                    }
                    // 小活动一个都挑不出来 → 至少给一个带任务的（⑤⑥ 仍守）
                    if (!picked.Any(IsSmallEvent) && !AnyAiActive())
                    {
                        var t = tasks.FirstOrDefault(ok);
                        if (t != null) picked.Add(t);
                    }
                    TakeRewards(RewardMax);
                }

                // 兜底：什么都没凑出来（池子太小/全冲突）→ 随便补一个能用的
                if (picked.Count == 0)
                {
                    var any = pool.FirstOrDefault(ok);
                    if (any != null) picked.Add(any);
                    TakeRewards(RewardMax);
                }
            }

            // ── ⑤⑥ 最后一道闸（defense in depth）：万一上面的分支漏了，这里强拆 ──
            if (picked.Any(IsAiEvent) && picked.Any(IsTaskEvent))
            {
                int dropped = picked.RemoveAll(IsTaskEvent);
                LWarn($"[EventRotator] ⚠自检：档位#{slot} 同时出现 AI 与带任务活动 ⇒ **已强拆**（丢掉 {dropped} 个带任务的）"
                    + "（用户规格 ⑤：不能出现 ai活动+限时带任务的活动）");
            }
            if (picked.Count(IsTaskEvent) > 1)
            {
                var keep = picked.First(IsTaskEvent);
                picked.RemoveAll(x => IsTaskEvent(x) && !ReferenceEquals(x, keep));
                LWarn($"[EventRotator] ⚠自检：档位#{slot} 出现 >1 个带任务活动 ⇒ **只保留 '{keep.Id}'**"
                    + "（用户规格 ⑥：带任务轮换到了只开启一个）");
            }

            // ★ 日志委托已在方法开头定义（LInfo / LWarn）
            if (picked.Count > 0)
            {
                int nt = picked.Count(IsTaskEvent), ns = picked.Count(IsSmallEvent), nr = picked.Count(IsRewardEvent), nx = picked.Count(IsSeasonal);
                LInfo($"[EventRotator] 轮换[{Mode}] 档位#{slot}（每 {IntervalDays} 天换一档）→ "
                      + $"{string.Join(" + ", picked.Select(p => p.Id))}"
                      + $"　[组合：带任务 {nt} / 小活动 {ns} / 奖励 {nr}" + (nx > 0 ? $" / 季节 {nx}" : "") + "]");
                // 自检：只允许一个带任务的活动
                if (nt > 1) LWarn($"[EventRotator] ⚠ 自检失败：轮换同时选了 {nt} 个带任务的活动（规则要求最多 1 个）");
            }
        }
        catch (Exception ex) { Warn("[EventRotator] 轮换选取失败: " + ex.Message); }
        return picked;
    }

    /// <summary>
    /// ★ v0.1.3 换档时间基准（小时，0~23）。
    /// <para>
    /// 默认 0（= 午夜换档，与旧行为一致）。**建议设成 4**：
    /// 因为档位是按"日期"算的，午夜换档会让"晚上玩到 22:00、第二天早上再玩"的人
    /// 跨过一次换档；把基准挪到凌晨 4:00，玩家在**任何一个连续游玩时段内**都不会被换档打断，
    /// 而且服务端下次启动时会**预先**按新档位算好（进游戏即是新活动）。
    /// </para>
    /// </summary>
    public static int SlotRebaseHour;

    /// <summary>当前档位序号（从 2020-01-01 起算，保证跨重启稳定、可复现）。</summary>
    /// <summary>★ v0.1.4 是否启用了自动轮换（供"随机地图"种子判断用）。</summary>
    public static bool SlotRebaseEnabled => Enabled;

    /// <summary>当前档位序号（从 2020-01-01 起算，保证跨重启稳定、可复现）。</summary>
    public static int CurrentSlot() => SlotAt(DateTime.Now);

    /// <summary>指定时刻属于哪个档位（把 SlotRebaseHour 之后的时刻算作"同一天"）。</summary>
    public static int SlotAt(DateTime now)
    {
        try
        {
            var shifted = now.AddHours(-Math.Clamp(SlotRebaseHour, 0, 23));
            var epoch = new DateTime(2020, 1, 1, Math.Clamp(SlotRebaseHour, 0, 23), 0, 0);
            var days = (shifted.Date - epoch.Date).TotalDays;
            return (int)Math.Floor(days / Math.Max(1, IntervalDays));
        }
        catch { return 0; }
    }

    /// <summary>当前档位开始的时刻（用于 UI 显示"本档从什么时候开始"）。</summary>
    public static DateTime SlotStart(int slot)
    {
        var epoch = new DateTime(2020, 1, 1, Math.Clamp(SlotRebaseHour, 0, 23), 0, 0);
        return epoch.AddDays((double)slot * Math.Max(1, IntervalDays));
    }

    /// <summary>下一档开始的时刻（客户端/UI 用来显示"下次换档倒计时"）。</summary>
    public static DateTime NextSlotStart() => SlotStart(CurrentSlot() + 1);

    /// <summary>
    /// ★ v0.1.4 预计算：某个档位会轮换出哪些活动（**不改任何状态**）。
    /// 现在**共用 PickCore**，所以"面板上显示的下一档活动"与真正换档后开出来的活动**完全一致**
    /// （v0.1.3 之前是两套算法，会漂移）。
    /// </summary>
    public static List<string> PreviewSlot(int slot, List<EventDef> all, List<EventDef> seasonalActive)
    {
        var ids = new List<string>();
        try
        {
            if (!Enabled || all == null || all.Count == 0) return ids;
            var picked = PickCore(slot, all, seasonalActive ?? new List<EventDef>(), false);
            foreach (var e in picked) ids.Add(e.Id);
        }
        catch { }
        return ids;
    }

    // ─────────────────── 手动强制的持续天数（durationDays）───────────────────

    private static string StatePath()
    {
        try { return System.IO.Path.Combine(EventEngine.ModPath ?? "", "rotation_state.json"); }
        catch { return null; }
    }

    /// <summary>
    /// 检查手动强制是否**已到期**。
    /// 返回 true = 该 force.txt 已过期，调用方应把它当作"无强制"处理（活动结束）。
    ///
    /// 状态文件 rotation_state.json：{ "forcedId": "...", "startedAt": "2026-10-14T11:00:00" }
    /// force.txt 里的 id 变了 → 重新计时。
    /// </summary>
    public static bool ForcedExpired(string forcedId)
    {
        try
        {
            if (DurationDays <= 0) return false;               // 0 = 不限制
            if (string.IsNullOrEmpty(forcedId)) return false;
            var path = StatePath();
            if (string.IsNullOrEmpty(path)) return false;

            string savedId = null;
            DateTime started = DateTime.MinValue;
            if (System.IO.File.Exists(path))
            {
                var txt = System.IO.File.ReadAllText(path);
                using var doc = JsonDocument.Parse(txt);
                if (doc.RootElement.TryGetProperty("forcedId", out var fi)) savedId = fi.GetString();
                if (doc.RootElement.TryGetProperty("startedAt", out var sa) &&
                    DateTime.TryParse(sa.GetString(), out var dt)) started = dt;
            }

            // id 变了（或首次见）→ 重新计时
            if (!string.Equals(savedId, forcedId, StringComparison.OrdinalIgnoreCase) || started == DateTime.MinValue)
            {
                var obj = new Dictionary<string, object>
                {
                    ["forcedId"] = forcedId,
                    ["startedAt"] = DateTime.Now.ToString("o"),
                    ["durationDays"] = DurationDays,
                };
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(obj));
                EventEngine.Log?.Warning($"[EventRotator] 手动强制 '{forcedId}' 开始计时：{DurationDays} 天后自动结束");
                return false;
            }

            var elapsed = (DateTime.Now - started).TotalDays;
            if (elapsed >= DurationDays)
            {
                EventEngine.Log?.Warning($"[EventRotator] ★手动强制 '{forcedId}' 已到期（{elapsed:0.#} / {DurationDays} 天）→ 活动结束");
                return true;
            }
            return false;
        }
        catch (Exception ex) { Warn("[EventRotator] durationDays 检查失败: " + ex.Message); return false; }
    }

    /// <summary>稳定哈希（不用 string.GetHashCode —— 它在不同进程间不保证一致）。</summary>
    private static long StableHash(string s)
    {
        unchecked
        {
            long h = 1469598103934665603L;
            foreach (var c in s) { h ^= c; h *= 1099511628211L; }
            return h & 0x7FFFFFFFFFFFFFFFL;
        }
    }

    /// <summary>(地图, 首领) 触碰集合 —— 该活动会改到哪些图的哪些首领（对外暴露给冲突提示用）。</summary>
    public static HashSet<string> TouchSetPublic(EventDef e) => TouchSet(e);

    private static readonly Dictionary<string, string> MapZhMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bigmap"] = "海关", ["woods"] = "森林", ["shoreline"] = "海岸线", ["interchange"] = "立交桥",
        ["lighthouse"] = "灯塔", ["rezervbase"] = "储备站", ["laboratory"] = "实验室", ["labyrinth"] = "迷宫",
        ["tarkovstreets"] = "街区", ["sandbox"] = "零号地", ["sandbox_high"] = "零号地(高)",
        ["factory4_day"] = "工厂(白天)", ["factory4_night"] = "工厂(夜晚)", ["town"] = "小镇",
        ["terminal"] = "终端", ["suburbs"] = "郊区",
    };

    private static readonly Dictionary<string, string> BossZhMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bosskilla"] = "Killa", ["bossbully"] = "雷什拉", ["bossgluhar"] = "格鲁哈",
        ["bosssanitar"] = "萨尼塔", ["bosskojaniy"] = "森林萨尼塔", ["bosstagilla"] = "大锤",
        ["bossknight"] = "骑士", ["bossbigpipe"] = "大管", ["bossbirdeye"] = "鸟眼",
        ["bosszryachiy"] = "扎里亚奇", ["bossboar"] = "野猪", ["bosskolontay"] = "科隆泰",
        ["sectantpriest"] = "邪教祭司", ["sectantwarrior"] = "邪教战士",
        ["bosspartisan"] = "游击队员", ["bosspartisan"] = "游击队员",
        ["followerbully"] = "雷什拉小弟", ["followergluharassault"] = "格鲁哈突击手",
        ["followergluharsecurity"] = "格鲁哈护卫", ["followergluharscout"] = "格鲁哈侦察兵",
        ["followerkojaniy"] = "萨尼塔小弟", ["followersanitar"] = "萨尼塔小弟",
        ["followerzryachiy"] = "扎里亚奇小弟", ["followerbigpipe"] = "大管",
        ["followerbirdeye"] = "鸟眼", ["bossrogue"] = "Rogue", ["rogue"] = "Rogue",
    };

    /// <summary>地图键 → 中文名（冲突提示用）。</summary>
    public static string MapZh(string key)
    {
        if (string.IsNullOrEmpty(key)) return "?";
        return MapZhMap.TryGetValue(key.Trim(), out var v) ? v : key;
    }

    /// <summary>首领角色名 → 中文名（冲突提示用）。</summary>
    public static string BossZh(string key)
    {
        if (string.IsNullOrEmpty(key)) return "?";
        var k = key.Trim().ToLowerInvariant().Replace("_", "");
        if (BossZhMap.TryGetValue(k, out var v)) return v;
        if (k.StartsWith("*") || k == "any") return "任意首领";
        return key;
    }

    // ───────────────────────── 冲突判定 ─────────────────────────

    /// <summary>
    /// 两个活动是否互斥：**动过同一张图的同一个首领**即算冲突。
    /// 判据完全从 events.json 的字段算出来（与 tools/analyze_conflicts.py 同一套算法），
    /// 所以改配置/加活动都自动生效，不需要维护任何硬编码组名。
    /// </summary>
    public static bool Conflicts(EventDef a, EventDef b)
    {
        try
        {
            if (a == null || b == null) return false;
            // ★ v1.3.8【P1-7 修】同 Id 判断必须在 ReferenceEquals 之前：
            //   以前"同一个对象"直接被当成"不冲突" ⇒ ok() 会把已经在跑的活动当新产出，
            //   调用方再用 Contains 丢掉 ⇒ 该档白转（0 新增）。
            if (string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)) return true;   // ★ v1.6.0【R10】后面的 ReferenceEquals 分支不可达，已删除

            // ★ 2026-10-31 显式互斥表（邪教之夜 ⇄ 僵尸万圣节 这类"刷的东西不同但语义冲突"）
            if (EventEngine.MutuallyExcluded(a, b)) return true;

            // 两个活动都声明了季节首领 → 互斥（同一时刻只能一个季节）
            if (!string.IsNullOrEmpty(a.ForceSeasonalBossKey) && !string.IsNullOrEmpty(b.ForceSeasonalBossKey)
                && !string.Equals(a.ForceSeasonalBossKey, b.ForceSeasonalBossKey, StringComparison.OrdinalIgnoreCase))
                return true;

            // ★★★ v2.6.59【🚨 用户要求 2026-10-16】跳蚤等级门槛**硬互斥**
            //   【用户原话】「**我测试的时候这样开活动没问题,轮换的时候可别出现什么跳蚤免手续费和跳蚤关闭一起开**」
            //   【为什么必须互斥】`closed_flea`（关闭跳蚤）把 `fleaMinLevel` 拉到 **99**（买不了）、
            //     `flea_promotion`（跳蚤促销）把它降到 **1**（任意等级可访问）—— **两者语义相反**，
            //     同时开会互相覆盖（数值层已在 `ApplyFlea` 改成"取更严格值"，但那只是**兜底**；
            //     用户要求**轮换层就不要把它们排到同一档**）✗
            //   【判据】**只要两个活动都声明了 `FleaMinLevel` ⇒ 判为互斥** ✔
            //     ⇒ 通用（不止这两个：将来任何两个改 `fleaMinLevel` 的活动都不再同档）✔
            //   ⚠ **只影响"轮换自动挑"**（`ok()` 里走 `AvoidConflicts`）—— **用户手动 `force.txt` /
            //     F12 手选**完全不经过这里，**想一起开测试照样能开** ✔（用户明确说"我测试的时候这样开没问题"）
            if (a.FleaMinLevel.HasValue && b.FleaMinLevel.HasValue) return true;

            // ★★★ v2.6.61【🚨 用户要求"这种类似的活动自动轮换都别出冲突的"· 全量冲突排查结论】
            //   【排查方法】88 活动 × 全部字段 ⇒ 摊成 `字段.路径` ⇒ **7744 对全覆盖**「同一路径、值不同」，
            //     再逐字段判"累加(Union/Multiply) vs 覆盖(Overwrite)"，并回源码核实合并语义。
            //   【结论】**只有 2 个字段是"覆盖型" ⇒ 需要互斥**（下面两条）；其余 1283 对**绝不能禁** ✗
            //     （`bossChance` 325 对 / `bossSpawns` 300 对 / `enableQuests` 210 对都是 UNION 各自追加，
            //      禁掉会把轮换池锁死 —— 26 个 AI 活动彼此全互斥就挑不出 AI 活动了）✔
            //
            // C-1 `forceWeatherSeason`：**覆盖型**（`Plugin.cs` 逐活动调 `WeatherSeasonDimension.Apply`
            //     + `ApplyEventSeason`，**无任何合并/取严逻辑** ⇒ 后写覆盖）
            //     ⇒ 值不同才冲突（值相同不算）· 影响 **24 对**（WINTER 6 个 / AUTUMN 4 个）
            if (!string.IsNullOrEmpty(a.ForceWeatherSeason) && !string.IsNullOrEmpty(b.ForceWeatherSeason)
                && !string.Equals(a.ForceWeatherSeason, b.ForceWeatherSeason, StringComparison.OrdinalIgnoreCase))
                return true;
            //
            // C-2 `traderPriceMultiplierPerTrader`：**乘法型**（`TraderAssortDimension.cs:185`），
            //     但**同一商人方向相反**时两个意图互相抵消（连乘 1.15×0.8 = 0.92×）⇒ 判冲突 ✗
            //     ⚠ **同向不冲突**（例：`cultist_pilgrimage` 0.8 + `thermal_for_treats` 0.8 ⇒ 0.64×，只是更便宜）✔
            if (a.TraderPriceMultiplierPerTrader != null && b.TraderPriceMultiplierPerTrader != null)
            {
                foreach (var kv in a.TraderPriceMultiplierPerTrader)
                {
                    if (!b.TraderPriceMultiplierPerTrader.TryGetValue(kv.Key, out var vb)) continue;
                    double va = kv.Value, vb2 = vb;
                    if (va <= 0 || vb2 <= 0) continue;
                    bool aUp = va > 1.0, bUp = vb2 > 1.0;
                    bool aDown = va < 1.0, bDown = vb2 < 1.0;
                    if ((aUp && bDown) || (aDown && bUp)) return true;   // 一涨一折 ⇒ 冲突
                }
            }

            foreach (var t in TouchSet(a))
                if (TouchSet(b).Contains(t)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>(地图, 首领) 触碰集合 —— 该活动会改到哪些图的哪些首领。</summary>
    private static HashSet<string> TouchSet(EventDef e)
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddSpawns(Dictionary<string, List<string>> cfg)
        {
            if (cfg == null) return;
            foreach (var kv in cfg)
                foreach (var b in kv.Value ?? new List<string>())
                    s.Add(kv.Key.ToLowerInvariant() + "|" + (b ?? "").ToLowerInvariant());
        }

        AddSpawns(e.BossSpawns);
        AddSpawns(e.ZeroBossSpawns);
        // 注：ForceExtraSpawns 是 {地图: 数量}（不点名首领），语义上是"给这张图多加几波"，
        //     无法精确到角色，所以不参与"同图同首领"的冲突判定，避免误判成互斥。

        if (e.BossChance != null)
            foreach (var kv in e.BossChance)
                foreach (var b in kv.Value?.Keys ?? Enumerable.Empty<string>())
                    s.Add(kv.Key.ToLowerInvariant() + "|" + b.ToLowerInvariant());

        return s;
    }
}
