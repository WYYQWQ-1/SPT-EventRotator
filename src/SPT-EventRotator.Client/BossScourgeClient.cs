using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using EFT;
using HarmonyLib;

namespace EventRotator.Client;

/// <summary>
/// ★★★ 自定义 bot 类型 <c>bossScourge</c>（灾厄小队冠军，13790）+ <c>followerScourge</c>
/// （灾厄小队小弟/护卫，13791）的**客户端运行期**接线。
///
/// <para>枚举成员本身由 <c>Patcher/Patcher.cs</c>（BepInEx preloader patcher，输出到
/// <c>BepInEx\patchers\</c>）在 Assembly-CSharp 载入前注入。本类只做"载入之后才做得了"的三件事：
/// <list type="number">
///   <item><b>行为元数据</b>：给 `WildSpawnTypeExtension._spawnTypeSettings` 补两条。
///     这张表是 `IsBoss()/IsFollower()/IsHostileToEverybody()/GetScavRoleKey()` 的唯一数据源，
///     且 `WildSpawnTypeExtension.Init()`（BotsController.Init 时）会遍历**全部枚举值**
///     并给缺失项打 `BLOCKER ERROR`；`IsFollower()` 还会直接
///     `_spawnTypeSettings[role]` 下标取值（Assembly-CSharp:187825-187828）⇒ 缺了会 KeyNotFound。
///     ★ 取值**逐字照抄 `arenaFighterEvent`**（Assembly-CSharp:187363-187365：
///     isBoss=true / isFollower=true / isHostileToEverybody=true），**只有 ScavRoleKey 换成本 mod 自己的**
///     —— 显示名的唯一来源就是这个键（`EFT.WildSpawnTypeExtension._spawnTypeSettings[enum]` 的 ScavRoleKey），
///     它与 `SPT_Data/database/locales/global/*.json` 里的键一一对应：
///     <c>ScavRole/ArenaFighterEvent</c> = Smuggler/劫掠者（所以小弟此前被显示成 Raider/Smuggler）；
///     本 mod 改用 <c>ScavRole/ScourgeBoss</c>（Scourge/灾祸）与
///     <c>ScavRole/ScourgeFollower</c>（Scourge's guard/灾祸小队护卫），这两条文案由**服务端**
///     <c>LocaleInject</c> 注入（客户端读的是服务端下发的 locale 表）。</item>
///   <item><b>随从适配</b>：`BotBoss.IsFollowerSuitableForBoss(follower, boss)` → `IsSuitable()` 查
///     `ComparableCache`（Assembly-CSharp:187729-187736 / 52219-52222）。把 bossScourge ⇄ followerScourge ⇄
///     arenaFighterEvent 三者互相登记后，4 名 followerScourge 才会被认作"这个 boss 的合法随从"。</item>
///   <item><b>AI 行为对齐</b>：`StandartBotBrain.Activate()` 的 switch 里没有新枚举值 ⇒ 会落到
///     `default: StandardAssaultLayersStrategy`（SCAV 脑）。而 `arenaFighterEvent` 用的是
///     `PmcLayersStrategy(withPursuit:false)`（Assembly-CSharp:70102-70104）。
///     另外 `BotEnemyChooser.Create` 对 arenaFighterEvent 返回 `ArenaFighterEnemyChooser`
///     （:104820-104822），`BotGroupWarnData.ShallBossAttack` 对 arenaFighterEvent 返回 false
///     （:58575-58594）。这三处用 Harmony postfix/prefix 补成**与 arenaFighterEvent 相同**（两个角色都补）。</item>
/// </list></para>
///
/// <para>⚠ `PmcLayersStrategy` 重建部分照抄 MoreBotsAPI 的 `StandartBotBrainActivatePatch`
/// （MoreBotsPlugin 反编译 :400-435）：必须 **Dispose 旧 brain/agent → 建新 brain → 建新 agent
/// → 手动触发 OnSetStrategy**，否则 BigBrain/SAIN 的层注入挂不到新 brain 上。</para>
///
/// <para>⚠ 未实机验证：本环境无法启动游戏。所有分支都 try/catch，失败只打日志、回退原版行为。</para>
/// </summary>
internal static class BossScourgeClient
{
    internal const string BossRoleName = "bossScourge";
    internal const int BossRoleValue = 13790;

    internal const string FollowerRoleName = "followerScourge";
    internal const int FollowerRoleValue = 13791;

