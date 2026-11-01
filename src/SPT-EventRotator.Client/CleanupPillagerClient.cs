using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using EFT;

namespace EventRotator.Client;

/// <summary>
/// ★★★ 自定义 bot 类型 <c>cleanupcrew</c>（清洁队，13792）、<c>pillager</c>（劫掠者，13793）、
/// <c>bloodhound</c>（寻血猎犬，13794）、<c>gus</c>（军火商 Gus，13795）、<c>basmach</c>（军火商 Basmach，13796）
/// 的**客户端运行期**接线（第 11 项 + 第 9 项，克隆源都是 <c>arenaFighterEvent</c>）。
///
/// <para><b>为什么需要</b>：这些类型是 2026 活动里服务端新下发的 <c>WildSpawnType</c> 名字。
/// 客户端有两处会**按名字**解析枚举，缺一个就炸（与 bossScourge 完全同源，见
/// <c>Patcher/Patcher.cs</c> 顶部注释）：
/// <list type="number">
///   <item><c>BossLocationSpawn.ParseBossName()</c> / <c>ParseExcortType()</c> 直接
///     <c>Enum.Parse(typeof(WildSpawnType), 服务端下发的角色名)</c> ⇒ <b>进不了图</b>，
///     报 <c>Requested value 'cleanupcrew' was not found.</c></item>
///   <item>bot profile 里的 <c>Info.Settings.Role</c> 是字符串，客户端按名字反序列化 ⇒ 进图后崩/无效 bot。</item>
/// </list>
/// 枚举成员本身由 <c>Patcher/Patcher.cs</c>（BepInEx preloader patcher →
/// <c>BepInEx\patchers\SPT-EventRotator.Patcher.dll</c>）在 Assembly-CSharp 载入前注入；
/// 本类只做"载入之后才做得了"的两件事：</para>
/// <list type="number">
///   <item><b>行为元数据</b>：给 <see cref="WildSpawnTypeExtension._spawnTypeSettings"/> 补五条。
///     这张表是 <c>IsBoss()/IsFollower()/IsHostileToEverybody()/GetScavRoleKey()</c> 的唯一数据源；
///     <c>WildSpawnTypeExtension.Init()</c> 会遍历全部枚举值、缺项打 BLOCKER ERROR，
///     <c>IsFollower()</c> 还会直接下标取值（缺了 KeyNotFound）。
///     ★ 取值与灾厄小队**同口径**（= 照抄 <c>arenaFighterEvent</c>：isBoss/isFollower/isHostileToEverybody
///     全部 true），只把 ScavRoleKey 换成这几个角色自己的键 ⇒ 显示名/击杀提示读的就是这个键：
///     <c>ScavRole/CleanupCrew</c>、<c>ScavRole/Pillager</c>、<c>ScavRole/Bloodhound</c>、<c>ScavRole/Gus</c>、<c>ScavRole/Basmach</c>
///     （文案由**服务端** <c>LocaleInject</c> 注入）。</item>
///   <item><b>随从适配</b>：<c>WildSpawnTypeExtension.ComparableCache</c> 里给每个角色各自登记
///     「自身 + arenaFighterEvent」，这样它们既能让**同类型**当随从（清洁队/猎犬 3~4 人一组，两个军火商 2 人一组），
///     也能和现有的 arena 活动互认随从。⚠ 只做加法登记，不动别人已有的组。</item>
/// </list>
///
/// <para><b>故意没做的事</b>（与 bossScourge 的差异，避免扯动行为）：</para>
/// <list type="bullet">
///   <item><b>不改脑层</b>：<c>StandartBotBrain.Activate</c> 的 switch 没有新枚举 ⇒ 落到
///     <c>default: StandardAssaultLayersStrategy</c>（SCAV 脑）。Cleanup Crew 的官方定位正是
///     「代替普通 SCAV」、Pillagers 是匪帮、Bloodhound 是猎犬队 ⇒ SCAV 脑就是想要的行为；不像灾厄小队那样换竞技场脑。</item>
///   <item><b>不加小队友军修正</b>：<c>BossScourgeClient.EnsureSquadFriendly</c> 只处理
///     灾厄小队三个值；这几个是**不同阵营**（Arena 里本就互相敌对），保持
///     <c>HostilityGuard</c> 的「除自己外全敌对」统一规则即可 —— 那段代码按 events.json 的
///     bossSpawns/bossChance 自动收录角色，不需要改 HostilityGuard。</item>
///   <item><b>不碰 SAIN 排除 / BigBrain 层</b>：那是灾厄小队专用（ScourgeAi/），这几个走原版 AI。</item>
/// </list>
///
/// <para>⚠ 未实机验证（本环境无法启动游戏）；所有分支 try/catch，失败只打日志、绝不抛。</para>
/// </summary>
internal static class CleanupPillagerClient
{
    internal const string CleanupRoleName = "cleanupcrew";
    internal const int CleanupRoleValue = 13792;

