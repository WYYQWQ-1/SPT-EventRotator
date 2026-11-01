using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;

namespace EventRotator.Client;

/// <summary>
/// ★★★ 【v0.1.6 条款③】June 2022 删档前 Day 7「Deadly Toxin 致命毒素」的第 ③ 条
/// （官方原文，handover 的 events_wiki.txt:1636-1643）：
/// <code>
/// * The Unknown toxin debuff is given to your PMC at the start of the raid, which gives you
///   8 minutes to live unless you find an antidote on Bosses or airdrops.
/// </code>
/// 用户口径（2026，含他玩官方时的补充）：「进图即感染未知病毒，变成塔科夫超人的同时直接倒计时，
/// 倒计时结束就暴毙；空投 / SCAV 口袋 / boss 身上会刷解毒剂，**解毒剂重置倒计时**」。
///
/// <para>【用户裁决】① 走"零 globals 改动"的 **a 方案**（不走原生 LethalToxin）；
/// ② **超人不带副作用**（所以**不用** <c>BuffsZombieInfection</c> —— 它带
/// <c>Effects.ZombieInfection = {Dehydration:-0.84, HearingDebuffPercentage:0.2}</c>）；<br/>
/// ③ **只解毒素**（原生 <c>RemoveIntoxication()</c>，不走未验证的"解病毒"路）。</para>
///
/// <para>【用什么官方 API】
/// <list type="bullet">
/// <item>施加 buff：<c>ActiveHealthController.TryDoExternalBuff(name)</c>（Assembly-CSharp:812355）
///   → <c>DoExternalBuff</c>（:812364，查 <c>Effects.Stimulator.Buffs</c> 表，**表里没有就什么都不做** ⇒ 天然降级）</item>
/// <item>毒素：<c>"Buffs_KultistsToxin"</c> → <c>{BuffType:"UnknownToxin", Duration:-1}</c>
///   → <c>DoIntoxication()</c>（:812862）→ <c>AddEffect&lt;Intoxication&gt;</c>（:810310，永久 DoT）</item>
/// <item>解毒剂：xTG-12（tpl <c>5fca138c2a7b221b2852a5c6</c>）自带 <c>StimulatorBuffs="Buffs_Antidote"</c>
///   → <c>EStimulatorBuffType.Antidote</c>（:811679）→ <c>RemoveIntoxication()</c>（:812437）</item>
/// <item>暴毙：<c>Kill(EDamageType.LethalToxin)</c>（:812708，public）—— 我们自己计时到点调用</item>
/// <item>查毒素还在不在：<c>FindActiveEffects&lt;ActiveHealthController.Intoxication&gt;()</c>（:814641）</item>
/// <item>解毒剂保护期：<c>HasStimulatorBuff(EStimulatorBuffType.Antidote)</c>（:812636）</item>
/// </list></para>
///
/// <para>【超人 buff 的名字】<c>Buffs_EventToxinSuperhuman</c> 是**我们要服务端新增**的那条
/// （只含 <c>HealthRate+50 / EnergyRate+50 / SkillRate(Vitality)+20</c>，无任何副作用条目）。
/// 服务端 globals 还没加时：这里**只 warning、不施加**，本局就只有毒素、没有超人
/// （→ 见 <c>audit/致命毒素-条款3.md</c> 里的精确 JSON 与服务端最小改法）。</para>
///
/// <para>【安全护栏】① **只有真正观察到毒素效果之后才开始计时**（buff 名不存在 / 免疫技能挡掉 ⇒ 永远不判死）；
/// ② 每局只结算一次；③ 全程 try/catch，任何异常都吞掉；④ 换局自动重置。</para>
/// </summary>
internal static class ToxinInfection
{
    private const string Tag = "[EventRotator.Client][致命毒素]";

    /// <summary>本机 globals 现成的毒素 buff（`BuffType: UnknownToxin`，`Duration: -1`）。</summary>
    private const string ToxinBuff = "Buffs_KultistsToxin";

    /// <summary>★ 需要服务端在 `globals.config.Health.Effects.Stimulator.Buffs` 里新增（只有正面三项，无副作用）。</summary>
    private const string SupermanBuff = "Buffs_EventToxinSuperhuman";

    /// <summary>官方原文 "8 minutes to live"。</summary>
    private const float CountdownSeconds = 480f;

    /// <summary>重复施加的最小间隔（解毒剂保护期内施加是无效的，靠这个节奏重试）。</summary>
    private const float ReapplyDelay = 3f;

    /// <summary>一轮最多试这么多次（≈ 120 秒，够跨过解毒剂的 60 秒保护期）。</summary>
    private const int MaxApplyTries = 40;

    /// <summary>F12 开关取值来源（Plugin.Awake 注入；默认 true）。</summary>
    internal static Func<bool> EnabledSource;