    private static ManualLogSource _log;
    private static bool _installed;
    private static Harmony _harmony;

    /// <summary>
    /// 按**名字**解析角色值（不迷信常量）：若别的 mod 先用同名成员注册了另一个数值，
    /// 我们的 patcher 会跳过（不重复加名），此时仍应跟随枚举里该名字的真实值，
    /// 否则运行期比较全部落空。名字不存在时退回给定的常量。
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

    private static readonly WildSpawnType Boss = ResolveRole(BossRoleName, BossRoleValue);
    private static readonly WildSpawnType Follower = ResolveRole(FollowerRoleName, FollowerRoleValue);

    /// <summary>原版"竞技场斗士"：本 mod 两个新角色的行为/随从/友好关系的参照物。</summary>
    private static readonly WildSpawnType EscortRole = WildSpawnType.arenaFighterEvent;

    internal static void Install(ManualLogSource log)
    {
        _log = log;
        if (_installed) return;
        _installed = true;

        try
        {
            // 0) 枚举是否真的注册了（prepatcher 产物在 BepInEx\patchers\）。两个角色逐个判定。
            var missing = new List<string>();
            foreach (var (name, value) in new[] { (BossRoleName, (int)Boss), (FollowerRoleName, (int)Follower) })
            {
                var defined = Enum.GetName(typeof(WildSpawnType), (WildSpawnType)value);
                if (!string.Equals(defined, name, StringComparison.Ordinal))
                    missing.Add($"{name}（解析值 {value} / 当前名字 = '{defined ?? "(未定义)"}'）");
            }
            if (missing.Count > 0)
            {
                _log?.LogError(
                    "[Scourge] ❌ 客户端 EFT.WildSpawnType 里缺少：" + string.Join("；", missing) + "\n" +
                    "   ⇒ 请确认 BepInEx\\patchers\\SPT-EventRotator.Patcher.dll 已安装（这是 preloader patcher，不是 plugins 里的插件）。\n" +
                    "   缺它：地图 JSON 里的这些角色会让客户端 Enum.Parse 抛异常（进不了图），" +
                    "服务端下发的 bot profile 也无法反序列化。本类将不做任何事，避免半套用。");
                return;
            }

            // 1) 行为元数据（逐字照抄 arenaFighterEvent，只换 ScavRoleKey ⇒ 显示名）。
            EnsureSpawnTypeSettings(Boss, BossRoleName, "ScavRole/ScourgeBoss");
            EnsureSpawnTypeSettings(Follower, FollowerRoleName, "ScavRole/ScourgeFollower");

            // 2) 随从适配：冠军 ⇄ 小弟 ⇄ 原版 arenaFighterEvent 三者互相适配
            //    （小弟既要能被冠军认作随从，也要能被 arenaFighterEvent 组认下 —— 兼容其它活动）。
            LinkSuitable(Boss, Follower);
            LinkSuitable(Boss, EscortRole);
            LinkSuitable(Follower, EscortRole);

            // 3) 行为对齐 arenaFighterEvent（只对本 mod 的两个角色生效，不影响任何原版 bot）。
            _harmony = new Harmony("com.kirisame.eventrotator.client.bossscourge");

            var activate = AccessTools.Method(typeof(StandartBotBrain), "Activate");
            if (activate != null)
            {
                _harmony.Patch(activate, null,
                    new HarmonyMethod(typeof(BossScourgeClient).GetMethod(nameof(BrainPostfix), BindingFlags.NonPublic | BindingFlags.Static))
                    { priority = 800 });
                _log?.LogInfo("[Scourge] ✔ Harmony postfix: StandartBotBrain.Activate → ArenaFighterLayersStrategy（真·竞技场脑）");
            }
            else _log?.LogWarning("[Scourge] ⚠ 找不到 StandartBotBrain.Activate（脑层不补，回退原版默认 SCAV 脑）");

            var chooser = AccessTools.Method(typeof(BotEnemyChooser), "Create", new[] { typeof(BotOwner) });
            if (chooser != null)
            {
                _harmony.Patch(chooser, null,
                    new HarmonyMethod(typeof(BossScourgeClient).GetMethod(nameof(EnemyChooserPostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                _log?.LogInfo("[Scourge] ✔ Harmony postfix: BotEnemyChooser.Create → ArenaFighterEnemyChooser");
            }

            var shall = AccessTools.Method(typeof(BotGroupWarnData), "ShallBossAttack");
            if (shall != null)
            {
                _harmony.Patch(shall,
                    new HarmonyMethod(typeof(BossScourgeClient).GetMethod(nameof(ShallBossAttackPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                _log?.LogInfo("[Scourge] ✔ Harmony prefix: BotGroupWarnData.ShallBossAttack → false（与 arenaFighterEvent 相同）");
            }

            // ★★★ 抄自 danauraborealis/Scourge 的 **BigBrain 小队战术层**（ScourgeAi/ 下 13 个文件）：
            //   Combat（交火/掩护/机动）、Escort（跟随护卫）、Recovery（受创后撤）、SquadSlots（站位配额）、
            //   Navigation（寻路/侧翼）、ProtectionPolicy、TacticalPolicy、CombatReadiness。
            //   ★ 只对 13790/13791 生效：注册层时带角色白名单，运行时再靠
            //     `ScourgeBotContext.Usable()` → `ScourgeRoles.IsScourge(Profile.Info.Settings.Role)` 门控
            //     ⇒ 不会影响任何原版 bot（含原版 arenaFighter / arenaFighterEvent）。
            //   ★ 同时让 SAIN 排除这两个角色（SainExclude），小队 AI 完全由本 mod 驱动。
            try
            {
                ScourgeAi.ScourgeAiLog.Source = _log;
                ScourgeAi.ScourgeAiConfig.Bind(new BepInEx.Configuration.ConfigFile(
                    System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "com.kirisame.eventrotator.client.scourgeai.cfg"), true));

                if (ScourgeAi.ScourgeAiRuntime.Initialize())
                    _log?.LogInfo("[Scourge] ✔ 小队战术层已注册（Combat/Escort/Recovery/SquadSlots，仅 bossScourge/followerScourge）");
                else
                    _log?.LogWarning("[Scourge] ⚠ 小队战术层未启用（回退原版 AI）: " + ScourgeAi.ScourgeAiRuntime.FailureReason);
            }
            catch (Exception ex)
            {
                _log?.LogWarning("[Scourge] 小队战术层初始化异常（回退原版 AI）: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            _log?.LogError("[Scourge] 客户端接线失败（回退原版行为，不影响其它 bot）: " + ex);
        }
    }

    /// <summary>给 `_spawnTypeSettings` 补一条（已存在则不覆盖），ScavRoleKey 决定客户端显示名。</summary>
    private static void EnsureSpawnTypeSettings(WildSpawnType role, string name, string scavRoleKey)
    {
        try
        {
            if (WildSpawnTypeExtension._spawnTypeSettings.ContainsKey(role)) return;
            WildSpawnTypeExtension._spawnTypeSettings[role] =
                new WildSpawnTypeSettings(isBoss: true, isFollower: true, isHostileToEverybody: true,
                                          scavRoleKey: scavRoleKey);
            _log?.LogInfo($"[Scourge] ✔ _spawnTypeSettings 已登记 {name}={(int)role}" +
                          $"（照抄 arenaFighterEvent：isBoss/isFollower/isHostileToEverybody=true, {scavRoleKey}）");
        }
        catch (Exception ex) { _log?.LogWarning($"[Scourge] ⚠ _spawnTypeSettings 登记 {name} 失败: {ex.Message}"); }
    }

    /// <summary>把两个角色在 `WildSpawnTypeExtension.ComparableCache` 里登记为互相"适配"（加法式，保留已有组）。</summary>
    private static void LinkSuitable(WildSpawnType a, WildSpawnType b)
    {
        try
        {
            var cache = WildSpawnTypeExtension.ComparableCache;
            if (cache == null) { _log?.LogWarning("[Scourge] ⚠ ComparableCache 为空，跳过随从适配"); return; }

            if (!cache.TryGetValue(a, out var setA)) cache[a] = setA = new HashSet<WildSpawnType>();
            setA.Add(b);
            if (!cache.TryGetValue(b, out var setB)) cache[b] = setB = new HashSet<WildSpawnType>();
            setB.Add(a);
            _log?.LogInfo($"[Scourge] ✔ ComparableCache: {a} ⇄ {b}（随从适配）");
        }
        catch (Exception ex) { _log?.LogWarning("[Scourge] ⚠ 随从适配失败: " + ex.Message); }
    }

    /// <summary>
    /// ★ 把"冠军 bossScourge"、"小弟 followerScourge"与"原版 arenaFighterEvent"改成互相友军。
    ///
    /// <para>为什么需要：<see cref="HostilityGuard.EnsureHostility"/> 对每个活动敌种的统一规则是
    /// 「AlwaysEnemies = 除自己外的全部枚举，AlwaysFriends = 只有自己」；这三个是**不同的枚举值**
    /// ⇒ 会被互相列入敌人 ⇒ 冠军打小弟、小弟打冠军。
    /// 这里只改这三条：各自从 AlwaysEnemies 移除另两个、把三者加入 AlwaysFriends，其它角色一律不动。</para>
    ///
    /// <para>调用点：<c>HostilityGuard.EnsureHostility()</c> 写完敌对表之后、`ToArray()` 之前。</para>
    /// </summary>
    internal static void EnsureSquadFriendly(List<AdditionalHostilitySettings> list)
    {
        try
        {
            if (list == null || list.Count == 0) return;
            var squad = new[] { Boss, Follower, EscortRole };
            int fixedCount = 0;

            foreach (var e in list)
            {
                if (e == null) continue;
                if (!squad.Contains(e.BotRole)) continue;

                var enemies = (e.AlwaysEnemies ?? Array.Empty<WildSpawnType>())
                              .Where(t => !squad.Contains(t)).ToArray();
                if (enemies.Length != (e.AlwaysEnemies?.Length ?? 0)) { e.AlwaysEnemies = enemies; fixedCount++; }

                var friends = e.AlwaysFriends ?? Array.Empty<WildSpawnType>();
                if (squad.Any(r => !friends.Contains(r)))
                {
                    e.AlwaysFriends = friends.Concat(squad).Distinct().ToArray();
                    fixedCount++;
                }
                // 显式标记为"对三派玩家都主动敌对"（EnsureHostility 已写；这里只是防御性重申）
                e.SavagePlayerBehaviour = EWarnBehaviour.AlwaysEnemies;
                e.BearPlayerBehaviour = EWarnBehaviour.AlwaysEnemies;
                e.UsecPlayerBehaviour = EWarnBehaviour.AlwaysEnemies;
            }

            if (fixedCount > 0)
                _log?.LogInfo($"[Scourge] ✔ 敌对表已修正：{BossRoleName} ⇄ {FollowerRoleName} ⇄ arenaFighterEvent 互相友军（调整 {fixedCount} 处；不涉及其它角色）");
        }
        catch (Exception ex)
        {
            _log?.LogWarning("[Scourge] ⚠ EnsureSquadFriendly 失败（保持原敌对表）: " + ex.Message);
        }
    }

    /// <summary>本 mod 的两个角色之一？</summary>
    private static bool IsScourge(BotOwner owner)
        => owner?.Profile?.Info?.Settings != null
           && (owner.Profile.Info.Settings.Role == Boss || owner.Profile.Info.Settings.Role == Follower);

    /// <summary>把本 mod 角色的脑层换成与 arenaFighterEvent 相同的 PmcLayersStrategy。</summary>
    private static void BrainPostfix(StandartBotBrain __instance, BotOwner ____owner)
    {
        try
        {
            if (!IsScourge(____owner)) return;                            // 只动本 mod 的两个角色
            if (__instance.BaseBrain is ArenaFighterLayersStrategy) return;   // 幂等（重复 Activate）

            __instance.BaseBrain?.Dispose();
            __instance.Agent?.Dispose();

            // ★ 2026-10-24 修正：本活动是「竞技场小队」，脑应是**真·竞技场脑** `arenaFighter`(34)，
            //   不是 `arenaFighterEvent`(35) 的 `PmcLayersStrategy`。依据 Assembly-CSharp：
            //   `case WildSpawnType.arenaFighter: BaseBrain = new ArenaFighterLayersStrategy(_owner);`（:70099-70101）
            //   `case WildSpawnType.arenaFighterEvent: … PmcLayersStrategy(withPursuit:false)`（:70102-70105）
            //   ★ 2026-10-30【掉帧修复 A】不能再用 ArenaFighterLayersStrategy 本名：SAIN 对 "ArenaFighter"
            //     无角色门槛预注册了 6 个战斗层，和 ScourgeAi 3 层叠在同一个脑上 ⇒ 每帧多 6 次层判定。
            //     改用 ScourgeBrain（同行为、只换 ShortName）⇒ SAIN 那 6 层不匹配，小队只剩 ScourgeAi 3 层。
            // ★★ 2026-10-31【排障开关】个别环境出现「首领站原地、随从正常」⇒ 允许回退到旧脑
            //   （旧脑会吃 SAIN 6 层，掉帧但兼容性最好；配置项见 Plugin ★灾厄小队用旧脑(排障)）
            ArenaFighterLayersStrategy brain;
            bool legacy = EventRotator.Client.Plugin.ScourgeLegacyBrain;
            // ★★ 2026-10-31【首领单独处理】实测反馈：换到 ScourgeBrain 后**首领站原地、随从正常**。
            //   首领整局只有 1 只 ⇒ 让它继续用旧脑（吃 SAIN 6 层）的性能代价可忽略，
            //   但行为与"改名之前"完全一致（那时首领是正常的）。
            //   随从数量多，仍然用 ScourgeBrain 拿掉重复层。
            bool isBoss = ____owner?.Profile?.Info?.Settings != null && (int)____owner.Profile.Info.Settings.Role == 13790;   // bossScourge（运行时枚举，不能写名字）
            // ★★ 2026-10-31【回到原设计】用户明确「我们之前让他们套用黑狐行为层」：
            //   默认 legacy=true ⇒ 全队 ArenaFighter 脑 ⇒ SAIN 的 BlackDiv(848420) 层生效（行为优先）。
            //   想省帧才把这个开关关掉（届时首领仍用旧脑保稳、随从换 ScourgeBrain 只吃 ScourgeAi 3 层）。
            if (legacy || isBoss)
                brain = new ArenaFighterLayersStrategy(____owner);
            else
                brain = new ScourgeAi.ScourgeBrain(____owner);
            __instance.BaseBrain = brain;
            __instance.Agent = new AICoreAgent<BotLogicDecision>(
                ____owner.BotsController.AICoreController,
                brain,
                AIActionsList.ActionsList(____owner),
                ____owner.gameObject,
                ____owner.name + " " + ____owner.Profile?.Info?.Settings?.Role,
                __instance.CG_Activate);

            // BigBrain / SAIN 在 StandartBotBrain.Activate 的 postfix 里靠这个事件注入层；
            // 我们换过 brain 后必须自己再触发一次（MoreBotsAPI 同款做法，见 MoreBotsPlugin :420-430）。
            var evField = typeof(StandartBotBrain).GetField("OnSetStrategy",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            (evField?.GetValue(__instance) as Action<BaseBrain>)?.Invoke(brain);

            try { ScourgeDebug.SetLog(m => _log?.LogInfo(m)); ScourgeDebug.Register(____owner, brain.GetType().Name); } catch { }
            _log?.LogInfo($"[Scourge] ✔ {____owner?.Profile?.Info?.Nickname}: brain → " + ((legacy || isBoss) ? "ArenaFighter（套 SAIN/黑狐层）" : "ScourgeBrain（掉帧模式·只吃 ScourgeAi 3 层）") + $"｜角色={____owner?.Profile?.Info?.Settings?.Role}");
        }
        catch (Exception ex)
        {
            _log?.LogWarning("[Scourge] ⚠ 替换脑层失败（保持原版默认脑）: " + ex.Message);
        }
    }

    /// <summary>arenaFighterEvent 专用的敌人选择器，照抄给本 mod 的两个角色。</summary>
    private static void EnemyChooserPostfix(BotOwner owner, ref BotEnemyChooser __result)
    {
        try
        {
            if (!IsScourge(owner)) return;
            __result = new ArenaFighterEnemyChooser(owner);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("[Scourge] ⚠ 替换 EnemyChooser 失败（保持默认）: " + ex.Message);
        }
    }

    /// <summary>`ShallBossAttack` 对 arenaFighterEvent 返回 false（Assembly-CSharp:58575-58594），本 mod 角色照抄。</summary>
    private static bool ShallBossAttackPrefix(BotGroupWarnData __instance, ref bool __result)
    {
        try
        {
            if (__instance?.Boss != null && IsScourge(__instance.Boss))
            {
                __result = false;   // false = 不因"BossNoAttack"而放过玩家（与 arenaFighterEvent 相同）
                return false;       // 跳过原方法
            }
        }
        catch { }
        return true;
    }
}
