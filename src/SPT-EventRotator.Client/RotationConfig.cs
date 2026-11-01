using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EventRotator.Client;

/// <summary>
/// ★ F12 下拉框用的枚举（BepInEx ConfigurationManager 会把 enum 渲染成下拉列表）。
/// </summary>
public enum RotMode
{
    顺序轮换_sequential = 0,
    随机轮换_random = 1,
}

public enum SeasonMode
{
    跟活动_followEvent = 0,
    按天数轮换_rotate = 1,
    指定季节_fixed = 2,
    关_按现实日期_off = 3,
}

/// <summary>
/// ★★★ 2026-10-29【第 22 项 · 日期窗生效范围】服务端 `config.json` **顶层** `dateWindowRule`。
/// <list type="bullet">
/// <item><b>只在季节轮换关闭时生效</b>（默认）：只有『季节模式 = 关（按现实日期）』时，活动的日期窗才参与判定；
///   跟活动 / 按天数轮换 / 指定季节 一律不看日期窗（活动完全由轮换决定）。</item>
/// <item><b>始终按现实日期</b>：日期窗总是参与 = 一键回退到改动前的旧行为。</item>
/// </list>
/// ⚠ 不受它影响：手动勾选的活动（force.txt）永远生效；奖励轮换 / 季节轮换 / 主轮换 / 周抽签各自照旧。
/// </summary>
public enum DateWindowRuleE
{
    只在季节轮换关闭时生效_seasonOffOnly = 0,
    始终按现实日期_always = 1,
}

/// <summary>
/// ★ v2.6.0 天气季节（春夏秋冬）—— 落点 WeatherConfig.OverrideSeason。
/// ⚠ 注意区分：
///   · 这里选的是【天气季节】（下雪/落叶/绿草），对应 `configs/weather.json → overrideSeason`
///   · 万圣节/圣诞那种是【活动季节】，由活动自带的 seasonalEvent 字段驱动，不在这里选
/// </summary>
/// <summary>
/// ★ v0.1.2 【活动季节】模式 —— 独立于天气季节（万圣节/圣诞/新年/愚人节）。
/// 想开"同一天气季节下的不同活动季节"（比如万圣节 2022/2023/2024 各版本）就用这个。
/// </summary>
public enum EventSeasonModeE
{
    不动_off = 0,
    指定活动季节_fixed = 1,
}

/// <summary>★ 活动季节（SeasonalEventType）：万圣节 / 圣诞节 / 新年 / 愚人节。</summary>
public enum EventSeasonPick
{
    万圣节_HALLOWEEN = 0,
    圣诞节_CHRISTMAS = 1,
    新年_NEWYEARS = 2,
    愚人节_APRILFOOLS = 3,
}

public enum SeasonPick
{
    春天_SPRING = 0,
    夏天_SUMMER = 1,
    秋天_AUTUMN = 2,
    冬天_WINTER = 3,
    深秋_AUTUMN_LATE = 4,
    早春_SPRING_EARLY = 5,
    暴风雨_STORM = 6,
}

/// <summary>
/// ★ 轮换 / 季节 的 F12 配置（v2.1.10）。
///
/// 这些设置实际存在**服务端** <c>SPT-EventRotator\config.json</c> 里，
/// 客户端只负责读写它 —— 所以改完要**重启服务端**才生效（工具提示里已注明）。
/// </summary>
internal sealed class RotationConfig
{
    private readonly Plugin _p;
    private readonly ConfigFile _cfg;

    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<RotMode> Mode;
    public readonly ConfigEntry<int> IntervalDays;
    /// <summary>★ v0.1.3 换档时间基准（0~23 整点）。默认 4 = 凌晨 4 点换档。</summary>
    public readonly ConfigEntry<int> SlotRebaseHour;

    // ═══════════ ★ v0.1.4 季节活动触发阶段 ═══════════
    /// <summary>季节活动在季节内的开启阶段（off/start/middle/end）。</summary>
    public readonly ConfigEntry<SeasonPhaseE> SeasonEventPhase;
    public readonly ConfigEntry<int> DurHalloween, DurChristmas, DurNewyear, DurAprilFools;
    /// <summary>★ v0.1.4 每次轮换叠几个奖励活动。</summary>
    public readonly ConfigEntry<int> SmallCountMax;
    public readonly ConfigEntry<int> DurationDays;
    public readonly ConfigEntry<int> Count;
    public readonly ConfigEntry<bool> AvoidConflicts;
    public readonly ConfigEntry<bool> OnlyWhenNoSeasonal;

    public readonly ConfigEntry<SeasonMode> SeasonMode_;
    public readonly ConfigEntry<SeasonPick> SeasonPick_;
    /// <summary>
    /// ★★★ v0.1.6【用户要求：「指定季节这个加个可开启是否指定季节」】
    /// 开（默认）= 按『★指定季节』强制（= **现在的行为，逐字不变**）；
    /// 关        = **不强制指定季节**（服务端 `config.json → seasonRotation.mode` 会写成 `off`
    ///             ⇒ 服务端走 Plugin.cs:2932 的「不覆盖」分支，天气季节保持服务端原值/活动自带）。
    /// </summary>
    public readonly ConfigEntry<bool> ForceSeason;
    public readonly ConfigEntry<int> SeasonInterval;
    public readonly ConfigEntry<int> SeasonOffset;
    /// <summary>★ 天气季节 → 自动开对应活动季节（春→愚人节 / 秋→万圣 / 冬→圣诞）</summary>
    public readonly ConfigEntry<bool> SeasonAutoEvent;

    /// <summary>
    /// ★★★ 2026-10-29【第 22 项】日期窗生效范围 —— 服务端 config.json **顶层** `dateWindowRule`：
    /// <c>seasonOffOnly</c>（默认）| <c>always</c>（旧行为 / 回退）。
    /// 说明见 <see cref="DateWindowRuleE"/>。
    /// </summary>
    public readonly ConfigEntry<DateWindowRuleE> DateWindowRule_;

    /// <summary>
    /// ★★★ v1.3.9【接管原版季节窗】服务端 config.json → vanillaSeasonTakeover。
    /// 开（默认）= 关闭 SPT「按现实日期自动开官方节日」（万圣 / 圣诞 / 圣诞1月档 / 新年 / 愚人节 全部），节日由本 mod 调度；
    /// 关 = 保持原版行为（官方节日照旧在过，可能与轮换冲突）。
    /// </summary>
    public readonly ConfigEntry<bool> TakeoverVanillaSeason;

    // ★ v0.1.2 【活动季节】独立控制（万圣节/圣诞/新年/愚人节）
    public readonly ConfigEntry<EventSeasonModeE> EventSeasonMode_;
    public readonly ConfigEntry<EventSeasonPick> EventSeasonPick_;

    // ── ★ v2.2.0 奖励类轮换 ──
    public readonly ConfigEntry<bool> RewardEnabled;
    public readonly ConfigEntry<int> RewardMin;
    public readonly ConfigEntry<int> RewardMax;

