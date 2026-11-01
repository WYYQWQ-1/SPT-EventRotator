using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Comfort.Common;
using CommonAssets.Scripts.Game;
using EFT;
using EFT.GlobalEvents;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace EventRotator.Client;

/// <summary>
/// ★★★ 【v0.1.6 新增】官方万圣节「杀了仪式祭司 ⇒ 仪式开始 ⇒ 只留 1 个撤离点」的**客户端**还原。
///
/// <para>【用户报的 bug】原本"不许打小鹿时撤离点正常，打了之后仪式开始才只剩 1 个"——
/// 服务端侧的完整测量见 <c>audit/撤离点-触发时机.md</c>：服务端**拿不到**「仪式开始」这个时刻
/// （整条链是客户端本地的，没有任何 HTTP 上报），而且撤离点在客户端**只在本局开战时读一次**
/// （<c>InitAllExfiltrationPoints</c> 只有 :668974 / :671580 两个调用点）⇒ 中途改服务端数据对局内无效。</para>
///
/// <para>【正解】客户端**自己就有官方实现**，0.16.9 里没人调（死代码）：
/// <c>ExfiltrationController.EventDisableAllExitsExceptOne(string exitNameToSave)</c>（Assembly-CSharp:521560-521579）——
/// 它遍历"当前还在的"撤离点：命中的那个 <c>Settings.ExfiltrationTime *= GlobalConfiguration.EventSettings.ExitTimeMultiplier</c>（自带 ×3），
/// 其余全部 <c>Status = EExfiltrationStatus.NotPresent</c>。所以本插件**只负责选点 + 调用**，不再自己乘倍率。</para>
///
/// <para>【触发点】<c>BotHalloweenEvent.StartRitual()</c>（Assembly-CSharp:156633，仪式开始的唯一入口）——
/// 它内部 `_ritualStarted = true` 后本地发 <c>HalloweenSummonStartedEvent</c>（:156640，
/// <c>CreateCommonEvent&lt;&gt;().Invoke()</c>，纯本地、无网络）。本插件挂**两条线**互为备份：
/// ① Harmony postfix <c>BotHalloweenEvent.StartRitual</c>（主，不依赖事件总线）；
/// ② <c>GlobalEventsController.Instance.SubscribeOnEvent&lt;HalloweenSummonStartedEvent&gt;</c>（备，官方 :568286 同款订阅）。
/// 两条线都进同一个幂等入口 <see cref="OnRitualStarted"/>，重复触发只会锁一次。</para>
///
/// <para>【选保留点】玩家自己的出生点 <c>Profile.Info.EntryPoint</c>（客户端在 :668973 写入）⇒ 在
/// <c>ExfiltrationController.Instance.ExfiltrationPoints</c> 里挑 <c>EligibleEntryPoints</c> 命中它的那个
/// （与客户端自己的过滤规则 :521601 一致，**不写死地图/撤离点名**：官方那套是"Crossroads 只属 boiler tanks 组、
/// ZB-1011 属 customs 组"，写死必然挑错）。</para>
///
/// <para>【不冲突】服务端默认"什么都不锁"（<c>exitKeepOnly</c> 没写 <c>@timing</c> 指令时）；
/// 若有人手动开了 <c>@timing=always</c>，本插件检测到"本局只剩 ≤1 个撤离点"就**跳过调用**，
/// 避免 <c>ExfiltrationTime</c> 被两次 ×3 叠加。</para>
/// </summary>
internal static class RitualExitLock
{
    private const string Tag = "[EventRotator.Client][撤离点]";

    /// <summary>★ F12 开关的**取值来源**（由 `Plugin.Awake` 注入 = 读 `Config` 的那一项）。
    /// 用 Func 而不是缓存 bool：F12 里改完**当前这一帧**就生效，也不会出现"插件拿到过期副本"。
    /// 未注入（理论上不会发生）时按"开"处理。</summary>
    internal static Func<bool> EnabledSource;

    /// <summary>当前是否启用「仪式开始 ⇒ 只留 1 个撤离点」（F12 默认开）。找不到类型/方法/事件时也只是 warning + 不动作，绝不影响游戏。</summary>
    public static bool Enabled
    {
        get { try { return EnabledSource == null || EnabledSource(); } catch { return true; } }
    }

    private static ManualLogSource _log;
    private static bool _installed;
    private static readonly HashSet<string> _warned = new HashSet<string>();

    // ── 每局状态（换局 = GameWorld 实例变了 → 全部重置）──
    private static object _raid;
    private static Action _unsub;
    private static bool _subscribed;
    private static float _nextSubTry;
    private static bool _lockedThisRaid;

    // ══ 条款②：june2022_toxin（June 2022 删档前 Day7「致命毒素」）官方「All extracts except one are locked」══
    //   与万圣节那条（仪式开始才锁）**触发时机不同**：这条是**进图即锁**，其余完全共用同一把官方刀（:521560）。
    //   官方原文（handover 的 events_wiki.txt:1636-1643）："* All extracts except one are locked."
    /// <summary>F12 开关取值来源（Plugin.Awake 注入，默认 true）。**只有在「致命毒素」活动生效时才真的锁**。</summary>
    internal static Func<bool> StartLockEnabledSource;
    /// <summary>当前是否允许"进图即锁"（F12，默认开）。</summary>
    public static bool StartLockEnabled { get { try { return StartLockEnabledSource == null || StartLockEnabledSource(); } catch { return true; } } }
    /// <summary>「致命毒素 june2022_toxin」此刻是否生效（EventsOverlay 每帧从服务端 `当前生效活动.txt` 的 `#事件|…|<id>|…` 推过来）。拿不到 = false ⇒ **不锁**（fail-safe）。</summary>
    public static bool ToxinActivityActive;
    /// <summary>
    /// ★★★ v0.1.6【用户裁决：该活动**只在海关生效**】本局地图是不是 `bigmap`（海关）。
    /// 由宿主组件 EventsOverlay 每帧推（判据 = `Singleton&lt;GameWorld&gt;.Instance.LocationId`，Assembly-CSharp:551349 / 官方同款用法 :435760）。
    /// ⚠ 默认 **false**（fail-safe）：拿不到地图 ⇒ **不锁撤离点**。
    /// ⚠ 只门控**条款②「进图即锁」**（<see cref="TickStartLock"/>）；万圣节那条"仪式开始才锁"**不受地图限制**。
    /// </summary>
    public static bool ToxinIsOnBigmap;

