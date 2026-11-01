using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace EventRotator;

/// <summary>
/// ★ 季节轮换（合并 spt_season_rotator 的核心能力，且**不依赖外部 mod**）。
///
/// 问题：SPT 自带的季节是**按现实日期**判定的（SeasonalEventService 比对 seasonalevents.json 的
///       startMonth/endMonth）→ 万圣节 10-28~11-09、圣诞 12-13~12-31、新年 1-1、愚人节 4-1，
///       也就是**几个月才轮到一次**，想连着体验不同季节要等一整年。
///
/// 本机制：用"档位"把季节压缩到任意天数，轮换顺序也可配。
///
/// config.json：
/// <code>
/// "seasonRotation": {
///   "mode": "rotate",            // rotate = 按天数轮换；followEvent = 跟活动（默认）；off = 按现实日期
///   "intervalDays": 30,          // ★ 每个季节持续多少天（你想"一个月一季"就写 30）
///   "order": ["HALLOWEEN","CHRISTMAS","NEWYEARS","APRILFOOLS"],
///   "offset": 0                  // 从 order 的第几个开始（想从圣诞开始就写 1）
/// }
/// </code>
///
/// 优先级：seasonRotation.mode=rotate 时**最高**（压过活动的 seasonalEvent 和现实日期）；
///         否则活动自带 seasonalEvent 生效；都没有则回落到现实日期。
/// </summary>
internal static class SeasonRotation
{
    /// <summary>rotate / fixed / followEvent / off</summary>
    public static string Mode = "followEvent";
    public static int IntervalDays = 30;
    public static int Offset;
    /// <summary>★ fixed 模式：直接指定季节（HALLOWEEN / CHRISTMAS / NEWYEARS / APRILFOOLS）。</summary>
    public static string FixedSeason;
    // ═══════════ ★ v0.1.2 活动季节（独立控制）═══════════
    /// <summary>活动季节模式：off（不动）/ fixed（用 EventSeasonVal）。</summary>
    public static string EventSeasonMode = "off";
    /// <summary>指定的活动季节：HALLOWEEN / CHRISTMAS / NEWYEARS / APRILFOOLS。</summary>
    public static string EventSeasonVal;

    /// <summary>活动季节是否被明确指定。</summary>
    public static bool IsEventSeasonFixed =>
        string.Equals(EventSeasonMode, "fixed", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(EventSeasonVal);

    /// <summary>
    /// ★★★ v2.6.40【用户裁决 ⑨-2】**组合展开是否"按实际选择来"**（true ⇒ **不做**日期窗过滤）。
    ///
    /// <para><b>用户原话</b>：「**未指定季节 或者 未开启季节轮换时 按照现实时间来，开启季节轮换后 按实际选择来**」</para>
    ///
    /// <para><b>判据（两条"实际选择"，其余一律按现实时间）</b>：</para>
    /// <list type="number">
    /// <item><b><see cref="IsRotating"/></b> —— `config.seasonRotation.mode == "rotate"`（`SeasonRotation.cs:484` 读入）
    ///       ⇒ **季节轮换开着** ⇒ 轮换到哪一季就允许展开那一族（哪怕现实日期对不上）。</item>
    /// <item><b><see cref="IsEventSeasonFixed"/></b> —— `config.eventSeason.mode == "fixed"` 且有季节
    ///       （`SeasonRotation.cs:466-469` 读入）⇒ 用户**显式指定了活动季节**。</item>
    /// </list>
    ///
    /// <para><b>⚠ 为什么 `seasonRotation.mode == "fixed"` 归到"按现实时间"</b>：
    /// 它指定的是**天气季节**，不是**活动季节**。实机配置正是 `seasonRotation.mode="fixed"`（WINTER）
    /// + `eventSeason.mode="off"` + `autoOpenSeasonalEvent=false`
    /// ⇒ **季节活动根本不自动开，只会被日期窗开出来** ⇒ 就是"按照现实时间来" ✔
    /// （与用户裁决里「mode=fixed ⇒ 8/15 不再展开 christmas」的验收口径一致）</para>
    ///
    /// <para><b>返回 null = 拿不到</b> ⇒ 调用方**照旧展开**（保持现状）+ warning（可降级）。</para>
    /// </summary>
    public static bool? ExpansionFollowsSeasonChoice()
    {
        try
        {
            if (IsRotating) return true;            // 季节轮换开着 ⇒ 按实际选择
            if (IsEventSeasonFixed) return true;    // 显式指定了活动季节 ⇒ 按实际选择
            return false;                           // followEvent / fixed / off ⇒ 按现实时间
        }
        catch { return null; }                      // 拿不到 ⇒ 交给调用方降级
    }

    /// <summary>把上面那条判据描述成日志用的一句话（季节模式 + 当前季节）。</summary>
    public static string SeasonChoiceDesc()
    {
        try
        {
            // ★ v1.6.0【S10a】rotate 模式下应显示"轮换到的季节"，而不是 config 里残留的固定季节
            var shown = IsRotating ? (CurrentSeason() ?? "-") : (FixedSeason ?? CurrentSeason() ?? "-");
            return $"seasonRotation.mode={Mode}(季节={shown})"
                 + $" / eventSeason.mode={EventSeasonMode}(季节={EventSeasonVal ?? "-"})"
                 + $" / autoOpenSeasonalEvent={AutoOpenSeasonalEvent}";
        }
        catch { return "(季节模式读取失败)"; }
    }

    /// <summary>
    /// ★ v2.6.1 天气季节 → 对应的【活动季节】（SeasonalEventType）。
    ///   用户要求：进入相应季节就自动开那个活动季节；手动指定了活动就忽略。
    /// </summary>
    public static readonly Dictionary<string, string> SeasonToEventMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SPRING"] = "APRILFOOLS",
        ["SPRING_EARLY"] = "APRILFOOLS",
        ["SUMMER"] = "",
        ["AUTUMN"] = "HALLOWEEN",
        ["AUTUMN_LATE"] = "HALLOWEEN",
        ["WINTER"] = "CHRISTMAS",
        ["STORM"] = "",
    };