    private const string CatReward = "【奖励类轮换】Rewards";
    private const string CatRot = "【轮换设置】Rotation";
    private const string CatSeason = "【季节轮换】天气季节 Weather Season";
    private const string CatEventSeason = "【活动季节】万圣/圣诞/新年/愚人节 Event Season";

    public RotationConfig(Plugin p, ConfigFile cfg)
    {
        _p = p;
        _cfg = cfg;

        Enabled = cfg.Bind(CatRot, "启用自动轮换 (enable)", false, Locale.D("开 = 服务端按【每档天数】自动换活动；关 = 只用下面勾选的那个活动（force.txt）。\n"
          + "⚠ 改完需重启服务端。",
        "On = the server auto-rotates events by 【interval days】; Off = only the event ticked below is used (force.txt).\n"
        + "⚠ Restart the server after changing this."));

        Mode = cfg.Bind(CatRot, "轮换模式 (mode)", RotMode.顺序轮换_sequential, Locale.D("顺序轮换 = 按 events.json 里的先后顺序依次来；随机轮换 = 每天伪随机抽一个（同一天固定，重启不变）。\n"
          + "★ 这是下拉框，点开直接选。",
        "Sequential = follow the order listed in events.json; Random = pick one pseudo-randomly per day (fixed for the whole day, unchanged by restarts).\n"
        + "★ This is a drop-down — open it and pick."));

        IntervalDays = cfg.Bind(CatRot, "每档持续天数 (intervalDays)", 3, Locale.D("一档活动持续多少天，到期自动换下一个。\n例：写 3 = 每 3 天换一个活动。\n"
          + "⚠ 改完需重启服务端（轮换在服务端启动时按时间预先算好，见下面『换档时间基准』）。",
        "How many days one event slot lasts before the next one starts automatically.\n"
        + "Example: 3 = a new event every 3 days.\n"
        + "⚠ Restart the server after changing this (the rotation is pre-computed from the clock when the server starts; see 『slot rebase hour』 below).\n\n"
        + "ℹ Two official events are **longer than a season** (Supply and Demand 60d / Shifting Balance 47d) and will legitimately span two seasons — an event sticking around after the season changes is expected, not a bug."));

        SlotRebaseHour = cfg.Bind(CatRot, "★ 换档时间基准（几点换）(slotRebaseHour)", 4, Locale.D("★ 换档发生在**每天的这个整点**（0~23）。默认 4 = 凌晨 4 点换档。\n\n"
          + "为什么默认 4 点而不是 0 点（午夜）：\n"
          + "   轮换是按【日期】分档的。如果 0 点换档，一个「晚上玩到 22 点、第二天早上再玩」的人\n"
          + "   会在两次游玩之间被换掉一档，体感像「我刚开的活动自己变了」。\n"
          + "   挪到凌晨 4 点后，正常作息（早/午/晚任意时段）的连续游玩都不会被换档打断。\n\n"
          + "另外：换档是**服务端启动时按时间预先算好的** —— 你启动服务端的那一刻就是新档位，\n"
          + "进游戏就是新活动，不需要「上游戏看到旧活动 → 再重启一次」。\n"
          + "⚠ 改完需重启服务端。",
        "★ The slot change happens at **this hour of every day** (0~23). Default 4 = change over at 04:00.\n"
        + "\n"
        + "Why 4 AM instead of 0:00 (midnight):\n"
        + "   Slots are cut by 【date】. If the switch were at midnight, someone who plays until 22:00\n"
        + "   and continues the next morning would lose a slot between the two sessions — it feels like\n"
        + "   「the event I just started changed by itself」.\n"
        + "   Moved to 04:00, continuous play at any normal hour (morning / noon / evening) is never interrupted by a slot change.\n"
        + "\n"
        + "Also: the slot is **pre-computed from the clock when the server starts** — the moment you start the server you are already\n"
        + "on the new slot, so you get the new event directly. No 「launch game, see the old event → restart again」 cycle needed.\n"
        + "⚠ Restart the server after changing this."));

        DurationDays = cfg.Bind(CatRot, "活动到期天数 (durationDays)", 0, Locale.D("【活动时间限定】只对**手动强制**（F12 勾选的那个）生效：\n"
          + "   0  = 永久生效（不限时）\n"
          + "   7  = 7 天后自动结束（回到轮换 / 无活动）\n"
          + "自动轮换模式下用上面的『每档持续天数』。\n"
          + "⚠ 改完需重启服务端。",
        "【Event time limit】only applies to the **manually forced** event (the one ticked in F12):\n"
        + "   0  = permanent (no time limit)\n"
        + "   7  = ends automatically after 7 days (back to rotation / no event)\n"
        + "In auto-rotation mode use 『interval days』 above instead.\n"
        + "⚠ Restart the server after changing this."));

        Count = cfg.Bind(CatRot, "每档活动数 (count)", 1, Locale.D("一次同时开几个活动（1 = 只开一个）。\n多开时靠下面『避免冲突』保证不打架。",
        "How many events run at the same time (1 = only one).\n"
        + "With several events, 『avoid conflicts』 below keeps them from clashing."));

        AvoidConflicts = cfg.Bind(CatRot, "避免冲突 (avoidConflicts)", true, Locale.D("自动排除会互相冲突的活动（比如两个都要独占同一张图的首领位）。",
        "Automatically skips events that conflict with each other (e.g. two events that both want the boss slot on the same map)."));

                    // ★ v1.0.0 合并：`★ 每次叠几个奖励活动 (rewardCount)` 这一项**已按用户裁决整项删除**   // ★ v1.0.0 合并（只改描述，不动绑定方式）

        OnlyWhenNoSeasonal = cfg.Bind(CatRot, "仅在季节间隙轮换", true, Locale.D(
        "开 = 只有在【季节活动】正在跑时才暂停填充轮换（普通日期窗活动不影响轮换）；\n"
        + "关 = 季节活动期间也照样叠加填充活动。\n\n"
        + "★ v1.4.0 语义修正：旧版是\"只要有任何一个活动在跑就暂停\"，全年有 64% 的天被误挡；现在只看季节活动。",
        "On = filler rotation pauses only while a SEASONAL event is running "
        + "(normal date-window events no longer block it);\n"
        + "Off = filler events stack on top even during a seasonal event.\n\n"
        + "v1.4.0 semantic fix: the old build paused on ANY active event (blocking ~64% of the year)."));

        // ─────────── 季节轮换（天气季节：春夏秋冬）───────────
        SeasonMode_ = cfg.Bind(CatSeason, "季节模式 (mode)", SeasonMode.跟活动_followEvent, Locale.D("★ 这里管的是【天气季节】= 春夏秋冬（下雪 / 落叶 / 绿草），不是万圣节那种活动季节。\n\n"
          + "跟活动      = 开哪个活动就切到它自带的天气季节（活动没写就不覆盖）\n"
          + "按天数轮换  = 不管活动，按下面『换季间隔天数』自动轮：春→夏→秋→冬 循环\n"
          + "指定季节    = 永远固定成下面『★指定季节』选的那个（春/夏/秋/冬）\n"
          + "关          = 不覆盖，完全按服务端原来的天气（默认）\n\n"
          + "生效方式：服务端启动时套用，可随时还原。⚠ 改完需重启服务端。",
        "★ This controls the 【weather season】= spring / summer / autumn / winter (snow / falling leaves / green grass), not event seasons like Halloween.\n"
        + "\n"
        + "Follow event   = switch to the weather season the event itself ships with (no override if the event defines none)\n"
        + "Rotate by days = ignore events and cycle spring→summer→autumn→winter using 『season interval days』 below\n"
        + "Fixed season   = always pin the season picked in 『★Fixed season』 below (spring/summer/autumn/winter)\n"
        + "Off            = no override, keep the server's original weather (default)\n"
        + "\n"
        + "Applied when the server starts; can be reverted anytime. ⚠ Restart the server after changing this."));

        SeasonPick_ = cfg.Bind(CatSeason, "★指定季节（春夏秋冬）(season)", SeasonPick.春天_SPRING, Locale.D("★ 下拉框，选【天气季节】：春天 / 夏天 / 秋天 / 冬天（另有 深秋 / 早春 / 暴风雨）。\n\n"
          + "只有『季节模式 = 指定季节』时生效。\n"
          + "效果 = 整张图变成那个季节的天气与植被（冬天下雪、秋天落叶、春夏绿草）。\n"
          + "生效方式：服务端启动时套用，可随时还原。\n\n"
          + "⚠ 万圣节 / 圣诞那种『活动季节』不在这里选 —— 那是活动自带的，勾对应活动即可。",
        "★ Drop-down; pick the 【weather season】: spring / summer / autumn / winter (also late autumn / early spring / storm).\n"
        + "\n"
        + "Only applies when 『season mode = fixed season』.\n"
        + "Effect = the whole map switches to that season's weather and vegetation (snow in winter, falling leaves in autumn, green grass in spring/summer).\n"
        + "Applied when the server starts; can be reverted anytime.\n"
        + "\n"
        + "⚠ Event seasons like Halloween / Christmas are NOT picked here — they come with the event itself, just tick that event."));

        // ★★★ v0.1.6【用户要求】「指定季节这个加个可开启是否指定季节」
        ForceSeason = cfg.Bind(CatSeason, "★启用指定季节 (force season)", true, Locale.D("★ 这一项管『★指定季节』**要不要真的生效**。\n\n"
          + "开（默认）= 按『★指定季节』选的值强制指定季节 = **你现在的行为，完全不变**。\n"
          + "关         = **不强制指定季节**：客户端会把服务端 config.json 的 `seasonRotation.mode` 写成 `off`，\n"
          + "             服务端随即走「不覆盖」分支（日志：『天气季节 → 不覆盖（还原默认）』），\n"
          + "             ⇒ 天气季节保持服务端原值 / 由活动自带的天气季节决定。\n\n"
          + "什么时候用：你只想让『季节模式』那种自动逻辑生效、又不想删掉自己选过的季节时，把这里关掉即可。\n"
          + "⚠ 只影响『季节模式 = 指定季节』这一种情况；其它模式（跟活动 / 按天数轮换 / 关）**一律不受本项影响**。\n"
          + "⚠ 关掉**不会**动你选的季节值（它还留着），下次打开立即恢复原来的指定。\n"
          + "⚠ 改完需重启服务端（和其它季节项一样）。",
        "★ This decides whether 『★Fixed season』**really takes effect**.\n"
        + "\n"
        + "On (default) = force the season picked in 『★Fixed season』 = **your current behavior, exactly as before**.\n"
        + "Off         = **do not force a season**: the client writes `off` into `seasonRotation.mode` of the server's config.json,\n"
        + "             and the server takes the 「no override」 branch (log: 『weather season → no override (restore default)』),\n"
        + "             ⇒ the weather season keeps the server's original value / is decided by the event's own weather season.\n"
        + "\n"
        + "When to use it: when you only want the automatic logic of 『season mode』 to run and do not want to delete the season you picked once — just turn this off.\n"
        + "⚠ Only affects 『season mode = fixed season』; the other modes (follow event / rotate by days / off) are **not affected by this item at all**.\n"
        + "⚠ Turning this off **does not** touch the season you picked (it is kept); turn it back on and the old selection applies immediately.\n"
        + "⚠ Restart the server after changing this (same as the other season items)."));

        SeasonInterval = cfg.Bind(CatSeason, "换季间隔天数 (intervalDays)", 30, Locale.D("『按天数轮换』时每个季节持续多少天。\n"
          + "   30  = 一个月一季（推荐）\n"
          + "   90  = 一季三个月\n"
          + "SPT 原生按现实日期是 10 月底万圣、12 月圣诞、1 月新年、4 月愚人节。",
        "How many days each season lasts in 『rotate by days』 mode.\n"
        + "   30  = one season per month (recommended)\n"
        + "   90  = one season per three months\n"
        + "Vanilla SPT follows real dates: Halloween in late October, Christmas in December, New Year in January, April Fools in April."));

        SeasonAutoEvent = cfg.Bind(CatSeason, "★进入季节自动开对应活动季节", true, Locale.D("开 = 天气季节到「秋」就自动开【万圣节】、到「冬」就自动开【圣诞节】、到「春」就开【愚人节】。\n"
          + "     这样你不用手动勾活动，轮换到哪个季节就自动带上那个季节的活动（含它的任务/僵尸/圣诞老人）。\n"
          + "关 = 只改天气，不动活动季节。\n\n"
          + "⚠ 【手动指定了活动就会忽略这个联动】—— 你在下面勾了任意活动（写 force.txt），活动季节就跟那个活动走。\n"
          + "⚠ 改完需重启服务端。",
        "On = weather season autumn auto-starts 【Halloween】, winter auto-starts 【Christmas】, spring auto-starts 【April Fools】.\n"
        + "     You then do not have to tick events by hand: whichever season the rotation reaches brings that season's event along (with its quests / zombies / Santa).\n"
        + "Off = only the weather changes, the event season is left alone.\n"
        + "\n"
        + "⚠ 【Manually forcing an event disables this link】— if you tick any event below (writes force.txt), the event season follows that event instead.\n"
        + "⚠ Restart the server after changing this."));

        // ★★★ 2026-10-29【第 22 项 · 日期窗生效范围】顶层 dateWindowRule
        DateWindowRule_ = cfg.Bind(CatSeason, "★日期窗生效范围 (dateWindowRule)", DateWindowRuleE.只在季节轮换关闭时生效_seasonOffOnly,
            Locale.D("★ 这一项管『活动自带的日期窗』（events.json 的 startMonth/startDay ~ endMonth/endDay）到底什么时候生效。\n\n"
              + "只在季节轮换关闭时生效（默认）\n"
              + "     = 只有『季节模式 = 关（按现实日期）』时，日期窗才参与判定；\n"
              + "       其它模式（跟活动 / 按天数轮换 / 指定季节）一律**不看**日期窗，活动完全由轮换 + 季节阶段决定。\n"
              + "始终按现实日期（回退）\n"
              + "     = 日期窗总是参与 = 一键回到改动前的旧行为（10/28 就开万圣节那种）。\n\n"
              + "⚠ 不受它影响：手动勾选的活动（force.txt）永远生效；奖励轮换 / 季节轮换 / 主轮换 / 周抽签各自照旧；\n"
              + "  『接管原版季节窗』（关掉官方按日期自动开节日）是另一套开关，与本项无关。\n"
              + "⚠ 改完需重启服务端。",
            "★ Controls when an event's own date window (events.json startMonth/startDay ~ endMonth/endDay) applies.\n\n"
              + "Only when season rotation is off (default)\n"
              + "     = date windows apply only when 『season mode = Off (follow the real date)』;\n"
              + "       in any other mode (follow the event / rotate by days / fixed) date windows are **ignored** and the rotation decides.\n"
              + "Always follow the real date (rollback)\n"
              + "     = date windows always apply (the old behaviour, e.g. Halloween opening on Oct 28).\n\n"
              + "⚠ Not affected: manually ticked events (force.txt) always apply; reward / season / main rotation and the weekly draw are unchanged;\n"
              + "  『take over vanilla season windows』 is a separate switch and unrelated to this one.\n"
              + "⚠ Restart the server after changing this."));

        TakeoverVanillaSeason = cfg.Bind(CatSeason, "★接管原版季节窗（关掉官方按日期自动开节日）", true,
            Locale.D("开（默认）= 由本 mod 全权调度节日：关闭 SPT 的『按现实日期自动开官方活动』。\n"
              + "     管的是**全部**官方季节窗：万圣节 10/28–11/9、圣诞 12/13–12/31、圣诞 1 月档 1/2–1/14、新年 1/1、愚人节 4/1。\n"
              + "     实测：不改的话 10/28 官方万圣节会自己开——实验室出现『仪式』撤离点、实验室免票，还会和我们轮换的节日撞车。\n"
              + "     落点：SPT_Data\\configs\\seasonalevents.json → enableSeasonalEventDetection（改前留 .bak-eventrotator 备份，可回退）。\n"
              + "关 = 保留原版行为（官方节日照旧在过）。\n\n"
              + "⚠ 改完需重启服务端（磁盘改动下次启动生效；内存那份本局就生效）。",
            "On (default) = this mod owns the calendar: disables SPT's date-based auto seasonal events for ALL of them\n"
              + "     (Halloween Oct 28-Nov 9, Christmas Dec 13-31, Christmas Jan Jan 2-14, New Year Jan 1, April Fools Apr 1).\n"
              + "     Where: SPT_Data\\configs\\seasonalevents.json -> enableSeasonalEventDetection (backed up as .bak-eventrotator).\n"
              + "Off = keep vanilla behaviour.\n\n"
              + "⚠ Restart the server after changing this."));

        // ═══════════ ★★★ v0.1.4 季节活动触发阶段 ═══════════
        SeasonEventPhase = cfg.Bind(CatSeason, "★★ 季节活动何时开启（季节内阶段）", SeasonPhaseE.不开_off, Locale.D("★ 这个决定【季节活动】在这一个季节里的**哪一段**开启。\n\n"
          + "不开        = 不自动开季节活动（想开自己勾，默认）\n"
          + "季节一开始就开 = 季节第 1 天就开（例如 30 天季节 → 第 1 天开）\n"
          + "季节过半才开  = 季节过一半才开，并**按活动的官方持续时长顺延**（30 天季节 + 万圣节 13 天 → 第 18 天开、开到季末）\n"
          + "季节收尾才开  = 同上，活动恰好压在季节末尾（体感上季节『先平静后热闹』）\n\n"
          + "▶ 官方持续时长（可在下面四项改）：万圣节 13 天（10/28–11/9）、圣诞 19 天（12/13–12/31）、"
          + "新年 2 天（12/31–1/1）、愚人节 1 天（4/1）。\n\n"
          + "⚠ 只要这里选了 non-不开 且开着『按天数轮换』，下面的『换季间隔天数』会被**自动抬到最低下限**"
          + "（= 最短的季节活动时长），避免「季节活动还没开完就换季」。想自由设天数就把这里设成『不开』。\n"
          + "⚠ 改完需重启服务端。",
        "★ This decides **which part** of a season the 【seasonal event】 starts in.\n"
        + "\n"
        + "Off             = do not start seasonal events automatically (tick them yourself; default)\n"
        + "At season start = start on day 1 of the season (e.g. a 30-day season → starts on day 1)\n"
        + "Mid-season      = start after half the season, **shifted by the event's official duration** (30-day season + 13-day Halloween → starts on day 18, runs to the end of the season)\n"
        + "Season end      = same as above, the event sits right at the end of the season (the season feels 『calm first, busy last』)\n"
        + "\n"
        + "▶ Official durations (editable in the four items below): Halloween 13 days (10/28–11/9), Christmas 19 days (12/13–12/31), New Year 2 days (12/31–1/1), April Fools 1 day (4/1).\n"
        + "\n"
        + "⚠ As long as this is anything but Off and 『rotate by days』 is on, 『season interval days』 below is **raised to its lower bound** (= the shortest seasonal event duration), so a season never ends before its event finished. Set this to 『Off』 if you want to choose the days freely.\n"
        + "⚠ Restart the server after changing this."));

        DurHalloween = cfg.Bind(CatSeason, "万圣节官方时长（天）", 13, Locale.D("本体万圣节日期窗 10/28–11/9 = 13 天。用于计算『季节过半/收尾才开』的开启日。",
        "Vanilla Halloween date window 10/28–11/9 = 13 days. Used to compute the start day for 『mid-season / season end』."));
        DurChristmas = cfg.Bind(CatSeason, "圣诞节官方时长（天）", 19, Locale.D("本体圣诞日期窗 12/13–12/31 = 19 天。",
        "Vanilla Christmas date window 12/13–12/31 = 19 days."));
        DurNewyear = cfg.Bind(CatSeason, "新年官方时长（天）", 1, Locale.D("本体新年日期窗 = 1/1 一天（最短的季节活动，也是最低下限的来源）。\n⚠ v1.3.9 修正：旧默认写的 2 天（按 12/31–1/1）与官方不符，已改为 1。",
        "Vanilla New Year window = Jan 1 only (1 day) - the shortest seasonal event and the source of the lower bound.\n⚠ Fixed in v1.3.9: the old default said 2 days (Dec 31-Jan 1), which did not match vanilla; now 1."));
        DurAprilFools = cfg.Bind(CatSeason, "愚人节官方时长（天）", 1, Locale.D("本体愚人节日期窗 4/1 = 1 天。",
        "Vanilla April Fools date window 4/1 = 1 day."));

        // ─────────── ★ v0.1.2 活动季节（独立分区）───────────
        EventSeasonMode_ = cfg.Bind(CatEventSeason, "活动季节模式 (mode)", EventSeasonModeE.不动_off, Locale.D("★ 【活动季节】= 万圣节 / 圣诞节 / 新年 / 愚人节（僵尸、圣诞老人、节日任务），独立于天气季节。\n\n"
          + "不动      = 不干预（默认）—— 活动季节由你勾选的活动自己决定\n"
          + "指定活动季节 = 强制开启下面选的那个活动季节\n\n"
          + "为什么要单独控制：同一个【天气】季节下可能想开【不同】的活动季节，\n"
          + "比如万圣节就有多个版本（2022 / 2023 / 2024 / 感染者 / 邪教之夜）—— 天气一样、活动不同。\n"
          + "⚠ 改完需重启服务端。",
        "★ 【Event season】= Halloween / Christmas / New Year / April Fools (zombies, Santa, holiday quests); independent of the weather season.\n"
        + "\n"
        + "Leave alone        = do not interfere (default) — the event season follows whatever event you ticked\n"
        + "Fixed event season = force the event season picked below\n"
        + "\n"
        + "Why control it separately: under one 【weather】 season you may want a 【different】 event season,\n"
        + "e.g. Halloween has several versions (2022 / 2023 / 2024 / infected / night of the cult) — same weather, different event.\n"
        + "⚠ Restart the server after changing this."));

        EventSeasonPick_ = cfg.Bind(CatEventSeason, "★指定活动季节 (season)", EventSeasonPick.万圣节_HALLOWEEN, Locale.D("★ 下拉框：万圣节 / 圣诞节 / 新年 / 愚人节。\n\n"
          + "只有『活动季节模式 = 指定活动季节』时生效。\n"
          + "选万圣节 → 僵尸 / 召唤 / 邪教任务全开；选圣诞节 → 圣诞老人 / 轮舞 / 圣诞任务全开。\n"
          + "生效方式：服务端启动时套用。\n\n"
          + "⚠ 想开万圣节的具体某个版本（2022/2023/2024/感染者），请直接在下面【① 季节活动】里勾那个活动。",
        "★ Drop-down: Halloween / Christmas / New Year / April Fools.\n"
        + "\n"
        + "Only applies when 『event season mode = fixed event season』.\n"
        + "Halloween → zombies / summons / cult quests all on; Christmas → Santa / khorovod / Christmas quests all on.\n"
        + "Applied when the server starts.\n"
        + "\n"
        + "⚠ To run one specific Halloween version (2022/2023/2024/infected), tick that event directly under 【① Seasonal events】 below."));

        SeasonOffset = cfg.Bind(CatSeason, "起始偏移 (offset)", 0, Locale.D("从轮换顺序的第几个开始。\n顺序固定为：万圣节 → 圣诞节 → 新年 → 愚人节。\n"
          + "写 0 = 从万圣节开始，写 1 = 从圣诞节开始，以此类推。",
        "Which index of the rotation order to start from.\n"
        + "The order is fixed: Halloween → Christmas → New Year → April Fools.\n"
        + "0 = start at Halloween, 1 = start at Christmas, and so on."));

        Hook(Enabled); Hook(Mode); Hook(IntervalDays); Hook(DurationDays); Hook(Count);
        Hook(SlotRebaseHour);   // ★ v0.1.3 换档时间基准
        Hook(SeasonEventPhase); Hook(DurHalloween); Hook(DurChristmas); Hook(DurNewyear); Hook(DurAprilFools);
        Hook(AvoidConflicts); Hook(OnlyWhenNoSeasonal);
        Hook(SeasonMode_); Hook(SeasonPick_); Hook(ForceSeason); Hook(SeasonInterval); Hook(SeasonOffset); Hook(SeasonAutoEvent);
        Hook(DateWindowRule_);   // ★ 2026-10-29【第 22 项】日期窗生效范围
        Hook(EventSeasonMode_); Hook(EventSeasonPick_);

        // ── 奖励类轮换：从 category=reward 的活动里随机开 N~M 个 ──
        RewardEnabled = cfg.Bind(CatReward, "启用奖励类轮换", true, Locale.D("开 = 每局从【④ 奖励类活动】里随机开若干个（只改玩家侧数值/掉落，不引入敌种，可叠加）。\n"
          + "关 = 不自动开奖励类活动（仍可手动勾选）。\n⚠ 改完需重启服务端。",
        "On = each raid randomly enables a few events from 【④ Reward events】(they only change player-side values / loot, add no new enemy types, and can stack).\n"
        + "Off = reward events are not enabled automatically (you can still tick them by hand).\n"
        + "⚠ Restart the server after changing this."));
        RewardMin = cfg.Bind(CatReward, "最少开几个 (countMin)", 1, Locale.D("随机开启的个数下限。0 = 允许一个都不开。",
        "Lower bound of how many are randomly enabled. 0 = none may be enabled."));
        RewardMax = cfg.Bind(CatReward, "最多开几个 (countMax)", 2, Locale.D("随机开启的个数上限。★ 会自动做冲突检查，不会开出互相打架的组合。",
        "Upper bound of how many are randomly enabled. ★ Conflict checks are automatic — no clashing combination is ever picked."));
        Hook(RewardEnabled); Hook(RewardMin); Hook(RewardMax);
    }