    /// <summary>
    /// ★★★ v0.1.7【用户裁决：**联机时就不锁撤离**】宿主每 0.25 s 推一次"是否真的在联机局里"
    /// （判据 = `Plugin.InCoopGame()`：装了 FIKA **且**在对局里，见 Plugin 里的两层信号）。
    /// true ⇒ <see cref="TickStartLock"/> 与 <see cref="OnRitualStarted"/> 都**只记录、不锁任何撤离点**。
    /// ⚠ 默认 false ⇒ **单机行为零变化**。
    /// </summary>
    public static bool CoopNoLock;
    /// <summary>
    /// ★ v0.1.6【三态之一】**地图是否已就绪**（GameWorld 建好 **且** `LocationId` 已赋值）。
    /// 未就绪 ⇒ <see cref="TickStartLock"/> **等待**（不动作、**不打"跳过"警告**）——
    /// 实测出现过"同一局早期被判成非目标图、之后又正常执行"的误报（用户当时就在海关）。
    /// </summary>
    public static bool ToxinMapKnown;
    /// <summary>地图未就绪的最长等待（秒）；超时后不再等，但**仍不动作**（fail-safe，绝不误锁）。</summary>
    private const float MapUnknownTimeoutSec = 30f;
    /// <summary>★ v0.1.7【Y3】"进图即锁"存在性轮询节流（0.25 s 一次）：轮询是**等待状态**，不是事件 ⇒ 晚 250 ms 无感、不漏触发
    /// （仪式那条线是 Harmony 回调 `OnRitualStarted`，不经过这里）。</summary>
    private static float _startLockPollAt;
    private static float _mapUnknownSince;

    /// <summary>
    /// ★★★ v0.1.6【修法 1 · 仪式锁活动门控】宿主是否**读到过**「当前生效活动」列表
    /// （`EventsOverlay.StatusFileTime > DateTime.MinValue`）。**false ⇒ 不锁**（fail-safe）。
    /// </summary>
    public static bool HalloweenActiveKnown;
    /// <summary>
    /// ★ v0.1.6【修法 1】当前生效的活动里**有没有万圣节季节活动**（`seasonalEvent == "Halloween"`）。
    /// 由宿主每帧推（原料 = 服务端 `当前生效活动.txt` 的生效 id + `Plugin.SeasonalEventById`，后者来自 events.json）。
    /// 只在 `HalloweenActiveKnown == true` 时有意义。
    /// </summary>
    public static bool HalloweenActive;

    // ══════════════ ★★★ v0.1.7【审计 P1-1】仪式锁门控的**超时降级**（与 ToxinMapKnown 的 30 秒机制对齐）══════════════
    //   修的问题：`HalloweenActiveKnown` 只有"false ⇒ 不锁"这一条路（RitualExitLock.cs:101 + EventsOverlay.cs:466），
    //   而它来自 `StatusFileTime > DateTime.MinValue` —— 只要服务端「当前生效活动.txt」读不到（没写/路径错/权限），
    //   它**恒为 false** ⇒ RitualGate 恒 "nolist" ⇒ OnRitualStarted **每局都不锁**，且只有一条 WarnOnce（静默永久失效）。
    //   ⇒ 现在：未就绪 **静默等**（最多 HalloweenUnknownTimeoutSec 秒）；超时 ⇒ 打**显式警告** + 本局不再等。
    //   ⚠ 语义**没变**：任何一态都**绝不"超时就锁"** —— 拿不到列表就不锁（fail-safe）。

    /// <summary>活动列表未就绪的最长等待（秒）。超时后打显式警告，但**仍不锁**（fail-safe，绝不误锁）。</summary>
    private const float HalloweenUnknownTimeoutSec = 30f;
    private static float _halloweenUnknownSince;

    /// <summary>
    /// ★★★ v0.1.7【审计 P1-1】仪式锁门控的**三态**（供 F12 显示 + 超时降级）：
    /// <list type="bullet">
    /// <item><c>0</c> = <b>等待活动列表</b>（还没读到 ⇒ 静默等，不锁）</item>
    /// <item><c>1</c> = <b>已就绪</b>（读到了 ⇒ 由 <see cref="HalloweenActive"/> 决定锁不锁）</item>
    /// <item><c>2</c> = <b>超时</b>（等满 <see cref="HalloweenUnknownTimeoutSec"/> 秒仍未读到 ⇒ 已打显式警告，本局不再等）</item>
    /// </list>
    /// </summary>
    public static byte HalloweenGateState { get; private set; }

    /// <summary>★ v0.1.7【审计 P1-1】仪式开始时列表还没就绪 ⇒ 记下来源，等列表就绪（或超时）后在 <see cref="TickClientGates"/> 里再定夺。null = 无待定仪式。</summary>
    private static string _pendingRitualSource;
    // ★★★ v1.0.1【BugFix】待定仪式**属于哪一局**。加载期 `Singleton<GameWorld>.Instance` 还是 null ⇒ 记 null，
    //   表示"这一局还没建好世界，先挂着"；一旦世界建好、且与记录的世界**不同** ⇒ 说明跨局了 ⇒ 丢弃。
    //   修的是：`StartRitual` 在进图加载期触发 → 待定；首个 Tick 时 `_raid == null` ⇒ 原代码不调 ResetRaid()
    //   ⇒ 待定逃过清理 → 在**下一局**被当成"本局仪式"执行 ⇒ 没杀小鹿撤离点却被封。
    private static object _pendingRitualGw;

    /// <summary>F12 只读显示用（人类可读）。★ v1.6.5【复核 #4】整串中文 ⇒ 英文界面直出，改走 Locale.T（中文逐字不变）。</summary>
    public static string HalloweenGateText
        => HalloweenGateState == 0 ? Locale.T("等待活动列表（未就绪 ⇒ 暂不锁）", "waiting for the event list (not ready ⇒ no locking yet)")
         : HalloweenGateState == 1 ? (HalloweenActive
                ? Locale.T("已就绪 · 有万圣节活动 ⇒ 会锁", "ready · Halloween event present ⇒ will lock")
                : Locale.T("已就绪 · 无万圣节活动 ⇒ 不锁", "ready · no Halloween event ⇒ will not lock"))
         : Locale.T("★超时（拿不到「当前生效活动」列表 ⇒ 本局不锁撤离点）",
                    "★timed out (cannot read 「active events」 ⇒ nothing is locked this raid)");

