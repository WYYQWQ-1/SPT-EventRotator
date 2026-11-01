using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;

namespace EventRotator.Client;

/// <summary>
/// ★★★ 【v0.1.6】Halloween 2022 官方条款：「**开局随机给玩家一个负面状态**」。
///
/// <para>【官方逐字】<c>handover-1.6.3/…/数据/events_wiki.txt:1584</c>（Halloween 2022 章节）：
/// <code>
/// * A random negative [[Health system#Temporary_status_effects|status effect]] is given to your PMC at the start a raid.
/// </code>
/// ⇒ **官方没有点名具体是哪几个状态**，它链接的是健康系统的「**Temporary status effects**」列表
/// （即"从本体的临时负面状态里随机来一个"）⇒ 本实现从**本机现成的负面 buff** 里随机，属**忠实近似**。</para>
///
/// <para>【候选池怎么挑的 —— 三条硬判据】
/// ① **纯负面**（含正面条目的不选）；
/// ② **要核"连带 <c>Effects.&lt;名字&gt;</c>"**（上一轮教训：buff 名不同则副作用不同）
///    —— 下表逐个给出该 buff 的条目读哪个 <c>Effects.*</c> 键，以及客户端读点；
/// ③ **不能危险到"开局即死"**（所以排除了 <c>Buffs_Obdolbos</c>：它含 25% 概率的 <c>HealthRate −600</c>）。</para>
///
/// <table>
/// <tr><th>候选 buff（本机 globals 键名）</th><th>它施加的负面状态</th><th>连带读取的 <c>Effects.*</c>（Assembly-CSharp 行号）</th></tr>
/// <tr><td><c>Buffs_melee_bleed</c></td><td>轻出血 100% + 重出血 50%</td>
///     <td><c>Effects.LightBleeding</c>（:809620）/ <c>Effects.HeavyBleeding</c>（:809627）— 两个键本机都有 ✔</td></tr>
/// <tr><td><c>Buffs_melee_blunt</c></td><td>脑震荡（Head，100 秒）+ 骨折</td>
///     <td><c>Fracture</c> 读 <c>Effects.Fracture</c>（:810229）✔；<c>Contusion</c> 类（:809884）**不读任何 EffectsSettings**（空状态效果）</td></tr>
/// <tr><td><c>Buffs_hultafors</c></td><td>脑震荡（20 秒）+ 骨折 + 疼痛（20 秒）</td>
///     <td>同上 + <c>Pain</c> 读 <c>Effects.Pain</c>（:810868 / :810894）✔</td></tr>
/// <tr><td><c>Buffs_Frostbite</c></td><td>冻伤：体温 −0.1 / 手抖 / 耐力·水分·能量·体力上限下降（15 秒）</td>
///     <td><c>BodyTemperature</c> 读 <c>Effects.BodyTemperature</c>（:809774/:809776）✔；<c>HandsTremor</c> 读 <c>Effects.Tremor</c>（:811905）✔；<c>Frostbite</c> 类（:810245）是**空类、不读设置** ✔</td></tr>
/// </table>
///
/// <para>【刻意排除的（都在本机 globals 里，但不适合当"随机负面状态"）】
/// <list type="bullet">
/// <item><c>BuffsZombieInfection</c> —— 它同时给「超人」正面三项（<c>HealthRate+50/EnergyRate+50/Vitality+20</c>），
///   并连带 <c>Effects.ZombieInfection</c>（脱水 −0.84 / 听觉 −20%，读点 :811957）⇒ **不是纯负面**。</item>
/// <item><c>Buffs_KultistsToxin</c> —— 未知毒素（<c>Effects.Intoxication</c>，:810326）是**永久 DoT**（`Duration: -1`），
///   而且本活动官方另有专门条款（wiki :1586「Unknown toxin is removed upon death for free」）⇒ 不算"临时状态"，
///   放进池里会变成"开局 3 分钟慢性死亡"。**要收它只需在下面的 <c>Candidates</c> 里加一行。**</item>
/// <item><c>Buffs_Obdolbos</c> / <c>Buffs_drink_*</c> / <c>Buffs_Antidote</c> 等 —— 含正面条目、或不是"状态效果"、
///   或含"低概率秒死"条目（<c>HealthRate −600</c>）⇒ 不选。</item>
/// </list></para>
///
/// <para>【怎么施加】用官方入口 <c>ActiveHealthController.TryDoExternalBuff(name)</c>（Assembly-CSharp:812355）
/// → <c>DoExternalBuff</c>（:812364，**表里没有这个名字就什么都不做** ⇒ 天然降级）。
/// 客户端各负面状态的分发点：<c>Contusion</c> :811722 / <c>Fracture</c> :811714 / <c>LightBleeding</c> :811698 /
/// <c>HeavyBleeding</c> :811706 / <c>Pain</c> :811565 / <c>HandsTremor</c> :811575 / <c>BodyTemperature</c> :811685 /
/// <c>FrostbiteBuff</c> :811775。</para>
///
/// <para>【门控】只在该活动（<c>halloween_2022</c>）生效时跑；每局**只随机一次**（幂等）；换局重置；全程 try/catch。</para>
/// </summary>
internal static class RandomDebuff
{
    private const string Tag = "[EventRotator.Client][随机负面状态]";