    /// <summary>任何一项变更 → 写回服务端 config.json。</summary>
    private void Hook<T>(ConfigEntry<T> e)
    {
        e.SettingChanged += (_, __) => { try { _p.ApplyRotationConfig(); } catch { } };
    }

    private static string ModeStr(RotMode m) => m == RotMode.随机轮换_random ? "random" : "sequential";

    /// <summary>★ 第 22 项：F12 枚举 → 服务端 config.json 顶层 <c>dateWindowRule</c> 字符串。</summary>
    private static string DateWindowRuleStr(DateWindowRuleE v)
        => v == DateWindowRuleE.始终按现实日期_always ? "always" : "seasonOffOnly";

    /// <summary>★ v0.1.4 季节活动触发阶段 → config 字符串。</summary>
    private static string SeasonPhaseStr(SeasonPhaseE p) => p switch
    {
        SeasonPhaseE.季节一开始就开_start => "start",
        SeasonPhaseE.季节过半才开_middle => "middle",
        SeasonPhaseE.季节收尾才开_end => "end",
        _ => "off",
    };

    private static SeasonPhaseE SeasonPhaseFromStr(string s) => (s ?? "off").Trim().ToLowerInvariant() switch
    {
        "start" => SeasonPhaseE.季节一开始就开_start,
        "middle" => SeasonPhaseE.季节过半才开_middle,
        "end" => SeasonPhaseE.季节收尾才开_end,
        _ => SeasonPhaseE.不开_off,
    };