    internal const string PillagerRoleName = "pillager";
    internal const int PillagerRoleValue = 13793;

    internal const string BloodhoundRoleName = "bloodhound";
    internal const int BloodhoundRoleValue = 13794;

    internal const string GusRoleName = "gus";
    internal const int GusRoleValue = 13795;

    internal const string BasmachRoleName = "basmach";
    internal const int BasmachRoleValue = 13796;

    /// <summary>显示名用的 locale 键（服务端 <c>LocaleInject</c> 负责注入 en/ch 文案）。</summary>
    internal const string CleanupRoleKey = "ScavRole/CleanupCrew";
    internal const string PillagerRoleKey = "ScavRole/Pillager";
    internal const string BloodhoundRoleKey = "ScavRole/Bloodhound";
    internal const string GusRoleKey = "ScavRole/Gus";
    internal const string BasmachRoleKey = "ScavRole/Basmach";

    private static ManualLogSource _log;
    private static bool _installed;

    /// <summary>原版"竞技场斗士"：本项五个新角色的行为/随从参照物（契约里的克隆源）。</summary>
    private static readonly WildSpawnType EscortRole = WildSpawnType.arenaFighterEvent;

    /// <summary>
    /// 按**名字**解析角色值（不迷信常量）：若别的 mod 先用同名成员注册了别的数值，
    /// 我们的 patcher 会跳过（不重复加名），此时应跟随枚举里该名字的真实值。
    /// 名字不存在时退回常量（此时 <see cref="Install"/> 会报缺 patcher 并退出）。
    /// </summary>
    private static WildSpawnType ResolveRole(string name, int fallback)
    {
        try
        {
            if (Enum.TryParse<WildSpawnType>(name, ignoreCase: true, out var v)
                && string.Equals(Enum.GetName(typeof(WildSpawnType), v), name, StringComparison.Ordinal))
                return v;
        }
        catch { }
        return (WildSpawnType)fallback;
    }

    /// <summary>
    /// 要接线的**全部角色**（加角色只改这里 + <c>Patcher/Patcher.cs</c> 的 <c>Roles</c>）。
    /// <para>Name/Value 用于"枚举到底有没有"的校对与日志；Role = 解析后的枚举值；LocaleKey 决定显示名。</para>
    /// </summary>
    private static readonly (string Name, int Value, WildSpawnType Role, string LocaleKey)[] Roles =
    {
        (CleanupRoleName,    CleanupRoleValue,    ResolveRole(CleanupRoleName, CleanupRoleValue),       CleanupRoleKey),
        (PillagerRoleName,   PillagerRoleValue,   ResolveRole(PillagerRoleName, PillagerRoleValue),     PillagerRoleKey),
        (BloodhoundRoleName, BloodhoundRoleValue, ResolveRole(BloodhoundRoleName, BloodhoundRoleValue), BloodhoundRoleKey),
        (GusRoleName,        GusRoleValue,        ResolveRole(GusRoleName, GusRoleValue),                GusRoleKey),
        (BasmachRoleName,    BasmachRoleValue,    ResolveRole(BasmachRoleName, BasmachRoleValue),        BasmachRoleKey),
    };