    /// <summary>当前是否启用（F12 默认开）。</summary>
    public static bool Enabled { get { try { return EnabledSource == null || EnabledSource(); } catch { return true; } } }

    /// <summary>「致命毒素 june2022_toxin」此刻是否生效（EventsOverlay 每帧推；拿不到 = false ⇒ 不动作）。</summary>
    public static bool ActivityActive;

    /// <summary>
    /// ★★★ v0.1.6【用户裁决：该活动**只在海关生效**】本局地图是不是 `bigmap`（海关）。
    /// 由宿主组件 EventsOverlay 每帧推（判据 = `Singleton&lt;GameWorld&gt;.Instance.LocationId`，Assembly-CSharp:551349；
    /// 官方同款用法 :435760 `LocationID => Singleton&lt;GameWorld&gt;.Instance.LocationId`）。
    /// ⚠ 默认 **false**（fail-safe）：拿不到地图 ⇒ **不感染、不计时、不上超人**。
    /// </summary>
    public static bool MapOk;

    /// <summary>
    /// ★ v0.1.6【三态之一】**地图是否已就绪**（GameWorld 建好 **且** `LocationId` 已赋值）。
    /// 未就绪 ⇒ <see cref="Tick"/> **等待**（不动作、**不打"跳过"警告**）——
    /// 实测出现过"同一局早期被判成非目标图、之后又正常执行"的误报（用户当时就在海关）。
    /// </summary>
    public static bool MapKnown;
    /// <summary>地图未就绪的最长等待（秒）；超时后不再等，但**仍不动作**（fail-safe）。</summary>
    private const float MapUnknownTimeoutSec = 30f;
    private static float _mapUnknownSince;

    private static ManualLogSource _log;
    private static readonly HashSet<string> _warned = new HashSet<string>();

    // ── 每局状态（换局 = GameWorld 实例变了 → 全部重置）──
    private static object _raid;
    private static int _applyTries;
    private static float _nextApplyAt;
    private static bool _toxinSeen;
    private static float _deadline;
    private static bool _settled;

    internal static void SetLog(ManualLogSource log) => _log = log;

    /// <summary>Update 每帧调用（EventsOverlay.Update）。内部极轻：一次单例取值 + 门控短路。</summary>
    internal static void Tick()
    {
        try
        {
            var gw = Singleton<GameWorld>.Instance;
            if (gw == null)
            {
                if (_raid != null) ResetRaid();     // 回主菜单/结算 → 清本局状态
                return;
            }
            if (!ReferenceEquals(gw, _raid))
            {
                if (_raid != null) ResetRaid();
                _raid = gw;
                _log?.LogInfo($"{Tag} 进入新的一局 → 感染状态已重置（等下判定「是否要感染」）");
            }
            if (_settled || !Enabled || !ActivityActive) return;
            float mapNow = UnityEngine.Time.realtimeSinceStartup;
            // ★★ v0.1.6【用户裁决：只在海关生效 · 三态】—— 未就绪 ⇒ **等待**（不动作、不打"跳过"）
            if (!MapKnown)
            {
                if (_mapUnknownSince <= 0f) _mapUnknownSince = mapNow;
                if (mapNow - _mapUnknownSince < MapUnknownTimeoutSec) return;
                WarnOnce("maptimeout", $"地图信息连续 {MapUnknownTimeoutSec:0} 秒仍未就绪 ⇒ 本局**不再等待**（也就**不感染、不计时**，fail-safe）；"
                                     + "若随后确实就绪且是海关，仍会照常执行" + Plugin.FikaLogSuffix);
                return;
            }
            _mapUnknownSince = 0f;
            // ★ 确实拿到地图、且不是 bigmap ⇒ 跳过 + 每局一条警告
            if (!MapOk)
            {
                WarnOnce("map", "活动生效中，但本局地图不是『海关（bigmap）』⇒ 跳过「未知毒素 + 倒计时 + 超人」（用户口径：该活动只在海关生效）" + Plugin.FikaLogSuffix);
                return;
            }

            var hc = gw.MainPlayer?.ActiveHealthController;      // Player.ActiveHealthController :597304
            if (hc == null) return;                              // 玩家还没就绪 → 下一帧再试

            float now = UnityEngine.Time.realtimeSinceStartup;

            // ① 第一次：把「超人」增益加上（每局只加一次；名字不在服务端 globals 里 ⇒ 只 warning）
            if (_applyTries == 0) ApplySuperman(hc);

            bool toxinOn = HasToxin(hc);

            // ② 只有"没感染上 / 被解毒剂解掉且过了保护期"时才重复施加
            if (!toxinOn && !_toxinSeen && now >= _nextApplyAt && _applyTries < MaxApplyTries
                && !hc.HasStimulatorBuff(EStimulatorBuffType.Antidote))      // 原生：解毒剂保护期内施加无效（:812865/:812882）
            {
                _nextApplyAt = now + ReapplyDelay;
                _applyTries++;
                hc.TryDoExternalBuff(ToxinBuff);     // :812355 → DoIntoxication :812862
                if (_applyTries == 1)
                    _log?.LogInfo($"{Tag} ☣ 已施加「未知毒素」（{ToxinBuff}）→ 观察到效果后开始 {CountdownSeconds / 60:0} 分钟倒计时");
                else if (_applyTries >= MaxApplyTries)
                    WarnOnce("notoxin", $"连续 {MaxApplyTries} 次都没能把「未知毒素」施加成功（buff 名不在服务端 globals？Immunity 技能一直挡？）"
                                      + $" ⇒ 本局**只继续尝试、绝不据此判定死亡**");
            }

            // ③ 毒素在身上 ⇒ 计时 / 到点暴毙
            if (toxinOn)
            {
                if (!_toxinSeen)
                {
                    _toxinSeen = true;
                    _applyTries = 0;                     // 新一轮：重试预算重置
                    _deadline = now + CountdownSeconds;
                    _log?.LogInfo($"{Tag} ☠ 已感染「未知毒素」⇒ **{CountdownSeconds / 60:0} 分钟内必须打 xTG-12 解毒剂**，否则暴毙（官方条款③）");
                }
                else if (now >= _deadline)
                {
                    _settled = true;
                    hc.Kill(EDamageType.LethalToxin);    // :812708（public）
                    _log?.LogWarning($"{Tag} ⏱ {CountdownSeconds / 60:0} 分钟到、毒素未解 ⇒ **暴毙**（EDamageType.LethalToxin，对应官方「8 minutes to live」）");
                }
                return;
            }

            // ④ 毒素不在身上，而之前见过 ⇒ 一定是被解掉了（xTG-12 的 Buffs_Antidote → RemoveIntoxication）
            if (_toxinSeen)
            {
                _toxinSeen = false;
                _applyTries = 0;
                _deadline = now + CountdownSeconds;      // ★ 用户口径：解毒剂"重置倒计时"
                _log?.LogInfo($"{Tag} ✅ 毒素已被解除（xTG-12 解毒剂 ⇒ 原生 RemoveIntoxication）⇒ **倒计时重置为 {CountdownSeconds / 60:0} 分钟**"
                            + $"（解毒剂保护期过后会重新感染，再打一针可再续）");
            }
        }
        catch { }
    }