    private static string SeasonModeStr(SeasonMode m) => m switch
    {
        SeasonMode.按天数轮换_rotate => "rotate",
        SeasonMode.指定季节_fixed => "fixed",
        SeasonMode.关_按现实日期_off => "off",
        _ => "followEvent",
    };

    /// <summary>
    /// ★★★ v0.1.6【启用指定季节】写 `config.json → seasonRotation.mode` 的最终取值（**纯函数，可单测**）。
    /// <para>· <paramref name="forceSeason"/> = 开（默认）⇒ 用户的『季节模式』**原样写出** ⇒
    ///   **与改动前逐字相同、零行为变化**（回归安全的关键）。</para>
    /// <para>· 关 ⇒ **只有**『季节模式 = 指定季节』这一种情况被降级成 <c>"off"</c>
    ///   （服务端 Plugin.cs:2932 走"不覆盖"分支，日志『天气季节 → 不覆盖（还原默认）』）；
    ///   其它模式（跟活动 / 按天数轮换 / 关）**原样写出** —— 它们本来就不使用『指定季节』，
    ///   不受本开关影响，**绝不越权改用户别的模式**。</para>
    /// </summary>
    internal static string SeasonModeForSave(bool forceSeason, SeasonMode picked)
        => (!forceSeason && picked == SeasonMode.指定季节_fixed) ? "off" : SeasonModeStr(picked);