    internal static void Install(ManualLogSource log)
    {
        _log = log;
        if (_installed) return;
        _installed = true;

        try
        {
            // 0) 枚举是否真的注册了（prepatcher 产物在 BepInEx\patchers\）。逐个角色判定（遍历 Roles ⇒ 加角色自动覆盖）。
            var missing = new List<string>();
            foreach (var r in Roles)
            {
                var defined = Enum.GetName(typeof(WildSpawnType), (WildSpawnType)r.Value);
                if (!string.Equals(defined, r.Name, StringComparison.Ordinal))
                    missing.Add($"{r.Name}（应为 {r.Value} / 当前名字 = '{defined ?? "(未定义)"}'）");
            }
            if (missing.Count > 0)
            {
                _log?.LogError(
                    "[ArenaTypes] ❌ 客户端 EFT.WildSpawnType 里缺少：" + string.Join("；", missing) + "\n" +
                    "   ⇒ 请确认 BepInEx\\patchers\\SPT-EventRotator.Patcher.dll 已安装（这是 preloader patcher，不是 plugins 里的插件）。\n" +
                    "   缺它：地图 JSON 里出现这些角色时客户端 Enum.Parse 直接抛 " +
                    "\"Requested value 'cleanupcrew' / 'pillager' / 'bloodhound' / 'gus' / 'basmach' was not found.\"（进不了图），" +
                    "服务端下发的 bot profile 也无法反序列化。本类将不做任何事，避免半套用。");
                return;
            }

            // 1) 行为元数据（与灾厄小队同口径 = 照抄 arenaFighterEvent，只换 ScavRoleKey ⇒ 显示名）。
            foreach (var r in Roles) EnsureSpawnTypeSettings(r.Role, r.Name, r.LocaleKey);

            // 2) 随从适配：每个类型自身 + 原版 arenaFighterEvent（加法式，不动别人的组）。
            foreach (var r in Roles)
            {
                LinkSuitable(r.Role, r.Role);
                LinkSuitable(r.Role, EscortRole);
            }

            _log?.LogInfo("[ArenaTypes] ✔ 客户端接线完成：" +
                          string.Join("、", Roles.Select(r => $"{r.Name}={(int)r.Role}（{r.LocaleKey}）")) +
                          "；AI 走原版 SCAV 脑、敌对性由 HostilityGuard 按 events.json 自动收录");
        }
        catch (Exception ex)
        {
            _log?.LogError("[ArenaTypes] 客户端接线失败（回退原版行为，不影响其它 bot）: " + ex);
        }
    }

    /// <summary>给 `_spawnTypeSettings` 补一条（已存在则不覆盖），ScavRoleKey 决定客户端显示名。</summary>
    private static void EnsureSpawnTypeSettings(WildSpawnType role, string name, string scavRoleKey)
    {
        try
        {
            if (WildSpawnTypeExtension._spawnTypeSettings.ContainsKey(role))
            {
                _log?.LogInfo($"[ArenaTypes] _spawnTypeSettings 已有 {name}={(int)role}，保持原值（不覆盖其它 mod 的设置）");
                return;
            }
            WildSpawnTypeExtension._spawnTypeSettings[role] =
                new WildSpawnTypeSettings(isBoss: true, isFollower: true, isHostileToEverybody: true,
                                          scavRoleKey: scavRoleKey);
            _log?.LogInfo($"[ArenaTypes] ✔ _spawnTypeSettings 已登记 {name}={(int)role}" +
                          $"（照抄 arenaFighterEvent：isBoss/isFollower/isHostileToEverybody=true, {scavRoleKey}）");
        }
        catch (Exception ex) { _log?.LogWarning($"[ArenaTypes] ⚠ _spawnTypeSettings 登记 {name} 失败: {ex.Message}"); }
    }

    /// <summary>把两个角色在 `WildSpawnTypeExtension.ComparableCache` 里登记为互相"适配"（加法式，保留已有组）。</summary>
    private static void LinkSuitable(WildSpawnType a, WildSpawnType b)
    {
        try
        {
            var cache = WildSpawnTypeExtension.ComparableCache;
            if (cache == null) { _log?.LogWarning("[ArenaTypes] ⚠ ComparableCache 为空，跳过随从适配"); return; }

            if (!cache.TryGetValue(a, out var setA)) cache[a] = setA = new HashSet<WildSpawnType>();
            setA.Add(b);
            if (!cache.TryGetValue(b, out var setB)) cache[b] = setB = new HashSet<WildSpawnType>();
            setB.Add(a);
            _log?.LogInfo($"[ArenaTypes] ✔ ComparableCache: {a} ⇄ {b}（随从适配）");
        }
        catch (Exception ex) { _log?.LogWarning("[ArenaTypes] ⚠ 随从适配失败: " + ex.Message); }
    }

    /// <summary>诊断用：是不是本类接线的任一角色（三个都认；本类目前不挂 Harmony，所以没被用到）。</summary>
    internal static bool IsEventRotatorRole(BotOwner owner)
    {
        try
        {
            if (owner?.Profile?.Info?.Settings == null) return false;
            var role = owner.Profile.Info.Settings.Role;
            foreach (var r in Roles) if (role == r.Role) return true;
        }
        catch { }
        return false;
    }
}