    /// <summary>
    /// ★★★ v0.1.7【审计 P1-1】每帧推进门控三态（与 <see cref="ToxinMapKnown"/> 的超时机制**对齐**）。
    /// </summary>
    /// <returns>true = 状态**刚**变为"已就绪"或"已超时"（调用方据此处理待定仪式）。</returns>
    private static bool TickHalloweenGate(float now)
    {
        if (HalloweenActiveKnown)
        {
            _halloweenUnknownSince = 0f;
            bool changed = HalloweenGateState != 1;
            HalloweenGateState = 1;
            return changed;
        }
        if (_halloweenUnknownSince <= 0f) _halloweenUnknownSince = now;
        if (now - _halloweenUnknownSince < HalloweenUnknownTimeoutSec)
        {
            HalloweenGateState = 0;                       // ★ 静默等（不动作、不打"跳过"警告）
            return false;
        }
        if (HalloweenGateState != 2)                      // ★ 首次超时 ⇒ **显式警告**（不是静默）
            _log?.LogWarning($"{Tag} ⚠ 仪式锁门控：连续 {HalloweenUnknownTimeoutSec:0} 秒拿不到服务端的「当前生效活动」列表 "
                           + $"⇒ 本局**不再等待**（也就**不会锁任何撤离点**，fail-safe）。"
                           + $"请检查：① 服务端是否已写出该文件；② F12 里「服务端 mod 目录」是否填对。");
        HalloweenGateState = 2;
        return true;
    }

    /// <summary>
    /// ★★★ v0.1.7【审计 P1-1】每帧推进"客户端门控"，并处理**被暂缓的仪式**。
    /// 为什么需要暂缓：<see cref="OnRitualStarted"/> 是**一次性回调**（Harmony postfix / 事件订阅），
    /// 列表晚到就永久错过 —— 旧版正是这样。现在改成"记下来，等列表就绪或超时后再定夺"。
    /// 任何异常都不外抛（绝不影响游戏）。
    /// </summary>
    private static void TickClientGates()
    {
        try
        {
            bool changed = TickHalloweenGate(UnityEngine.Time.realtimeSinceStartup);
            if (!changed || _pendingRitualSource == null) return;
            var src = _pendingRitualSource;
            _pendingRitualSource = null;                  // 先清，避免 OnRitualStarted 内部再入时重复
            _log?.LogInfo($"{Tag} 门控状态已确定（{HalloweenGateText}）⇒ 现在处理被暂缓的仪式（{src}）");
            OnRitualStarted(src);                         // 内部有门控 + _lockedThisRaid 双重保护，幂等
        }
        catch { }
    }
    /// <summary>撤离点是否已经过 `InitAllExfiltrationPoints`（:521467）——**早于它调用会被覆盖**（撤离点只在本局开战时读一次）。</summary>
    private static bool _exitsInit;
    private static float _exitsInitAt;
    private static bool _startLockDone;
    private static int _startLockTries;
    /// <summary>初始化完成后再等这么久才动手：让 `TimerPanel.SetTime`（:668990 附近）把面板建好、
    /// `OnStatusChanged` 订阅上 —— 否则状态变化发生在订阅之前，面板不会把被关的点标成 Locked。</summary>
    private const float StartLockDelaySeconds = 2f;

    /// <summary>Awake 调用一次：挂 Harmony 补丁（只改方法表，不跑游戏逻辑 ⇒ 不影响主界面/部署）。</summary>
    internal static void Install(ManualLogSource log)
    {
        _log = log;
        if (_installed) return;
        _installed = true;
        try
        {
            new RitualStartedPatch().Enable();
            _log?.LogInfo($"{Tag} ★已挂载 BotHalloweenEvent.StartRitual（Assembly-CSharp:156633，仪式开始的唯一入口）\n"
                        + $"{Tag}   行为：仪式开始 ⇒ 只留「玩家出生点对应的那一个」撤离点，其余关闭；该点撤离时间 ×EventSettings.ExitTimeMultiplier（官方实现自带，见 :521560）");
        }
        catch (Exception ex)
        {
            // 退化：只靠事件订阅（HalloweenSummonStartedEvent，:877998 / 本地触发点 :156640）
            _log?.LogWarning($"{Tag} ⚠ 挂载 BotHalloweenEvent.StartRitual 失败（不影响游戏，退化为只订阅仪式事件）：{ex.Message}");
        }
    }