    private static string EventSeasonPickStr(EventSeasonPick s) => s switch
    {
        EventSeasonPick.圣诞节_CHRISTMAS => "CHRISTMAS",
        EventSeasonPick.新年_NEWYEARS => "NEWYEARS",
        EventSeasonPick.愚人节_APRILFOOLS => "APRILFOOLS",
        _ => "HALLOWEEN",
    };

    private static string SeasonPickStr(SeasonPick s) => s switch
    {
        SeasonPick.夏天_SUMMER => "SUMMER",
        SeasonPick.秋天_AUTUMN => "AUTUMN",
        SeasonPick.冬天_WINTER => "WINTER",
        SeasonPick.深秋_AUTUMN_LATE => "AUTUMN_LATE",
        SeasonPick.早春_SPRING_EARLY => "SPRING_EARLY",
        SeasonPick.暴风雨_STORM => "STORM",
        _ => "SPRING",
    };

    /// <summary>把 F12 的值写进服务端 config.json（保留其它键）。</summary>
    /// <summary>★ v0.1.4 季节活动触发阶段。</summary>
    public enum SeasonPhaseE
    {
        不开_off,
        季节一开始就开_start,
        季节过半才开_middle,
        季节收尾才开_end,
    }

    /// <summary>★ v0.1.4 每次轮换叠几个奖励活动（显示用，实际语义见 config）。</summary>