    /// <summary>「超人」增益（只含正面三项；服务端没加这条 buff 时只 warning）。</summary>
    private static void ApplySuperman(ActiveHealthController hc)
    {
        try
        {
            var buffs = Singleton<GlobalConfiguration>.Instance?.Health?.Effects?.Stimulator?.Buffs;   // 与 :812366 同一条访问路径
            if (buffs == null) { WarnOnce("nocfg", "拿不到 globals 的 Effects.Stimulator.Buffs 表 ⇒ 跳过「超人」增益"); return; }
            if (!buffs.ContainsKey(SupermanBuff))
            {
                WarnOnce("nosuper", $"服务端 globals 里还没有「{SupermanBuff}」这条 buff ⇒ 本局**只有毒素、没有超人增益**"
                                  + $"（服务端数据侧待落：Effects.Stimulator.Buffs 新增这条，只放 HealthRate/EnergyRate/SkillRate 三项）");
                return;
            }
            hc.TryDoExternalBuff(SupermanBuff);          // :812355
            _log?.LogInfo($"{Tag} 🦾 已施加「超人」增益（{SupermanBuff}：回血 +50 / 能量 +50 / 活力 +20，**无任何副作用条目**）");
        }
        catch (Exception ex) { WarnOnce("super", "施加「超人」增益失败: " + ex.Message); }
    }

    /// <summary>毒素效果还在不在（<c>ActiveHealthController.Intoxication</c>，:810310；查询 API :814641）。</summary>
    private static bool HasToxin(ActiveHealthController hc)
    {
        try { return hc.FindActiveEffects<ActiveHealthController.Intoxication>().Any(); }
        catch { return false; }
    }

    private static void ResetRaid()
    {
        _raid = null;
        _applyTries = 0;
        _nextApplyAt = 0f;
        _toxinSeen = false;
        _deadline = 0f;
        _settled = false;
        _mapUnknownSince = 0f;
        _warned.Clear();
    }

    /// <summary>同一类警告只打一次。</summary>
    private static void WarnOnce(string key, string msg)
    {
        try { if (_warned.Add(key)) _log?.LogWarning($"{Tag} ⚠ {msg}"); } catch { }
    }
}