    /// <summary>
    /// ★ 候选池（本机 globals 现成键名）。**这就是"官方那句话"的落地清单**：
    /// 纯负面 + 已验证连带 <c>Effects.*</c> + 不会开局即死。要增删改这里一行即可。
    /// </summary>
    private static readonly string[] Candidates =
    {
        "Buffs_melee_bleed",     // 出血
        "Buffs_melee_blunt",     // 脑震荡 + 骨折
        "Buffs_hultafors",       // 脑震荡 + 骨折 + 疼痛
        "Buffs_Frostbite",       // 冻伤
    };

    /// <summary>人话解释（只用于日志/F12 展示）。</summary>
    private static readonly Dictionary<string, string> Human = new Dictionary<string, string>
    {
        ["Buffs_melee_bleed"] = "出血（轻出血 100% + 重出血 50%）",
        ["Buffs_melee_blunt"] = "脑震荡（头部 100 秒）+ 骨折",
        ["Buffs_hultafors"] = "脑震荡（20 秒）+ 骨折 + 疼痛（20 秒）",
        ["Buffs_Frostbite"] = "冻伤（体温下降 / 手抖 / 耐力·水分·能量·体力上限下降）",
        ["Buffs_KultistsToxin"] = "未知毒素（永久，需 xTG-12 解毒剂）",
    };

    /// <summary>F12 开关取值来源（Plugin.Awake 注入；默认 true）。</summary>
    internal static Func<bool> EnabledSource;

    /// <summary>当前是否启用（F12 默认开）。</summary>
    public static bool Enabled { get { try { return EnabledSource == null || EnabledSource(); } catch { return true; } } }

    /// <summary>「halloween_2022」此刻是否生效（EventsOverlay 每帧推；拿不到 = false ⇒ 不动作）。</summary>
    public static bool ActivityActive;

    private static ManualLogSource _log;
    private static readonly HashSet<string> _warned = new HashSet<string>();

    // ── 每局状态 ──
    private static object _raid;
    private static bool _done;

    internal static void SetLog(ManualLogSource log) => _log = log;

    /// <summary>候选池（去掉本机 globals 里没有的键），供日志/F12 自证。</summary>
    internal static string[] CandidatePool()
    {
        try
        {
            var buffs = Singleton<GlobalConfiguration>.Instance?.Health?.Effects?.Stimulator?.Buffs;
            if (buffs == null) return Array.Empty<string>();
            return Candidates.Where(buffs.ContainsKey).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Update 每帧调用（EventsOverlay.Update）。内部极轻：门控短路 + 一次就结束。</summary>
    internal static void Tick()
    {
        try
        {
            var gw = Singleton<GameWorld>.Instance;
            if (gw == null)
            {
                if (_raid != null) ResetRaid();
                return;
            }
            if (!ReferenceEquals(gw, _raid))
            {
                if (_raid != null) ResetRaid();
                _raid = gw;
                _log?.LogInfo($"{Tag} 进入新的一局 → 已重置（等开局判定要不要给随机负面状态）");
            }
            if (_done || !Enabled || !ActivityActive) return;

            var hc = gw.MainPlayer?.ActiveHealthController;      // Player.ActiveHealthController :597304
            if (hc == null) return;                              // 玩家还没就绪 → 下一帧再试

            var buffs = Singleton<GlobalConfiguration>.Instance?.Health?.Effects?.Stimulator?.Buffs;   // 与 :812366 同路径
            if (buffs == null) { WarnOnce("nocfg", "拿不到 globals 的 Effects.Stimulator.Buffs 表 ⇒ 本局跳过随机负面状态"); _done = true; return; }

            var pool = Candidates.Where(buffs.ContainsKey).ToArray();
            if (pool.Length == 0)
            {
                WarnOnce("nopool", "本机 globals 里 4 个候选负面 buff **一个都没有** ⇒ 本局跳过（只 warning，不影响游戏）");
                _done = true;
                return;
            }

            // ★ 随机挑一个（官方：A random negative status effect）
            var pick = pool[UnityEngine.Random.Range(0, pool.Length)];
            hc.TryDoExternalBuff(pick);                          // :812355（表里没有该名字 ⇒ 什么都不做）
            _done = true;

            string human = Human.TryGetValue(pick, out var h) ? h : "负面状态";
            _log?.LogInfo($"{Tag} 🎲 开局随机负面状态 ⇒ **{pick}**（{human}）"
                        + $"［官方 Halloween 2022「A random negative status effect is given to your PMC at the start a raid」，wiki:1584；"
                        + $"本局池 {pool.Length} 个：{string.Join(" / ", pool)}］");
        }
        catch { }
    }

    private static void ResetRaid()
    {
        _raid = null;
        _done = false;
        _warned.Clear();
    }

    /// <summary>同一类警告只打一次。</summary>
    private static void WarnOnce(string key, string msg)
    {
        try { if (_warned.Add(key)) _log?.LogWarning($"{Tag} ⚠ {msg}"); } catch { }
    }
}