        private const string DefaultConfigJson = "{\n"
            + "  \"logLevel\": \"basic\",\n"
            + "  \"sainPresetInject\": true,\n"
            + "  \"dateWindowRule\": \"seasonOffOnly\",\n"
            + "  \"rotation\": {\n"
            + "    \"enabled\": true,\n"
            + "    \"mode\": \"random\",\n"
            + "    \"intervalDays\": 7,\n"
            + "    \"slotRebaseHour\": 4,\n"
            + "    \"durationDays\": 0,\n"
            + "    \"count\": 1,\n"
            + "    \"seed\": 20261014,\n"
            + "    \"avoidConflicts\": true,\n"
            + "    \"onlyWhenNoSeasonal\": true,\n"
            + "    \"rewardCountMin\": 1,\n"
            + "    \"rewardCountMax\": 2,\n"
            + "    \"smallCountMax\": 2,\n"
            + "    \"pool\": []\n"
            + "  },\n"
            + "  \"seasonRotation\": {\n"
            + "    \"mode\": \"rotate\",\n"
            + "    \"intervalDays\": 60,\n"
            + "    \"offset\": 0,\n"
            + "    \"order\": [\n"
            + "      \"SPRING\",\n"
            + "      \"SUMMER\",\n"
            + "      \"AUTUMN\",\n"
            + "      \"WINTER\"\n"
            + "    ],\n"
            + "    \"autoOpenSeasonalEvent\": false,\n"
            + "    \"eventPhase\": \"middle\",\n"
            + "    \"seasonalEventDurations\": {\n"
            + "      \"HALLOWEEN\": 13,\n"
            + "      \"CHRISTMAS\": 19,\n"
            + "      \"NEWYEARS\": 2,\n"
            + "      \"APRILFOOLS\": 1\n"
            + "    },\n"
            + "    \"season\": \"SPRING\"\n"
            + "  },\n"
            + "  \"eventSeason\": {\n"
            + "    \"mode\": \"off\",\n"
            + "    \"season\": \"HALLOWEEN\"\n"
            + "  },\n"
            + "  \"rewardRotation\": {\n"
            + "    \"enabled\": true,\n"
            + "    \"countMin\": 1,\n"
            + "    \"countMax\": 2,\n"
            + "    \"avoidConflicts\": true,\n"
            + "    \"pool\": [],\n"
            + "    \"seed\": 0\n"
            + "  },\n"
            + "  \"autoPairs\": {},\n"
            + "  \"autoBossChance\": {},\n"
            + "  \"autoLootBoost\": {\n"
            + "    \"all_bosses_reserve\": 1.25,\n"
            + "    \"art_of_explosion\": 1.25,\n"
            + "    \"boss_cultist_presence\": 1.5,\n"
            + "    \"bosses_rogues_wander\": 1.25,\n"
            + "    \"bosses_take_a_hint\": 1.5,\n"
            + "    \"cultist_hunting\": 1.5,\n"
            + "    \"cultist_pilgrimage\": 1.5,\n"
            + "    \"factory_cleanup_crew\": 1.25,\n"
            + "    \"full_speed_ahead\": 1.25,\n"
            + "    \"geared_scavs\": 1.25,\n"
            + "    \"hustle\": 1.5,\n"
            + "    \"key_to_salvation\": 1.25,\n"
            + "    \"meeting_of_the_bosses\": 1.5,\n"
            + "    \"permanent_boss_presence\": 1.25,\n"
            + "    \"reshala_vs_glukhar\": 1.25,\n"
            + "    \"sanitar_event\": 1.25,\n"
            + "    \"scav_on_labs\": 1.25,\n"
            + "    \"scav_takeover\": 1.25,\n"
            + "    \"shifting_balance\": 1.5,\n"
            + "    \"smugglers_2024\": 1.5,\n"
            + "    \"targrad_tales\": 1.25,\n"
            + "    \"tarkov_hospitality\": 1.5,\n"
            + "    \"tarkov_mystery\": 1.25,\n"
            + "    \"terragroup_trail\": 1.25\n"
            + "  },\n"
            + "  \"mapFillersEnable\": false,\n"
            + "  \"mapFillers\": {},\n"
            + "  \"allowOnOverhaulMaps\": true,\n"
            + "  \"randomBossMapCount\": 1,\n"
            + "  \"randomBossMapChance\": 100,\n"
            + "  \"seasonAutoPick\": \"newest\",\n"
            + "  \"families\": {\n"
            + "    \"halloween\": {\n"
            + "      \"2022\": [\n"
            + "        \"halloween_2022\"\n"
            + "      ],\n"
            + "      \"2023\": [\n"
            + "        \"halloween_2023\"\n"
            + "      ],\n"
            + "      \"2024\": [\n"
            + "        \"halloween\",\n"
            + "        \"night_of_the_cult\"\n"
            + "      ],\n"
            + "      \"2025\": [\n"
            + "        \"softcore\"\n"
            + "      ]\n"
            + "    },\n"
            + "    \"christmas\": {\n"
            + "      \"2022\": [\n"
            + "        \"santa_2022\"\n"
            + "      ],\n"
            + "      \"2023\": [\n"
            + "        \"permanent_boss_presence\"\n"
            + "      ],\n"
            + "      \"2024\": [\n"
            + "        \"khorovod_2024\",\n"
            + "        \"cultist_pilgrimage\"\n"
            + "      ]\n"
            + "    }\n"
            + "  },\n"
            + "  \"familyGeneric\": {\n"
            + "    \"christmas\": [\n"
            + "      \"christmas\",\n"
            + "      \"christmas_january\",\n"
            + "      \"newyear\"\n"
            + "    ]\n"
            + "  },\n"
            + "  \"seasonBagNoRepeat\": true\n"
            + "}\n";