    /// <summary>Update 每帧调用（内部极轻：一次单例取值 + 几个 bool）。</summary>
    internal static void Tick()
    {
        try
        {
            var gw = Singleton<GameWorld>.Instance;
            if (gw == null)
            {
                // 不在局内（主菜单 / 结算）→ 清理本局状态，下一局重来
                if (_raid != null) ResetRaid();
                return;
            }
            if (!ReferenceEquals(gw, _raid))
            {
                // ★★★ v1.0.1【BugFix】这里原来只在 `_raid != null` 时清，导致"首个 Tick 时 _raid 还是 null"
                //   这一路径下**待定仪式逃过清理**，跨局后被误当作本局仪式执行。
                //   现在按"待定仪式记录的归属世界"精确判断：
                //     · 归属世界为 null（加载期触发，世界还没建好）⇒ **保留**（这是本局的仪式，稍后补上，行为不变）
                //     · 归属世界非 null 且 ≠ 当前世界 ⇒ **跨局** ⇒ 丢弃（这才是真正的 bug 场景）
                if (_pendingRitualSource != null && _pendingRitualGw != null && !ReferenceEquals(_pendingRitualGw, gw))
                {
                    _log?.LogInfo($"{Tag} 待定仪式来自**上一局**（世界已变）⇒ 丢弃（不在本局锁撤离点）");
                    _pendingRitualSource = null;
                    _pendingRitualGw = null;
                }
                if (_raid != null) ResetRaid();
                _raid = gw;
                _log?.LogInfo($"{Tag} 进入新的一局 → 撤离点锁状态已重置（等仪式开始）");
            }
            if (_subscribed || !Enabled)
            {
                TickClientGates();      // ★ v0.1.7【审计 P1-1】推进仪式锁门控三态 + 处理被暂缓的仪式
                TickStartLockPoll();    // ★ 条款②：仪式事件订阅与否，都不影响"进图即锁"这条线（Y3：0.25 s 节流）
                return;
            }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now >= _nextSubTry)
            {
                _nextSubTry = now + 1f;
                TrySubscribe();
            }
            TickClientGates();          // ★ v0.1.7【审计 P1-1】推进仪式锁门控三态 + 处理被暂缓的仪式
            TickStartLockPoll();        // ★ 条款②：进图即锁（Y3：0.25 s 节流）
        }
        catch { }
    }

    /// <summary>
    /// ★ 条款②（june2022_toxin）：**进图即锁**。只有「致命毒素」活动生效 + F12 开关打开时才走这条路，
    /// 且必须等 `InitAllExfiltrationPoints`（:521467）跑完（撤离点只在本局开战时读一次，早调会被覆盖）。
    /// 失败可重试（最多 30 秒）；任何异常都吞掉，绝不影响游戏。
    /// </summary>
    /// <summary>★ v0.1.7【Y3】0.25 s 节流的轮询入口（**只节流轮询，不节流任何事件回调**）。</summary>
    private static void TickStartLockPoll()
    {
        try
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _startLockPollAt) return;
            _startLockPollAt = now + 0.25f;
            TickStartLock();
        }
        catch { }
    }

    private static void TickStartLock()
    {
        try
        {
            if (_startLockDone || !StartLockEnabled || !ToxinActivityActive) return;
            // ★ v0.1.7【用户裁决：联机时不锁撤离】联机局 ⇒ 只记一次日志、**不做任何锁点动作**
            if (CoopNoLock)
            {
                WarnOnce("coop", "联机（FIKA）：撤离点锁定已按用户裁决**自动关闭** ⇒ 本次不锁任何撤离点（进图即锁条款②）" + Plugin.FikaLogSuffix);
                _startLockDone = true;      // 本局不再重试（不锁就是不锁）
                return;
            }
            float now = UnityEngine.Time.realtimeSinceStartup;

            // ★★ v0.1.6【用户裁决：只在海关生效 · 三态】—— 未就绪 ⇒ **等待**（不动作、不打"跳过"）
            if (!ToxinMapKnown)
            {
                if (_mapUnknownSince <= 0f) _mapUnknownSince = now;
                if (now - _mapUnknownSince < MapUnknownTimeoutSec) return;
                WarnOnce("maptimeout", $"地图信息连续 {MapUnknownTimeoutSec:0} 秒仍未就绪 ⇒ 本局**不再等待**（也就**不锁任何撤离点**，fail-safe）；"
                                     + "若随后确实就绪且是海关，仍会照常处理" + Plugin.FikaLogSuffix);
                return;
            }
            _mapUnknownSince = 0f;
            // ★ 确实拿到地图、且不是 bigmap ⇒ 跳过 + 每局一条警告
            if (!ToxinIsOnBigmap)
            {
                WarnOnce("map", "「致命毒素」生效中，但本局地图不是『海关（bigmap）』⇒ 跳过「进图即锁」（用户口径：该活动只在海关生效）" + Plugin.FikaLogSuffix);
                return;
            }

            // ① 等"撤离点已初始化"：判据 = 至少一个点被 LoadSettings 填过 ExfiltrationTime>0
            //    （ExitTriggerSettings.ExfiltrationTime 无初始值 ⇒ 初始化前恒为 0；见 :521495-521501）
            if (!_exitsInit)
            {
                if (!ExitsLookInitialized()) return;
                _exitsInit = true;
                _exitsInitAt = now;
                _log?.LogInfo($"{Tag}[进图即锁] 「致命毒素」生效中 + 撤离点已初始化 ⇒ {StartLockDelaySeconds:0} 秒后按条款②处理");
            }
            if (now - _exitsInitAt < StartLockDelaySeconds) return;

            // ② 二次确认：还没初始化完就重新计时（防止任何"提前armed"的情况在初始化前动手）
            if (!ExitsLookInitialized()) { _exitsInitAt = now; return; }

            _startLockTries++;
            if (TryLockExits("进图即锁（致命毒素条款②）", "june2022_toxin"))
            {
                _startLockDone = true;
            }
            else if (now - _exitsInitAt > 30f)      // 玩家/出生点迟迟没就绪 ⇒ 本局放弃（绝不乱关）
            {
                _startLockDone = true;
                WarnOnce("startlock", "「进图即锁」重试 30 秒仍拿不到出生点/可用撤离点 ⇒ **本局放弃**（不改任何撤离点）" + Plugin.FikaLogSuffix);
            }
        }
        catch { }
    }

    /// <summary>撤离点是否已经过 `InitAllExfiltrationPoints`（:521467）——判据：至少一个点被 `LoadSettings` 填过 `ExfiltrationTime > 0`。</summary>
    private static bool ExitsLookInitialized()
    {
        try
        {
            var pts = ExfiltrationController.Instance?.ExfiltrationPoints;   // :521379 / :521395
            if (pts == null) return false;
            foreach (var p in pts)
                if (p != null && p.Settings != null && p.Settings.ExfiltrationTime > 0f) return true;
            return false;
        }
        catch { return false; }
    }

    /// <summary>备用触发线：官方订阅方式（HalloweenEventController.Init :568286 同款）。</summary>
    private static void TrySubscribe()
    {
        try
        {
            var events = GlobalEventsController.Instance;   // Assembly-CSharp:877220
            if (events == null) return;                    // 还没建好 → 下一秒再试
            _unsub = events.SubscribeOnEvent<HalloweenSummonStartedEvent>(OnSummonStartedEvent);   // :877309，返回退订 Action
            _subscribed = true;
            _log?.LogInfo($"{Tag} 已订阅 HalloweenSummonStartedEvent（备份触发线，:877998，本地由 StartRitual 发出 :156640）");
        }
        catch (Exception ex) { WarnOnce("sub", "订阅仪式事件失败: " + ex.Message); }
    }

    private static void OnSummonStartedEvent(HalloweenSummonStartedEvent e) => OnRitualStarted("HalloweenSummonStartedEvent");

    /// <summary>★ 入口①（幂等）：仪式开始了 ⇒ 只留 1 个撤离点。（万圣节线；条款②走 <see cref="TickStartLock"/>）</summary>
    internal static void OnRitualStarted(string source)
    {
        try
        {
            if (!Enabled)
            {
                _log?.LogInfo($"{Tag} 仪式开始（{source}）→ F12 里本功能已关闭，不动撤离点");
                return;
            }

            // ★ v0.1.7【用户裁决：联机时不锁撤离】联机局 ⇒ 只记一次日志、不锁（也不走下面的门控判定）
            if (CoopNoLock)
            {
                WarnOnce("coop-ritual", $"仪式开始（{source}）：联机（FIKA）⇒ 撤离点锁定已按用户裁决**自动关闭**，不锁任何撤离点" + Plugin.FikaLogSuffix);
                return;
            }

            // ★★★ v0.1.6【修法 1 · 活动门控】只有"当前生效的活动里存在万圣节季节活动（SeasonalEvent=Halloween）"才锁。
            //   为什么：`Install()` 是无条件挂载的（只要游戏起了仪式就回调）⇒ 以前**任何一局**只要仪式触发就会锁 ✗
            //   （隐患：非万圣节活动局也可能被锁）。门控后：真万圣节局照旧锁；`june2022_toxin` 那种局不锁；
            //   **拿不到活动列表 ⇒ 不锁**（fail-safe，与"进图即锁"同款）。
            // ★★★ v0.1.7【审计 P1-1】门控加**超时降级 + 暂缓重试**：
            //   旧版是"列表没就绪 ⇒ 当场 WarnOnce + return" ⇒ 而本方法是**一次性回调**（Harmony postfix / 事件订阅）
            //   ⇒ 列表晚到就**永久错过**；叠加 `HalloweenActiveKnown` 可能恒 false ⇒ 仪式锁全盘静默失效。
            //   现在：未就绪且**还没超时** ⇒ 记成待定，由 TickClientGates() 在列表就绪或超时后再定夺（静默等）；
            //         已超时 ⇒ 打显式警告并放弃（fail-safe，**绝不"超时就锁"**）。
            switch (RitualGate(HalloweenActiveKnown, HalloweenActive))
            {
                case "nolist":
                    if (HalloweenGateState == 2)
                    {
                        WarnOnce("ritual-timeout", $"仪式开始（{source}）：门控**已超时**（连续 {HalloweenUnknownTimeoutSec:0} 秒拿不到「当前生效活动」列表）"
                                                 + $" ⇒ 不锁撤离点（fail-safe，避免误锁非万圣节局）");
                        return;
                    }
                    _pendingRitualSource = source;   // ★ 暂缓：等列表（最多 HalloweenUnknownTimeoutSec 秒）
                    try { _pendingRitualGw = Singleton<GameWorld>.Instance; } catch { _pendingRitualGw = null; }   // ★ 记归属局
                    _log?.LogInfo($"{Tag} 仪式开始（{source}）：活动列表还没就绪 ⇒ **暂缓**（等列表，最多 {HalloweenUnknownTimeoutSec:0} 秒；就绪后再定夺）");
                    return;
                case "notHalloween":
                    WarnOnce("ritual-noxmas", $"仪式开始（{source}）：当前生效的活动里**没有万圣节季节活动**（seasonalEvent=Halloween）⇒ 不锁撤离点"
                                            + "（该功能只服务万圣节局；`june2022_toxin` 这类局不再受影响）");
                    return;
            }
            _pendingRitualSource = null;         // ★ 门控已通过 ⇒ 清掉待定标记

            if (_lockedThisRaid)
            {
                _log?.LogInfo($"{Tag} 仪式开始（{source}）→ 本局已经锁过 ⇒ 跳过（幂等，避免 ×3 叠加）");
                return;
            }
            if (TryLockExits("仪式开始", source)) _lockedThisRaid = true;
        }
        catch (Exception ex) { WarnOnce("core", "处理仪式事件异常（游戏不受影响）: " + ex); }
    }

    /// <summary>
    /// ★★★ v0.1.6【修法 1】仪式锁的**门控结论**（纯函数 · 可单测）：
    /// <list type="bullet">
    /// <item><c>"nolist"</c> = 还没读到过「当前生效活动」列表 ⇒ **不锁**（fail-safe）</item>
    /// <item><c>"notHalloween"</c> = 列表读到了，但里面**没有** `seasonalEvent == "Halloween"` 的活动 ⇒ **不锁**</item>
    /// <item><c>"ok"</c> = 有万圣节季节活动在跑 ⇒ **照旧锁**</item>
    /// </list>
    /// </summary>
    internal static string RitualGate(bool listKnown, bool halloweenActive)
        => !listKnown ? "nolist" : (halloweenActive ? "ok" : "notHalloween");

    /// <summary>
    /// ★ v0.1.6【修法 1】生效活动里有没有万圣节季节活动（纯函数 · 可单测）。
    /// </summary>
    internal static bool HasHalloween(IEnumerable<string> activeIds, IDictionary<string, string> seasonalEventById)
    {
        try
        {
            if (activeIds == null || seasonalEventById == null) return false;
            foreach (var id in activeIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (seasonalEventById.TryGetValue(id.Trim(), out var s)
                    && string.Equals(s, "Halloween", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// ★★ 共用核心（万圣节"仪式开始"与条款②"进图即锁"都走这里）：只留「玩家出生点对应的那一个」撤离点，
    /// 其余关掉 —— 调官方 <c>ExfiltrationController.EventDisableAllExitsExceptOne</c>（:521560，自带 ×ExitTimeMultiplier）。
    /// </summary>
    /// <returns>true = 本次已处理完（成功 / 判定"无需处理、不能处理"）；false = 条件还没就绪，**可稍后重试**。</returns>
    private static bool TryLockExits(string what, string source)
    {
        var ctl = ExfiltrationController.Instance;      // :521379 = Singleton<GameWorld>.Instance.ExfiltrationController
        if (ctl == null) { WarnOnce("noctl", "ExfiltrationController.Instance == null ⇒ 不动撤离点"); return false; }
        var pts = ctl.ExfiltrationPoints;               // :521395
        if (pts == null || pts.Length == 0) { WarnOnce("nopts", "ExfiltrationPoints 为空 ⇒ 不动撤离点"); return false; }

        var present = pts.Where(p => p != null && p.Settings != null && p.Status != EExfiltrationStatus.NotPresent).ToArray();
        string entry = EntryPoint();
        bool singleIsTarget = present.Length == 1 && present[0] != null && present[0].Settings != null
                              && !string.IsNullOrEmpty(entry) && EligibleFor(present[0], entry);

        if (present.Length <= 1)
        {
            // 本局已经只剩 ≤1 个（服务端 @timing=always / 另一条线先锁的 / 本来就没点）⇒ 绝不再调
            // （再调一次会把 `ExfiltrationTime` 又乘一遍 ×倍率）
            if (present.Length == 1 && singleIsTarget)
                _log?.LogInfo($"{Tag} {what}（{source}）→ 本局只剩 '{present[0].Settings.Name}'，正是出生点 {entry} 对应的那个 ⇒ 判定已锁，跳过调用（不叠加 ×{Multiplier():0.##}）");
            else
                _log?.LogWarning($"{Tag} {what}（{source}）→ 本局「还在的」撤离点只有 {present.Length} 个（{(present.Length == 1 ? "'" + present[0].Settings.Name + "'" : "无")}，出生点 {entry}）⇒ 判定已被锁过或无点可留，**跳过调用**（防 ×3 叠加，也防把最后一个也关掉）");
            return true;
        }

        if (string.IsNullOrEmpty(entry))
        {
            // ★★★ v0.1.6【诊断日志 · Lead 批准】只在**这条**分支打（走 WarnOnce ⇒ 每局最多一次、不刷屏）；
            //   纯读纯日志、**不改变任何行为**。上一次复现时日志区分不了"哪一层没值"，这次一次打全。
            WarnOnce("noentry", "拿不到出生点 Profile.Info.EntryPoint ⇒ 本次不动撤离点（无法判断该留哪一个）。" + EntryPointDiag());
            return false;
        }

        // ① 与客户端自己的规则完全一致（:521601 `x.EligibleEntryPoints.Contains(entryPointName)`）
        var cands = pts.Where(p => IsUsable(p) && EligibleFor(p, entry)).ToArray();
        string why = "EligibleEntryPoints";
        // ② 兜底：用客户端自己的过滤函数（等价规则，另加 Chance>0；:521590）
        if (cands.Length == 0)
        {
            try
            {
                var profile = Singleton<GameWorld>.Instance?.MainPlayer?.Profile;
                var el = ctl.EligiblePoints(profile);   // :521590
                if (el != null) { cands = el.Where(p => IsUsable(p) && EligibleFor(p, entry)).ToArray(); why = "EligiblePoints(profile) 兜底"; }
            }
            catch (Exception ex) { WarnOnce("eligible", "EligiblePoints(profile) 兜底失败: " + ex.Message); }
        }
        if (cands.Length == 0)
        {
            WarnOnce("nopick", $"找不到出生点 {entry} 对应的可用撤离点 ⇒ **不改任何撤离点**（宁可不做，也不能把点全关掉）。"
                             + $" 当前还在的点：{string.Join(" / ", present.Select(x => x.Settings.Name))}");
            return true;
        }

        // ★★★ v1.6.6【P1 防困 · 通行条件档位】不能把玩家锁在"唯一一个进不去的点"上。
        //   【旧问题】候选只按 `IsUsable && EligibleFor` 过滤，而 `EligibleFor` 只看"在不在 + 时间>0"；
        //   `Rank` 只是排序偏好 ⇒ 若同组唯一候选是「要钥匙 / 要付款 / 要倒计时 / 世界事件」的点，
        //   就会出现"其余全关、只留一个用不了的点" = 玩家困在图里（只能自杀或等局终）。
        //   【服务端早有这层】`ExtraDimensions.ExitUsableTier`：3=无条件 / 2=空槽 / 1=有阻塞，
        //   且 `tier < 2` 一律不当保留点（原注释：否则玩家可能撤不出去）。客户端这次补上同一层。
        //   【判据】① 运行态必须是 `RegularMode`（`UncompleteRequirements`=条件未满足、
        //              `AwaitsManualActivation`=需手动激活、`Countdown`=正在倒计时撤离、
        //              `Pending`=尚未定稿、`Hidden/NotPresent`=不存在 ⇒ 都不选）；
        //           ② 数据层用 <see cref="ExitUsableTier"/> 分档（`Requirements` 由 `LoadSettings` 按
        //              `PassageRequirement` 建好：`None`=空数组=无条件；`Empty`=空槽；其它=有阻塞）。
        var regular = cands.Where(p => p.Status == EExfiltrationStatus.RegularMode).ToArray();
        var tier3 = regular.Where(p => ExitUsableTier(p) >= 3).ToArray();
        var tier2 = regular.Where(p => ExitUsableTier(p) >= 2).ToArray();
        ExfiltrationPoint[] usable = tier3.Length > 0 ? tier3 : tier2;
        int usableTier = tier3.Length > 0 ? 3 : (tier2.Length > 0 ? 2 : 0);
        if (usable.Length == 0)
        {
            // ★★★ 与 :525-528「宁可不做」同款保守策略：一个"无条件可用"点都没有 ⇒ **本局不锁**。
            _log?.LogInfo($"{Tag} {what}（{source}）→ 出生点 {entry} 的候选 {cands.Length} 个里"
                        + "**没有一个「无条件可用」的撤离点**"
                        + $"［{string.Join(" / ", cands.Select(x => x.Settings.Name + "=" + x.Status + "·" + ExitUsableTier(x) + "档"))}］"
                        + " ⇒ **本次不锁任何撤离点**（宁可少锁一次，也不把玩家锁到要钥匙/要付款/要倒计时的点上）");
            return true;    // 已处理（本局不再重试），且一个点都没动
        }

        // 选点：优先"现在就能用"的（同档保持数组原序 ⇒ 同类出生点结果稳定可复现）
        var pick = usable.OrderBy(Rank).First();
        string name = pick.Settings.Name;
        float before = pick.Settings.ExfiltrationTime;

        // ★★★ v0.1.6【修法 2 · 把"绝不全关"从"构造性保证"升级为**显式断言**】
        //   官方 `EventDisableAllExitsExceptOne(name)` 的语义 = 「保留 name、关掉其余」⇒ **只有 name 确实"当前还在"时才安全**
        //   （否则会把所有还在的点都关掉、一个不剩 = 用户最怕的"没地方撤离"）。
        //   我们本来就是从 `present` 里挑的（`IsUsable` 要求 Status != NotPresent）⇒ 这是**双保险**，不满足就一个点都不动。
        if (present.Length < 2 || !present.Any(p => p == pick))
        {
            _log?.LogWarning($"{Tag} {what}（{source}）→ ⚠ 判定**不可安全锁点**"
                           + $"（present={present.Length}，pick='{name}' 不在 present 里？{!present.Any(p => p == pick)}）"
                           + $" ⇒ **跳过，不改任何撤离点**（绝不允许「全部关闭」）");
            return true;    // 已处理（本局不再重试），且一个点都没动
        }

        // ★★ 调官方实现（:521560）：命中点 ×倍率，其余全部 NotPresent
        ctl.EventDisableAllExitsExceptOne(name);

        float after = pick.Settings.ExfiltrationTime;
        // ★ 计时面板**不需要我们刷**（也就不必为此引用 Sirenix/Odin）：
        //   `item.Status = NotPresent` 会走 ExfiltrationPoint.Status 的 setter（:909943）
        //   → :909963 `Disable(_status)` → :909975 `OnStatusChanged?.Invoke(...)`
        //   → 战局类 OnStatusChangedHandler（:668928）→ UpdateExfiltrationUi（:561603）
        //   → `TimerPanel.UpdateExfiltrationTimers(..., point.Status)`（:561635）
        //   → ExtractionTimersPanel.UpdateExfiltrationTimers（:942004）在 :942024 把该点标成 `Locked`。
        //   （官方另有 :568347 / :568444 两处 ForceUpdateExfiltrationPointsVisitedStatus()，那是**服务端下发**
        //     路径用的；我们走本地直接调用，状态变化已经带出 UI 更新。）

        _log?.LogInfo($"{Tag} {what} ⇒ 只留 '{name}'（出生点 {entry}），撤离时间 {before:0.#}s → {after:0.#}s"
                    + $"（×{Multiplier():0.##}）；其余 {present.Length - 1} 个撤离点已关"
                    + $"［触发={source}，选点依据={why}，候选 {cands.Length} 个：{string.Join(" / ", cands.Select(x => x.Settings.Name))}"
                    + $"，通行档位={usableTier}档（3=无条件 / 2=空槽）］");
        return true;
    }

    /// <summary>玩家自己的出生点（客户端在 :668973 写 Profile.Info.EntryPoint）。</summary>
    private static string EntryPoint()
    {
        try
        {
            var prof = Singleton<GameWorld>.Instance?.MainPlayer?.Profile;   // GameWorld.MainPlayer :551096
            var ep = prof?.Info?.EntryPoint;
            return string.IsNullOrEmpty(ep) ? "" : ep.Trim().ToLowerInvariant();
        }
        catch { return ""; }
    }

    /// <summary>该撤离点是否"可用且值得留"：有名、还在、时间 > 0（secretExits 不走这里，它们是另一套数组）。</summary>
    private static bool IsUsable(ExfiltrationPoint p)
        => p != null && p.Settings != null
           && !string.IsNullOrEmpty(p.Settings.Name)
           && p.Status != EExfiltrationStatus.NotPresent
           && p.Settings.ExfiltrationTime > 0f;

    /// <summary>与客户端自己的规则一致（:521601 / :910028 都是 EligibleEntryPoints.Contains(EntryPoint.ToLower())）。</summary>
    private static bool EligibleFor(ExfiltrationPoint p, string entry)
    {
        var arr = p.EligibleEntryPoints;
        if (arr == null || arr.Length == 0) return false;
        foreach (var x in arr)
            if (string.Equals(x, entry, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// ★★★ v1.6.6【P1 防困】撤离点"能不能当那个唯一保留点"的**通行条件档位** ——
    /// 服务端 <c>ExtraDimensions.ExitUsableTier</c>（:602-609）的客户端等价实现：
    /// <list type="bullet">
    /// <item><b>3 = 无条件</b>：`Requirements` 为空（= `PassageRequirement=None`，官方 `LoadSettings` 此时建空数组）</item>
    /// <item><b>2 = 空槽档</b>：只有 `EmptySlotRequirement`（`ERequirementState.Empty`，玩家腾个空位就能走；
    ///       只在没有任何 3 档候选时才用，与服务端"先 3 后 2"一致）</item>
    /// <item><b>1 = 有阻塞</b>：要物品/钥匙/付款（`TransferItem`）、世界事件、列车、合作、技能、定时、引用 …</item>
    /// </list>
    /// <para>依据 `Assembly-CSharp` 的 `ExfiltrationPoint.LoadSettings`（`ExfiltrationRequirement.CreateRequirement`）
    /// 与 `ExfiltrationRequirement.Requirement` 字段；读不到（异常/未初始化）⇒ 按 1 档处理
    /// （**fail-safe：不选它**，宁可不锁也不锁到用不了的点）。</para>
    /// </summary>
    private static int ExitUsableTier(ExfiltrationPoint p)
    {
        try
        {
            if (p == null) return 1;
            var req = p.Requirements;
            if (req == null || req.Length == 0) return 3;
            bool onlyEmpty = true;
            foreach (var r in req)
            {
                if (r == null) continue;
                if (r.Requirement != ERequirementState.Empty) { onlyEmpty = false; break; }
            }
            return onlyEmpty ? 2 : 1;
        }
        catch { return 1; }
    }

    /// <summary>同档取数组原序 ⇒ 结果稳定。优先"现在就能用"的点。</summary>
    private static int Rank(ExfiltrationPoint p)
    {
        switch (p.Status)
        {
            case EExfiltrationStatus.RegularMode: return 0;
            case EExfiltrationStatus.AwaitsManualActivation: return 1;
            case EExfiltrationStatus.UncompleteRequirements: return 2;
            case EExfiltrationStatus.Countdown: return 3;
            case EExfiltrationStatus.Pending: return 4;
            case EExfiltrationStatus.Hidden: return 5;
            default: return 6;
        }
    }

    private static float Multiplier()
    {
        try { return Singleton<GlobalConfiguration>.Instance.EventSettings.ExitTimeMultiplier; }
        catch { return 1f; }
    }

    /// <summary>
    /// ★★★ v0.1.6【纯诊断 · Lead 批准】"出生点拿不到"时打一条，把**每个可能来源**的值都摊开：
    /// <list type="number">
    /// <item><c>GameWorld.MainPlayer.Profile.Info.EntryPoint</c>（我们当前用的路径；GameWorld.MainPlayer :551096）</item>
    /// <item><c>GamePlayerOwner.MyPlayer.Profile.Info.EntryPoint</c>（官方同款用法 Assembly-CSharp:521553；该静态属性 :680156）</item>
    /// <item>本地局内部的 <c>_entryPoint</c> 字段（<c>spawnPoint.Infiltration</c> 的落点，Assembly-CSharp:668758 → :668973；
    ///   通过 <c>Singleton&lt;AbstractGame&gt;.Instance</c>（官方用法 :56560/:148407）反射读取，沿基类链找私有字段）</item>
    /// <item><c>ExfiltrationController.EligiblePoints(profile)</c> 的命中数与"可用"数（:521590，客户端自己就是这么建撤离点面板的）</item>
    /// </list>
    /// **只在"拿不到出生点"那条分支调用**（外层 WarnOnce ⇒ 每局最多一次）；**只读、纯日志、不改行为**。
    /// </summary>
    private static string EntryPointDiag()
    {
        try
        {
            var gw = Singleton<GameWorld>.Instance;
            var mp = gw?.MainPlayer;                                  // 来源①：GameWorld.MainPlayer（:551096）
            var profA = mp?.Profile;
            string epA = null; try { epA = profA?.Info?.EntryPoint; } catch { }

            string epB = null, errB = null;
            try { epB = GamePlayerOwner.MyPlayer?.Profile?.Info?.EntryPoint; }   // 来源②（:521553 / :680156）
            catch (Exception ex) { errB = ex.GetType().Name; }

            string epC = null, errC = null;
            try
            {
                var game = Singleton<AbstractGame>.Instance;          // 来源③：本地局内部字段（:56560/:148407）
                if (game != null)
                {
                    System.Reflection.FieldInfo fi = null;
                    for (var ty = game.GetType(); ty != null && fi == null; ty = ty.BaseType)
                        fi = ty.GetField("_entryPoint",
                                         System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    epC = fi?.GetValue(game) as string;
                }
            }
            catch (Exception ex) { errC = ex.GetType().Name; }

            int eligHit = -1, usable = -1;
            try
            {
                var el = ExfiltrationController.Instance?.EligiblePoints(profA);   // :521590（客户端自己的过滤）
                eligHit = el?.Length ?? -1;
                usable = el == null ? -1 : el.Count(IsUsable);
            }
            catch { }

            return $" 出生点诊断：MainPlayer={(mp == null ? "null" : "ok")} Profile(MainPlayer.Profile)={(profA == null ? "null" : "ok")}"
                 + $" EntryPoint①(MainPlayer.Profile)='{epA ?? "(null)"}'"
                 + $" EntryPoint②(GamePlayerOwner.MyPlayer)='{epB ?? (errB == null ? "(null)" : "ERR:" + errB)}'"
                 + $" EntryPoint③(AbstractGame._entryPoint)='{epC ?? (errC == null ? "(null)" : "ERR:" + errC)}'"
                 + $" EligiblePoints命中={eligHit} 其中可用={usable} LocationId='{gw?.LocationId}'";
        }
        catch (Exception ex) { return " 出生点诊断失败: " + ex.GetType().Name; }
    }

    private static void ResetRaid()
    {
        try { _unsub?.Invoke(); } catch { }
        _unsub = null;
        _subscribed = false;
        _lockedThisRaid = false;
        // 条款②（进图即锁）也按局重置
        _exitsInit = false;
        _exitsInitAt = 0f;
        _startLockDone = false;
        _startLockTries = 0;
        _mapUnknownSince = 0f;
        // ★ v0.1.7【审计 P1-1】仪式锁门控（三态）也按局重置
        _halloweenUnknownSince = 0f;
        HalloweenGateState = 0;
        _pendingRitualSource = null;
        _pendingRitualGw = null;
        _startLockPollAt = 0f;      // ★ v0.1.8【复核 R2-B3】Y3 节流时间戳也按局重置（不重置也不会卡：时钟单调；纯为一致性）
        _raid = null;
        _warned.Clear();
    }

    internal static void DiagRitualFired(string via)
    {
        try
        {
            var raid = UnityEngine.Object.FindObjectOfType<EFT.GameWorld>();
            float t = UnityEngine.Time.realtimeSinceStartup;
            var map = "";
            try { map = raid != null ? raid.LocationId ?? "" : ""; } catch { }
            HostilityGuard.DiagAlways($"[仪式诊断] ★仪式触发（{via}）｜realtime={t:F1}s｜地图={map}"
                + "｜看 realtime 就能判断是进图立刻触发还是击杀后触发");
        }
        catch { }
    }

    /// <summary>同一类警告只打一次（战局内每帧/每次都打会淹掉日志）。</summary>
    private static void WarnOnce(string key, string msg)
    {
        try { if (_warned.Add(key)) _log?.LogWarning($"{Tag} ⚠ {msg}"); } catch { }
    }
}

/// <summary>
/// 【仪式开始】的唯一入口：<c>BotHalloweenEvent.StartRitual()</c>（Assembly-CSharp:156633，全局命名空间）。
/// <para>为什么不用 prefix：<c>StartRitual()</c> 自身有 <c>if (!_ritualStarted)</c> 闸门（:156635），
/// postfix 跑完就代表"仪式真的开始了"（:156637 `_ritualStarted = true` 之后）。</para>
/// <para>用 postfix 而不是自己轮询 bot 死亡：官方 :156625 <c>OnBotRemove</c>（死的是 <c>sectactPriestEvent</c>）→
/// <c>CheckOnSectantsCount</c> → 首领到圈后 <c>_bossData.StartRitual()</c>（:80912）都汇到这一个方法。</para>
/// </summary>
internal sealed class RitualStartedPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(BotHalloweenEvent), nameof(BotHalloweenEvent.StartRitual));

    [PatchPostfix]
    private static void Postfix()
    {
        try
        {
            // ★ 2026-10-31【诊断】用户反馈"刚进图仪式倒计时就是 0:00:00"：
            //   这里无条件留痕，好判断 StartRitual 到底是进图就被调用，还是杀了小鹿才调用。
            RitualExitLock.DiagRitualFired("BotHalloweenEvent.StartRitual");
            // ★ 仪式开始 ⇒ 这批角色在官方表里是 AlwaysFriends，必须显式翻成敌对
            HostilityGuard.ForceRitualHostility();
            RitualExitLock.OnRitualStarted("BotHalloweenEvent.StartRitual");
        }
        catch { }
    }
}