    /// <summary>是否启用「天气季节 → 自动开活动季节」。config: seasonRotation.autoOpenSeasonalEvent（默认 true）</summary>
    public static bool AutoOpenSeasonalEvent = false;   // ★ v0.1.2 默认关（联动变成可选）

    // ═══════════ ★ v0.1.4 季节活动在季节内何时开启 ═══════════
    /// <summary>
    /// 季节活动触发阶段（config: seasonRotation.eventPhase）：
    /// <list type="bullet">
    /// <item><c>off</c>    = 不自动开季节活动（想开自己 F12 勾）</item>
    /// <item><c>start</c>  = 季节一开始就开</item>
    /// <item><c>middle</c> = 季节过半才开（按活动官方持续时长顺延到季末）</item>
    /// <item><c>end</c>    = 季节收尾才开（活动恰好结束在季末）</item>
    /// </list>
    /// </summary>
    public static string EventPhase = "off";

    /// <summary>
    /// 各季节活动的**官方持续天数**（v0.1.4 默认取本体真实日期窗）：
    /// 万圣节 10/28–11/9（13 天）、圣诞 12/13–12/31（19 天）、新年 12/31–1/1（2 天）、愚人节 4/1（1 天）。
    /// 可在 F12 里改（config: seasonalEventDurations）。
    /// </summary>
    public static readonly Dictionary<string, int> EventDurationDays = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HALLOWEEN"] = 13,
        ["CHRISTMAS"] = 19,
        ["NEWYEARS"] = 2,
        ["APRILFOOLS"] = 1,
    };

    /// <summary>该季节活动的官方持续天数（取不到时给 7 天兜底）。</summary>
    public static int DurationOf(string eventSeason)
    {
        if (string.IsNullOrEmpty(eventSeason)) return 7;
        return EventDurationDays.TryGetValue(eventSeason.Trim().ToUpperInvariant(), out var v) && v > 0 ? v : 7;
    }

    /// <summary>
    /// ★ v0.1.4 季节轮换的**最低天数锁定**（用户要求：**看切到的是哪个季节活动**）。
    /// <para>
    /// 规则：取**当前这个季节对应的那个季节活动**的官方持续时长作为下限。
    ///   · 切到秋天 → 万圣节 → 下限 13 天
    ///   · 切到冬天 → 圣诞节 → 下限 19 天
    ///   · 切到春天 → 愚人节 → 下限 1 天
    ///   · 夏天/暴风雨没有对应活动 → 不锁（下限 1 天）
    /// </para>
    /// 只有开了"季节轮换"（或者开了季节活动阶段）时才锁；关掉之后就随便设。
    /// </summary>
    public static int MinRotationDays()
    {
        bool needLock = IsRotating || (AutoOpenSeasonalEvent && !string.Equals(EventPhase, "off", StringComparison.OrdinalIgnoreCase));
        if (!needLock) return 1;
        var evSeason = CurrentEventSeasonForWeather(CurrentSeason());
        if (string.IsNullOrEmpty(evSeason)) return 1;      // 夏天/暴风雨：没有季节活动 → 不锁
        return Math.Max(1, DurationOf(evSeason));
    }

    /// <summary>
    /// ★ v0.1.4 按阶段判断"现在季节活动该不该开"。
    /// 返回 null = 不该开（或阶段为 off）；否则返回该季节对应的活动季节名。
    /// </summary>
    private static string _lastDurEndKey;

    public static string EventSeasonDueNow()
    {
        try
        {
            if (string.Equals(EventPhase, "off", StringComparison.OrdinalIgnoreCase)) return null;
            if (!IsRotating) return null;                     // 只有"季节轮换"模式下才有"季节内阶段"概念
            var ev = CurrentEventSeasonForWeather(CurrentSeason());
            if (string.IsNullOrEmpty(ev)) return null;        // 夏天/暴风雨没有对应活动
            var start = SeasonStart(CurrentSlot());
            int offset = EventStartOffsetDays(IntervalDays);
            var due = start.AddDays(offset);
            if (DateTime.Now < due) return null;

            // ★★★ v1.6.0【R8 · 用户裁决 A】季节活动**按官方时长结束**。
            //   以前只看"到没到开启时间"，从不判结束 ⇒ 圣诞/万圣节会开满整个 30 天季节，
            //   而 `onlyWhenNoSeasonal=true` 又让填充轮换整季停摆
            //   ⇒ 2026 年主轮换只剩 17 档 / 95 天有产出，17 个带任务活动要 3.4 年才轮完。
            //   现在：`due + 官方时长` 之后本季不再"有季节活动在跑" ⇒ 剩余天数恢复填充轮换。
            //   ⚠ 说明：SPT 的 `ForceSeasonalEvent` 没有回滚，天气/物资的节庆氛围会**持续到本季结束**；
            //     这里结束的是"活动列表里的那一项"与"轮换暂停判据"，不是季节本身。
            int dur = DurationOf(ev);
            if (dur > 0 && DateTime.Now >= due.AddDays(dur))
            {
                // ★ v1.6.1【回归审计 #2】按官方时长结束后，本 mod 认为"本季没有季节活动在跑"
                //   （填充轮换恢复、Want* 标志关闭），但 SPT 的 ForceSeasonalEvent **无法回滚**，
                //   原生节日效果（天气/首领表/物资）会持续到换季 —— 这个错位必须让玩家看得见。
                var key = CurrentSeason() + ":" + ev;
                if (!string.Equals(_lastDurEndKey, key, StringComparison.Ordinal))
                {
                    _lastDurEndKey = key;
                    Warn($"[EventRotator][季节活动] 本季 {ev} 已按官方时长 {dur} 天结束（{due.AddDays(dur):MM-dd}）"
                       + " ⇒ 活动列表不再列它、填充轮换恢复；"
                       + "⚠ 但 SPT 原生节日效果无法回滚，会持续到换季（这是官方 API 的限制）。");
                }
                return null;
            }

            return ev;
        }
        catch { return null; }
    }

    /// <summary>★ v0.1.4 当前季节对应的季节活动名（给日志/UI 用，没有则 null）。</summary>
    public static string CurrentSeasonalEventName()
        => CurrentEventSeasonForWeather(CurrentSeason());

    /// <summary>
    /// ★ v0.1.4 当前季节内，季节活动**应在哪一天开启**（从季节第 1 天起算，0-based 偏移）。
    /// phase=start → 0；phase=middle → 季节天数 - 活动持续天数（顺延到季末）；
    /// phase=end → 同上（"收尾"语义与"过半"在"活动持续到季末"这一点上一致，
    /// 区别在于 middle 会在活动未开时先给一段"纯季节"的缓冲）。取不到季节天数时返回 0。
    /// </summary>
    public static int EventStartOffsetDays(int seasonDays)
        => EventStartOffsetDays(seasonDays, CurrentEventSeasonForWeather(CurrentSeason()));

    /// <summary>★ v0.1.4 可离线测的重载：显式给出季节活动，不依赖当前日期/当前季节。</summary>
    public static int EventStartOffsetDays(int seasonDays, string eventSeason)
    {
        if (seasonDays <= 0) return 0;
        int dur = DurationOf(eventSeason);
        // ★ v1.5.0【A3 修】以前 middle 和 end 是同一个公式（`seasonDays - dur`），
        //   于是 CHRISTMAS（19 天）在 30 天季节的第 12 天（36.7%）就开了 —— 并不是"过半"。
        //   现在：middle = 季节过半；end = 最后 dur 天（真·收尾）。
        return EventPhase switch
        {
            "start" => 0,
            // ★★★ v1.5.1【P0-1 修】以前的写法在两个方向上都不自洽：
            //   ① `middle = seasonDays/2` 在 `dur > seasonDays/2` 时会让活动**溢出季末**
            //      （圣诞 19 天塞进 30 天季节：第 15 天开 → 跑到第 34 天，跨进春天 4 天）；
            //   ② `end = max(半季, seasonDays-dur)` 在 `seasonDays ≤ 2*dur` 时**与 middle 完全相同**，
            //      “收尾”选项退化成“过半”选项。
            //   ⇒ middle = min(半季, 季末-dur)、end = 季末-dur，恒有
            //      `0 ≤ middle ≤ end` 且 `end + dur ≤ seasonDays`（不溢出、不倒挂）。
            "middle" => Math.Max(0, Math.Min(seasonDays / 2, seasonDays - dur)),
            "end" => Math.Max(0, seasonDays - dur),
            _ => 0,
        };
    }

    /// <summary>
    /// ★ v1.6.0【S5 修】返回"映射到某个**活动季节**的天气季节档位，到目前一共命中过多少次"。
    /// 与 <see cref="SeasonHitsSoFar"/> 的区别：后者按单个天气季节计数，
    /// 于是 AUTUMN / AUTUMN_LATE 这种"两档同映射"会各自从 1 开始 ⇒ 连续两季得到同一个变体。
    /// </summary>
    private static long SeasonHitsSoFarByEventSeason(string eventSeason)
    {
        try
        {
            if (Order.Count == 0 || string.IsNullOrEmpty(eventSeason)) return 1;
            var epoch = new DateTime(2020, 1, 1);
            long totalSlots = (long)Math.Floor((DateTime.Now.Date - epoch).TotalDays / Math.Max(1, IntervalDays)) + 1;
            long hits = 0;
            for (long k = 0; k < totalSlots; k++)
            {
                int idx = (int)(((k + Offset) % Order.Count + Order.Count) % Order.Count);
                var ev = CurrentEventSeasonForWeather(Order[idx]);
                if (string.Equals(ev, eventSeason, StringComparison.OrdinalIgnoreCase)) hits++;
            }
            return Math.Max(1, hits);
        }
        catch { return 1; }
    }

    /// <summary>
    /// ★ v1.6.0【S4 修】季节最少天数应当保证**所有季节**里最长的官方活动都塞得下，
    /// 而不是只看"服务端启动那一刻所在的那个季节"（旧实现：启动在春天(愚人节 1 天)时
    /// 允许 intervalDays=14 ⇒ 轮到冬天时 19 天的圣诞溢出 12 天）。
    /// </summary>
    public static int MinRotationDaysAcrossAllSeasons()
    {
        try
        {
            int max = 1;
            foreach (var season in Order)
            {
                var ev = CurrentEventSeasonForWeather(season);
                if (string.IsNullOrEmpty(ev)) continue;
                max = Math.Max(max, DurationOf(ev));
            }
            return Math.Max(1, max);
        }
        catch { return 1; }
    }

    /// <summary>当前季节进度（0.0~1.0）。</summary>
    public static double SeasonProgress()
    {
        try
        {
            var start = SeasonStart(CurrentSlot());
            double days = (DateTime.Now - start).TotalDays;
            return IntervalDays <= 0 ? 0 : Math.Max(0, Math.Min(1, days / IntervalDays));
        }
        catch { return 0; }
    }

    /// <summary>第 slot 个季节的开始时刻（与 <see cref="CurrentSlot"/> 同一套算法：epoch=2020-01-01）。</summary>
    public static DateTime SeasonStart(int slot)
    {
        try
        {
            var epoch = new DateTime(2020, 1, 1);
            return epoch.AddDays((double)slot * Math.Max(1, IntervalDays));
        }
        catch { return DateTime.Now; }
    }

    /// <summary>
    /// ★★★ v0.1.4 同一季节里的**季节活动变体**排序（用户要求：同季节有多个变体，轮换要在同季节内切）。
    ///
    /// 实测各季节的变体数量：
    ///   · Halloween 4 个：halloween（主）/ halloween_2024 / halloween_2023 / halloween_2022
    ///   · Christmas 6 个：christmas（主）/ khorovod_2024 / kolotun / santa_2023 / santa_2022 / santa_2021
    ///   · NewYears  1 个：newyear
    ///   · AprilFools 1 个：april_fools
    ///
    /// 顺序 = 主活动优先，然后"越新的版本越靠前"（把 `_2024` 排在 `_2022` 之前）。
    /// 每过一个季节档位就往后取一个 → 同一个季节反复出现时会**换个变体**。
    /// </summary>
    public static List<EventDef> SeasonalVariantsOf(string eventSeason, List<EventDef> all)
    {
        var res = new List<EventDef>();
        try
        {
            if (string.IsNullOrEmpty(eventSeason) || all == null) return res;
            // ★ v1.5.0【A1 修】以前这里用 VariantRank 排序（主活动恒第一），
            //   而真正轮转用的是 familyYear 倒序 ⇒ 日志印的"顺序 + 第 N 个"与轮转次序不一致，
            //   玩家照日志预测下个冬天是哪个会猜错。现在两处共用同一排序。
            // ★ v1.6.0【S3-A 用户裁决：本体优先】
            //   先排"每年都开的本体"（familyGeneric=true），再按年份新的在前，最后按 id。
            //   这样 christmas（generic、无 familyYear）不会被垫到第 3 位，圆环舞/往年场次排在其后。
            res = all.Where(e => string.Equals(e.SeasonalEvent, eventSeason, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(e => e.FamilyGeneric == true ? 1 : 0)
                     .ThenByDescending(e => e.FamilyYear ?? int.MinValue)
                     .ThenBy(e => e.Id, StringComparer.Ordinal)
                     .ToList();
        }
        catch { }
        return res;
    }

    /// <summary>变体排序权重：越小越靠前。**主活动永远第一**，然后越新的年份版本越靠前。</summary>
    private static int VariantRank(string id)
    {
        if (string.IsNullOrEmpty(id)) return 900;
        var s = id.ToLowerInvariant();
        // 主活动（就是季节本身）永远第一
        if (s is "halloween" or "christmas" or "newyear" or "april_fools" or "newyears") return 0;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"_(19|20)(\d{2})$");
        if (m.Success)
        {
            int year = int.Parse(m.Groups[1].Value + m.Groups[2].Value);
            return 100 + (2100 - year);               // 2024 → 176，2022 → 178 ⇒ 新的靠前（都在主活动之后）
        }
        return 300;                                   // 无年份的具名变体（kolotun / halloween_infected）→ 主体之后
    }

    /// <summary>
    /// ★★★ v0.1.4 当前季节档位**应该用哪个季节活动变体**。
    /// <para>
    /// 用户要求：**随机选**，但在"下一次重置之前"不会抽到**已经开过的**变体；变体太少的季节跳过这套逻辑
    /// （避免轮换出 bug）。
    /// </para>
    /// <para>
    /// 实测变体数量：Halloween 4 个、Christmas 6 个、NewYears 1 个、AprilFools 1 个。
    /// <b>变体 ≤ 2 个的季节直接走确定性取第一个</b>（没有"随机不重复"的意义，也不值得冒风险）。
    /// </para>
    /// ★ v1.6.0：变体选择已改为**按季节命中次数的纯函数**，不再读写 seasonal_state.json。
    /// </summary>
    public static EventDef PickSeasonalVariant(List<EventDef> all)
        => PickSeasonalVariantFor(CurrentEventSeasonForWeather(CurrentSeason()), all);

    /// <summary>★ v0.1.4 可显式指定季节的重载（自检 / 复用；不依赖当前日期与季节档位）。</summary>
    /// <summary>
    /// ★ v1.6.5【服务端审计 #6】**唯一的变体环**：选择逻辑 / 日志 / 离线复算都用它。
    /// 规则：家族里只保留一个 generic 本体（id 等于 family 名那个，如 christmas），
    /// 其余 generic（newyear / christmas_january）靠"同族整组展开"带出，不参与轮换；
    /// 排序：本体优先 → 年份新的在前 → id。
    /// </summary>
    public static List<EventDef> SeasonalRingOf(string eventSeason, List<EventDef> all)
    {
        var res = new List<EventDef>();
        try
        {
            var variants = SeasonalVariantsOf(eventSeason, all);
            if (variants.Count == 0) return res;

            var primary = variants.FirstOrDefault(v =>
                    v.FamilyGeneric == true && !string.IsNullOrEmpty(v.Family)
                    && string.Equals(v.Id, v.Family, StringComparison.OrdinalIgnoreCase))
                ?? variants.Where(v => v.FamilyGeneric == true).OrderBy(v => v.Id, StringComparer.Ordinal).FirstOrDefault();

            var ring = variants.Where(v => v.FamilyGeneric != true || ReferenceEquals(v, primary)).ToList();
            if (ring.Count == 0) ring = variants.ToList();

            res = ring.OrderByDescending(v => v.FamilyGeneric == true ? 1 : 0)
                      .ThenByDescending(v => v.FamilyYear ?? int.MinValue)
                      .ThenBy(v => v.Id, StringComparer.Ordinal)
                      .ToList();
        }
        catch { }
        return res;
    }

    public static EventDef PickSeasonalVariantFor(string evSeason, List<EventDef> all)
    {
        try
        {
            if (string.IsNullOrEmpty(evSeason)) return null;
            var variants = SeasonalVariantsOf(evSeason, all);
            if (variants.Count == 0) return null;

            // 变体太少 → 不做随机不重复（用户明确要求：免得轮换出 bug）
            if (variants.Count <= 2) return variants[0];

            // ═══════════════════════════════════════════════════════════════════════
            // ★★★ v1.3.6【重启不再推进变体】用户实测反馈：
            //   「我们一共就重启了两次吧，所以我们更换文件也会直接进新轮换？」
            //   旧实现：每次**服务端启动**都从"没开过"的变体里挑一个并记账 ⇒ **重启 N 次就把变体刷一轮**。
            //   玩家视角这就是"重启/换文件就换内容"，会被当成 bug。
            //   ⇒ 现在改为**只跟季节档位走**：
            //       变体 = 有序列表[ (自 2020-01-01 起经过的完整季节数) % 变体数 ]
            //     同一季永远同一个变体（重启多少次都不变），换季才换 —— 与主轮换/奖励轮换的"档位"语义一致。
            //   ⚠ 顺序固定为：**年份新的在前**（khorovod_2024 → santa_2022 → 无年份的本体），同年按 id。
            //     这样"下一个冬天接着是下一个没出现过的变体"，行为可预期、离线可复算。
            // ═══════════════════════════════════════════════════════════════════════
            // ★ v1.6.1【回归审计 #4】环里**只留一个** generic 本体（家族名那个，如 christmas），
            //   其余 generic（newyear / christmas_january）靠"同族整组展开"带出即可。
            //   否则前 3 个冬天选中不同的 generic，但展开把它们一次全带出来 ⇒ 产出完全相同、看不出换变体。
            var primaryGeneric = variants.FirstOrDefault(v =>
                    v.FamilyGeneric == true && !string.IsNullOrEmpty(v.Family)
                    && string.Equals(v.Id, v.Family, StringComparison.OrdinalIgnoreCase))
                ?? variants.Where(v => v.FamilyGeneric == true).OrderBy(v => v.Id, StringComparer.Ordinal).FirstOrDefault();

            var ringVariants = variants.Where(v => v.FamilyGeneric != true
                                                || ReferenceEquals(v, primaryGeneric)).ToList();
            if (ringVariants.Count == 0) ringVariants = variants.ToList();

            var ordered = ringVariants
                .OrderByDescending(v => v.FamilyGeneric == true ? 1 : 0)
                .ThenByDescending(v => v.FamilyYear ?? int.MinValue)
                .ThenBy(v => v.Id, StringComparer.Ordinal)
                .ToList();

            int seasonIndex = SeasonIndexOfCurrent();
            // ★★★ v1.3.6 修正：变体序号必须按【该季节出现过的次数】算，不能按【总共过了几个季节】算。
            //   反例（用户实测提问）：「现在冬天固定一个变体，下一次轮到冬天再换另一个变体对吧？」
            //   若用 `seasonIndex % 变体数`：季节循环是 4 个（春/夏/秋/冬），变体数往往也是 4
            //   ⇒ 两个冬天之间 seasonIndex +4，`+4 % 4 == 0` ⇒ **永远是同一个变体**，永不切换。
            //   ⇒ 现在改为 `(seasonIndex / 季节数)` = 「这是第几个年度周期」，再对变体数取模：
            //      同一个冬天恒定；下一次轮到冬天（4 个季节之后）取下一个变体。
            // ★ v1.5.0【A4 修】以前用"第几个周期"，当 order 里有多个档位映射同一活动季时
            //   （如 AUTUMN / AUTUMN_LATE 都映射 HALLOWEEN）同一周期两次命中会用同一个变体。
            //   现在用"该天气季节到目前被轮到的次数"（SeasonHitsSoFar 本来是死代码）。
            // ★ v1.6.0【S5 修】命中次数按**活动季节**聚合：
            //   order 里 AUTUMN 与 AUTUMN_LATE 都映射到 HALLOWEEN 时，旧实现分别计数
            //   ⇒ 连续两季命中数相同 ⇒ 连续两季同一个变体。现在把映射到同一 evSeason 的档位一起数。
            int occurrence = (int)Math.Max(0, SeasonHitsSoFarByEventSeason(evSeason) - 1);
            int idx = ((occurrence % ordered.Count) + ordered.Count) % ordered.Count;
            var pick = ordered[idx];

            Info($"[EventRotator][季节变体] {evSeason} 共 {ordered.Count} 个；季节序号 #{seasonIndex}"
                 + $"（第 {occurrence + 1} 轮）⇒ 固定取 [{idx}]『{pick.NameZh}』"
                 + $"（同季重启不变；下一次轮到该季节才换下一个）"
                 + $"｜顺序：{string.Join(" → ", ordered.Select(v => v.Id))}");
            return pick;
        }
        catch (Exception ex) { Warn("[EventRotator] 季节变体选择失败: " + ex.Message); return null; }
    }
    /// <summary>
    /// ★ v1.6.0 当前是第几个季节（自 2020-01-01 起，每 `IntervalDays` 天 +1），**含 Offset**。
    /// 与 `CurrentSlot()`（活动轮换档位）不是一回事。
    /// </summary>
    public static int SeasonIndexOfCurrent()
    {
        try
        {
            var epoch = new DateTime(2020, 1, 1);
            var days = (DateTime.Today - epoch).TotalDays;
            return (int)Math.Floor(days / Math.Max(1, IntervalDays)) + Offset;
        }
        catch { return 0; }
    }

    /// <summary>
    /// ★ v0.1.4 可离线测的核心：给定"第几次遇到该季节"，取对应的变体（确定性版本，自检用）。
    /// </summary>
    public static EventDef VariantForHit(string eventSeason, List<EventDef> all, long hit)
    {
        try
        {
            var variants = SeasonalVariantsOf(eventSeason, all);
            if (variants.Count == 0) return null;
            if (variants.Count == 1) return variants[0];
            long h = Math.Max(1, hit) - 1;
            return variants[(int)(h % variants.Count)];
        }
        catch { return null; }
    }

    /// <summary>从 epoch 到现在，这个季节一共被轮到了几次（1-based；当前这次也算）。</summary>
    private static long SeasonHitsSoFar(string weatherSeason)
    {
        try
        {
            if (Order.Count == 0 || string.IsNullOrEmpty(weatherSeason)) return 1;
            int idx = Order.FindIndex(x => string.Equals(x, weatherSeason, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return 1;
            var epoch = new DateTime(2020, 1, 1);
            long totalSlots = (long)Math.Floor((DateTime.Now.Date - epoch).TotalDays / Math.Max(1, IntervalDays)) + 1;
            // 档位按 Order 顺序循环，所以第 n 个档位的季节 = Order[(n + Offset) % Order.Count]
            long hits = 0;
            for (long k = 0; k < totalSlots; k++)
                if (((k + Offset) % Order.Count + Order.Count) % Order.Count == idx) hits++;
            return Math.Max(1, hits);
        }
        catch { return 1; }
    }

    /// <summary>天气季节 → 活动季节（用 SeasonToEventMap）。</summary>
    public static string CurrentEventSeasonForWeather(string weatherSeason)
    {
        if (string.IsNullOrEmpty(weatherSeason)) return null;
        var key = weatherSeason.Trim().ToUpperInvariant();
        if (SeasonToEventMap.TryGetValue(key, out var v)) return v;
        // ★ v1.3.7【D4b】查不到就**明确告警一次**（以前静默 → 季节阶段"看起来没开"却查不出原因）
        Warn($"[EventRotator][季节映射] ⚠ 天气季节 '{key}' 没有对应的活动季节（可用：{string.Join("/", SeasonToEventMap.Keys)}）"
             + "｜检查 config.json 的 seasonRotation.order 是否写成了天气季节名");
        return null;
    }

    /// <summary>
    /// ★ v1.3.7【D4 修】默认季节顺序必须是**天气季节**（与 <see cref="SeasonToEventMap"/> 的键一致）：
    /// 以前默认写成 HALLOWEEN/CHRISTMAS/NEWYEARS/APRILFOOLS（活动季节名），
    /// 一旦 config 里**没写** `seasonRotation.order`，`CurrentSeason()` 就返回活动季名 ⇒
    /// `CurrentEventSeasonForWeather` 查不到 ⇒ `EventSeasonDueNow` 恒 null ⇒
    /// **季节活动阶段永久静默**（日志只有"本季没有对应季节活动"）。
    /// </summary>
    public static readonly List<string> Order = new()
    {
        "SPRING", "SUMMER", "AUTUMN", "WINTER"
    };

    private static void Info(string m) { try { EventEngine.Log?.Info(m); } catch { } }
    private static void Warn(string m) { try { EventEngine.Log?.Warning(m); } catch { } }

    public static void Load(JsonElement root, SPTarkov.Common.Models.Logging.ISptLogger<EventRotatorServer> log)
    {
        try
        {
            // ★★ v0.1.2 活动季节（独立节点 eventSeason）—— 必须放在 seasonRotation 检查【之前】，
            //    否则配置里没有 seasonRotation 时会直接 return，eventSeason 永远读不到。
            EventSeasonMode = "off";
            EventSeasonVal = null;
            if (root.TryGetProperty("eventSeason", out var es) && es.ValueKind == JsonValueKind.Object)
            {
                if (es.TryGetProperty("mode", out var em) && em.ValueKind == JsonValueKind.String)
                    EventSeasonMode = (em.GetString() ?? "off").Trim();
                if (es.TryGetProperty("season", out var evs2) && evs2.ValueKind == JsonValueKind.String)
                    EventSeasonVal = (evs2.GetString() ?? "").Trim().ToUpperInvariant();
                if (IsEventSeasonFixed)
                    log?.Warning($"[EventRotator] ★活动季节已【直接指定】→ {EventSeasonVal}（独立于天气季节，万圣/圣诞/新年/愚人节）");
                else
                    log?.Info($"[EventRotator] 活动季节模式 = {EventSeasonMode}（off=不干预 / fixed=指定）");
            }
            else
                log?.Info("[EventRotator] 未配置 eventSeason 节点 → 活动季节不干预");

            if (!root.TryGetProperty("seasonRotation", out var sr) || sr.ValueKind != JsonValueKind.Object)
                return;

            if (sr.TryGetProperty("mode", out var md) && md.ValueKind == JsonValueKind.String)
                Mode = (md.GetString() ?? "followEvent").Trim();
            if (sr.TryGetProperty("intervalDays", out var iv) && iv.ValueKind == JsonValueKind.Number)
                IntervalDays = Math.Max(1, iv.GetInt32());
            if (sr.TryGetProperty("offset", out var of) && of.ValueKind == JsonValueKind.Number)
                Offset = of.GetInt32();
            if (sr.TryGetProperty("season", out var se) && se.ValueKind == JsonValueKind.String)
                FixedSeason = (se.GetString() ?? "").Trim().ToUpperInvariant();
            if (sr.TryGetProperty("order", out var od) && od.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var x in od.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String) { var s = x.GetString(); if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim().ToUpperInvariant()); }
                if (list.Count > 0) { Order.Clear(); Order.AddRange(list); }
            }
            // ★ v0.1.2 天气季节 → 自动开活动季节（可选，默认关）
            if (sr.TryGetProperty("autoOpenSeasonalEvent", out var ao) && (ao.ValueKind == JsonValueKind.True || ao.ValueKind == JsonValueKind.False))
                AutoOpenSeasonalEvent = ao.GetBoolean();

            // ★★★ v0.1.4 季节活动触发阶段（off / start / middle / end）
            if (sr.TryGetProperty("eventPhase", out var ep) && ep.ValueKind == JsonValueKind.String)
                EventPhase = (ep.GetString() ?? "off").Trim().ToLowerInvariant();
            if (sr.TryGetProperty("seasonalEventDurations", out var sed) && sed.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in sed.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number)
                    {
                        var v = p.Value.GetInt32();
                        if (v > 0) EventDurationDays[p.Name.Trim().ToUpperInvariant()] = v;
                    }
            }

            // ★ 最低天数锁定：按**当前季节对应的那个季节活动**的官方时长（用户要求：看切到哪个季节活动）
            // ★ v1.6.0【S4】下限改成"所有季节里最长的官方活动时长"（旧实现只看启动那一刻的季节）
            // ★ v1.6.1【回归审计 #7】只在"季节轮换真的会用 intervalDays"时才抬下限，
            //   否则 fixed/off 模式会被强行抬到 19 天并打印"想自由设置请把 eventPhase 设成 off"（而它已经是 off）。
            bool needLock = IsRotating || (AutoOpenSeasonalEvent
                            && !string.Equals(EventPhase, "off", StringComparison.OrdinalIgnoreCase));
            int minDays = needLock ? MinRotationDaysAcrossAllSeasons() : 1;
            if (IntervalDays < minDays)
            {
                var evName = CurrentSeasonalEventName();
                log?.Warning($"[EventRotator] ⚠ 季节轮换周期 {IntervalDays} 天 < 最低下限 {minDays} 天"
                             + $"（本季 {CurrentSeason()} 对应的季节活动 {evName} 官方时长 {minDays} 天）"
                             + $" → 已自动抬到 {minDays} 天。"
                             + "原因：轮换太快会出现「季节活动还没开完就换季」。想自由设置请把 eventPhase 设成 off。");
                IntervalDays = minDays;
            }

            if (string.Equals(Mode, "rotate", StringComparison.OrdinalIgnoreCase))
            {
                var phaseZh = EventPhase switch
                {
                    "start" => "季节一开始就开",
                    "middle" => "季节过半才开",
                    "end" => "季节收尾才开",
                    _ => "不自动开（off）",
                };
                var curEv = CurrentEventSeasonForWeather(CurrentSeason());
                var now = DateTime.Now;
                var start = SeasonStart(CurrentSlot());
                var due = start.AddDays(EventStartOffsetDays(IntervalDays));
                log?.Warning($"[EventRotator] ★季节轮换已启用：每 {IntervalDays} 天换一季，顺序 {string.Join(" → ", Order)}"
                             + $"（当前档位 → {CurrentSeason()}）  最低锁定 {minDays} 天");
                log?.Info($"[EventRotator]   季节活动阶段 = {EventPhase}（{phaseZh}）"
                          + (string.IsNullOrEmpty(curEv)
                                ? "；本季（" + CurrentSeason() + "）没有对应季节活动"
                                : $"；本季对应 {curEv}（官方时长 {DurationOf(curEv)} 天），"
                                  + (now >= due ? $"已于 {due:MM-dd HH:mm} 满足开启条件 ✔ 现在应该开着"
                                                : $"将于 {due:MM-dd HH:mm} 开启（本季从 {start:MM-dd HH:mm} 开始，进度 {SeasonProgress() * 100:0}%）")));
            }
            else if (string.Equals(Mode, "fixed", StringComparison.OrdinalIgnoreCase))
                log?.Warning($"[EventRotator] ★季节已【指定】为 {FixedSeason}（fixed 模式，不随日期/天数变化）");
            else
                log?.Info($"[EventRotator] 季节模式 = {Mode}（followEvent=跟活动 / rotate=按天数轮换 / fixed=指定季节 / off=按现实日期）");
        }
        catch (Exception ex) { log?.Warning($"[EventRotator] seasonRotation 配置解析失败: {ex.Message}"); }
    }

    /// <summary>当前档位序号（跨重启稳定）。</summary>
    public static int CurrentSlot()
    {
        try
        {
            var epoch = new DateTime(2020, 1, 1);
            var days = (DateTime.Today - epoch).TotalDays;
            return (int)Math.Floor(days / Math.Max(1, IntervalDays));
        }
        catch { return 0; }
    }

    /// <summary>按轮换算出的当前季节（SeasonalEventType 的枚举名）。</summary>
    public static string CurrentSeason()
    {
        try
        {
            // ★ v1.3.7【D5 修】`mode=fixed` 时天气就固定成 FixedSeason，
            //   但这里以前仍返回"轮换档位"→ 活动季节/阶段按错误的季节挑（日志与实际天气不一致）。
            if (IsFixed && !string.IsNullOrEmpty(FixedSeason)) return FixedSeason;
            if (Order.Count == 0) return null;
            var slot = CurrentSlot() + Offset;
            var idx = ((slot % Order.Count) + Order.Count) % Order.Count;
            return Order[idx];
        }
        catch { return null; }
    }

    /// <summary>rotate 模式是否生效（生效时季节由本机制决定）。</summary>
    public static bool IsRotating => string.Equals(Mode, "rotate", StringComparison.OrdinalIgnoreCase);

    /// <summary>★ fixed 模式：季节被固定在 FixedSeason。</summary>
    public static bool IsFixed => string.Equals(Mode, "fixed", StringComparison.OrdinalIgnoreCase)
                                  && !string.IsNullOrEmpty(FixedSeason);

    /// <summary>
    /// ★ v0.1.3 给 UI 用：把当前季节说成一句人话（客户端悬浮面板顶部显示）。
    /// 天气季节（春夏秋冬）与活动季节（万圣/圣诞/新年/愚人节）是**两个独立维度**，这里都体现。
    /// </summary>
    public static string FriendlySeason()
    {
        try
        {
            var parts = new List<string>();

            // 天气季节
            if (IsRotating)
            {
                var cur = CurrentSeason();
                var zh = WeatherSeasonZh(cur);
                if (!string.IsNullOrEmpty(zh))
                    parts.Add($"天气季节：{zh}（按天数轮换，每 {IntervalDays} 天换一季）");
            }
            else if (IsFixed)
            {
                var zh = WeatherSeasonZh(FixedSeason);
                parts.Add($"天气季节：{(string.IsNullOrEmpty(zh) ? FixedSeason : zh)}（指定，不随日期变化）");
            }
            else if (string.Equals(Mode, "off", StringComparison.OrdinalIgnoreCase))
                parts.Add("天气季节：不干预（按现实日期）");
            else
                parts.Add($"天气季节：跟活动（mode={Mode}）");

            // 活动季节
            // ★ v1.6.3 以前这里只会写"由勾选的活动决定"，而 rotate 模式其实已经强制开了某个活动季节
            //   ⇒ 面板头部与页脚自相矛盾（头部说没指定、页脚写着 CHRISTMAS(已开启)）。
            var applied = EventRotatorStatusRouter.AppliedEventSeason;
            if (IsEventSeasonFixed) parts.Add($"活动季节：{EventSeasonZh(EventSeasonVal)}（指定）");
            else if (!string.IsNullOrEmpty(applied)) parts.Add($"活动季节：{EventSeasonZh(applied)}（本季已开启）");
            else parts.Add("活动季节：由勾选的活动决定");

            return string.Join("　·　", parts);
        }
        catch { return ""; }
    }

    private static string WeatherSeasonZh(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        switch (s.Trim().ToUpperInvariant())
        {
            case "SPRING": return "春天";
            case "SUMMER": return "夏天";
            case "AUTUMN": return "秋天";
            case "WINTER": return "冬天";
            case "AUTUMN_LATE": return "深秋";
            case "SPRING_EARLY": return "早春";
            case "STORM": return "暴风雨";
            default: return s;
        }
    }

    private static string EventSeasonZh(string s)
    {
        if (string.IsNullOrEmpty(s)) return "—";
        switch (s.Trim().ToUpperInvariant())
        {
            case "HALLOWEEN": return "万圣节";
            case "CHRISTMAS": return "圣诞节";
            case "NEWYEARS": return "新年";
            case "APRILFOOLS": return "愚人节";
            case "PROMO": return "促销";
            default: return s;
        }
    }

    /// <summary>距离下一次换季还有几天（用于日志）。</summary>
    public static double DaysUntilNext()    {
        try
        {
            var epoch = new DateTime(2020, 1, 1);
            var days = (DateTime.Today - epoch).TotalDays;
            var next = (Math.Floor(days / Math.Max(1, IntervalDays)) + 1) * IntervalDays;
            return Math.Max(0, next - days);
        }
        catch { return 0; }
    }
}