    public void Save()
    {
        var path = Path.Combine(_p.ModDirPublic, "config.json");
        JObject jo;
        try
        {
            // ★★★ v1.1.1【BugFix·F12 保存丢键】原来文件不存在时用 new JObject()，
            //   会把 autoLootBoost / families / seasonAutoPick / autoPairs 等“客户端不认识的键”全部丢掉（实测：全新安装后配置只剩 6 个键）。
            //   ⇒ 现在以完整默认配置为底座。
            jo = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : JObject.Parse(DefaultConfigJson);
        }
        catch (Exception ex)
        {
            // ★★★ C8【解析失败时 F12 一改就覆盖服务端配置】
            //   旧代码 catch 里 `jo = new JObject()` 然后**照常往下写** ⇒ 一个手改坏的 config.json
            //   （多一个逗号 / 注释 / BOM）会被 F12 的这次保存**整个覆盖**成"只有客户端认识的键"，
            //   用户的 families / familyGeneric / 服务端新增键全部丢失，且原文件没有任何备份。
            //   现在：只打 error 日志并**直接 return** —— 宁可不写，也绝不写坏用户配置。
            try { Plugin.Log?.LogError("[EventRotator.Client] config.json 解析失败，已放弃本次写入（不覆盖现有文件）: " + ex.Message); }
            catch { }
            return;
        }

        // ★刚才那种“已存在但缺键”的文件也要补齐：缺哪个顶层键就从默认配置拿哪个
        var defJo = JObject.Parse(DefaultConfigJson);
        foreach (var prop in defJo.Properties())
            if (jo[prop.Name] == null) jo[prop.Name] = prop.Value.DeepClone();

        var rot = jo["rotation"] as JObject ?? new JObject();
        rot["enabled"] = Enabled.Value;
        rot["mode"] = ModeStr(Mode.Value);
        rot["intervalDays"] = Math.Max(1, IntervalDays.Value);
        rot["slotRebaseHour"] = Math.Clamp(SlotRebaseHour.Value, 0, 23);   // ★ v0.1.3 换档时间基准
        // ★ v0.1.4 组合规则
        rot["durationDays"] = Math.Max(0, DurationDays.Value);
        rot["count"] = Math.Max(1, Count.Value);
        rot["avoidConflicts"] = AvoidConflicts.Value;
        rot["onlyWhenNoSeasonal"] = OnlyWhenNoSeasonal.Value;
        if (rot["pool"] == null) rot["pool"] = new JArray();
        jo["rotation"] = rot;

        var rr = jo["rewardRotation"] as JObject ?? new JObject();
        rr["enabled"] = RewardEnabled.Value;
        rr["countMin"] = Math.Max(0, RewardMin.Value);
        rr["countMax"] = Math.Max(RewardMin.Value, RewardMax.Value);
        rr["avoidConflicts"] = true;
        if (rr["seed"] == null) rr["seed"] = 0;
        if (rr["pool"] == null) rr["pool"] = new JArray();
        jo["rewardRotation"] = rr;

        var sr = jo["seasonRotation"] as JObject ?? new JObject();
        // ★★★ v0.1.6【启用指定季节】写出的 mode：
        //   · 开（默认）⇒ **与改动前逐字相同**：mode = 用户的『季节模式』（零行为变化，回归安全）
        //   · 关 ⇒ 若用户选的是『指定季节』就写 "off"（服务端 Plugin.cs:2932 走"不覆盖"分支，
        //          日志『天气季节 → 不覆盖（还原默认）』）；其它模式**原样写**（它们本来就不用『指定季节』，
        //          不受本开关影响，绝不越权改用户别的模式）。
        var seasonModeStr = SeasonModeForSave(ForceSeason.Value, SeasonMode_.Value);
        sr["mode"] = seasonModeStr;
        sr["season"] = SeasonPickStr(SeasonPick_.Value);   // ★ 关掉时**照旧保留**用户选的季节（下次打开即恢复）
        sr["intervalDays"] = Math.Max(1, SeasonInterval.Value);
        sr["offset"] = SeasonOffset.Value;
        sr["autoOpenSeasonalEvent"] = SeasonAutoEvent.Value;
        jo["vanillaSeasonTakeover"] = TakeoverVanillaSeason.Value;   // ★ v1.3.9 接管原版季节窗
        // ★★★ v0.1.4 季节活动触发阶段 + 各季节活动官方时长
        sr["eventPhase"] = SeasonPhaseStr(SeasonEventPhase.Value);
        sr["seasonalEventDurations"] = new JObject
        {
            ["HALLOWEEN"] = Math.Max(1, DurHalloween.Value),
            ["CHRISTMAS"] = Math.Max(1, DurChristmas.Value),
            ["NEWYEARS"] = Math.Max(1, DurNewyear.Value),
            ["APRILFOOLS"] = Math.Max(1, DurAprilFools.Value),
        };
        if (sr["order"] == null)
            sr["order"] = new JArray("SPRING", "SUMMER", "AUTUMN", "WINTER");
        jo["seasonRotation"] = sr;

        // ★ v0.1.2 活动季节（独立节点）
        var es = jo["eventSeason"] as JObject ?? new JObject();
        es["mode"] = EventSeasonMode_.Value == EventSeasonModeE.指定活动季节_fixed ? "fixed" : "off";
        es["season"] = EventSeasonPickStr(EventSeasonPick_.Value);
        jo["eventSeason"] = es;

        // ★★★ 2026-10-29【第 22 项】顶层 `dateWindowRule`（不放在 seasonRotation 里：跨模式的总开关）
        jo["dateWindowRule"] = DateWindowRuleStr(DateWindowRule_.Value);

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, jo.ToString(Formatting.Indented));
        _p.LogPublic($"config.json 已更新：轮换={Enabled.Value}({ModeStr(Mode.Value)}, {IntervalDays.Value}天/档, 到期{DurationDays.Value}天) "
                     + $"季节={SeasonModeStr(SeasonMode_.Value)}"
                     + (SeasonMode_.Value == SeasonMode.指定季节_fixed ? $"→{SeasonPickStr(SeasonPick_.Value)}" : $"({SeasonInterval.Value}天/季)"));
    }

    /// <summary>启动时从服务端 config.json 读回 F12 的值（不改文件）。</summary>
    public void Load()
    {
        try
        {
            var path = Path.Combine(_p.ModDirPublic, "config.json");
            if (!File.Exists(path)) return;
            var jo = JObject.Parse(File.ReadAllText(path));

            _p.SuspendCfg = true;
            var rot = jo["rotation"] as JObject;
            if (rot != null)
            {
                Enabled.Value = rot.Value<bool?>("enabled") ?? Enabled.Value;
                var m = (rot.Value<string>("mode") ?? "").ToLowerInvariant();
                if (m.Length > 0) Mode.Value = m.StartsWith("rand") ? RotMode.随机轮换_random : RotMode.顺序轮换_sequential;
                IntervalDays.Value = rot.Value<int?>("intervalDays") ?? IntervalDays.Value;
                SlotRebaseHour.Value = rot.Value<int?>("slotRebaseHour") ?? SlotRebaseHour.Value;   // ★ v0.1.3
                DurationDays.Value = rot.Value<int?>("durationDays") ?? DurationDays.Value;
                Count.Value = rot.Value<int?>("count") ?? Count.Value;
                AvoidConflicts.Value = rot.Value<bool?>("avoidConflicts") ?? AvoidConflicts.Value;
                OnlyWhenNoSeasonal.Value = rot.Value<bool?>("onlyWhenNoSeasonal") ?? OnlyWhenNoSeasonal.Value;
            }
            var rr2 = jo["rewardRotation"] as JObject;
            if (rr2 != null)
            {
                RewardEnabled.Value = rr2.Value<bool?>("enabled") ?? RewardEnabled.Value;
                RewardMin.Value = rr2.Value<int?>("countMin") ?? RewardMin.Value;
                RewardMax.Value = rr2.Value<int?>("countMax") ?? RewardMax.Value;
            }
            var sr = jo["seasonRotation"] as JObject;
            if (sr != null)
            {
                var m = (sr.Value<string>("mode") ?? "").ToLowerInvariant();
                SeasonMode_.Value = m switch
                {
                    "rotate" => SeasonMode.按天数轮换_rotate,
                    "fixed" => SeasonMode.指定季节_fixed,
                    "off" => SeasonMode.关_按现实日期_off,
                    _ => SeasonMode.跟活动_followEvent,
                };
                var s = (sr.Value<string>("season") ?? "").ToUpperInvariant();
                SeasonPick_.Value = s switch
                {
                    "SUMMER" => SeasonPick.夏天_SUMMER,
                    "AUTUMN" => SeasonPick.秋天_AUTUMN,
                    "WINTER" => SeasonPick.冬天_WINTER,
                    "AUTUMN_LATE" => SeasonPick.深秋_AUTUMN_LATE,
                    "SPRING_EARLY" => SeasonPick.早春_SPRING_EARLY,
                    "STORM" => SeasonPick.暴风雨_STORM,
                    _ => SeasonPick.春天_SPRING,
                };
                SeasonInterval.Value = sr.Value<int?>("intervalDays") ?? SeasonInterval.Value;
                SeasonOffset.Value = sr.Value<int?>("offset") ?? SeasonOffset.Value;
                SeasonAutoEvent.Value = sr.Value<bool?>("autoOpenSeasonalEvent") ?? SeasonAutoEvent.Value;
                TakeoverVanillaSeason.Value = jo.Value<bool?>("vanillaSeasonTakeover") ?? TakeoverVanillaSeason.Value;
                // ★ v0.1.4
                SeasonEventPhase.Value = SeasonPhaseFromStr(sr.Value<string>("eventPhase"));
                var sed = sr["seasonalEventDurations"] as JObject;
                if (sed != null)
                {
                    DurHalloween.Value = sed.Value<int?>("HALLOWEEN") ?? DurHalloween.Value;
                    DurChristmas.Value = sed.Value<int?>("CHRISTMAS") ?? DurChristmas.Value;
                    DurNewyear.Value = sed.Value<int?>("NEWYEARS") ?? DurNewyear.Value;
                    DurAprilFools.Value = sed.Value<int?>("APRILFOOLS") ?? DurAprilFools.Value;
                }
            }
            // ★ 2026-10-29【第 22 项】顶层 dateWindowRule（缺失/不认识 ⇒ 保持默认 seasonOffOnly）。
            //   放在 seasonRotation 块之外读取：它是跨模式的总开关，配置里没有 seasonRotation 节点时也该能读到。
            {
                var dwr = (jo.Value<string>("dateWindowRule") ?? "").Trim().ToLowerInvariant();
                if (dwr.Length > 0)
                    DateWindowRule_.Value = dwr == "always"
                        ? DateWindowRuleE.始终按现实日期_always
                        : DateWindowRuleE.只在季节轮换关闭时生效_seasonOffOnly;
            }
            var es2 = jo["eventSeason"] as JObject;
            if (es2 != null)
            {
                EventSeasonMode_.Value = (es2.Value<string>("mode") ?? "off").ToLowerInvariant() == "fixed"
                    ? EventSeasonModeE.指定活动季节_fixed : EventSeasonModeE.不动_off;
                EventSeasonPick_.Value = (es2.Value<string>("season") ?? "").ToUpperInvariant() switch
                {
                    "CHRISTMAS" => EventSeasonPick.圣诞节_CHRISTMAS,
                    "NEWYEARS" => EventSeasonPick.新年_NEWYEARS,
                    "APRILFOOLS" => EventSeasonPick.愚人节_APRILFOOLS,
                    _ => EventSeasonPick.万圣节_HALLOWEEN,
                };
            }
        }
        catch (Exception ex) { _p.LogPublic("读 config.json 失败: " + ex.Message); }
        finally { _p.SuspendCfg = false; }
    }
}
