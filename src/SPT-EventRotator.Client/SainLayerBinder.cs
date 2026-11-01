using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace EventRotator.Client
{
    /// <summary>
    /// ★★★ 核心修复：把 SAIN 的战斗层补齐到「活动敌种实际使用的 brain」上。
    ///
    /// ── 根因（已用源码 + 运行时探针双重确认）──
    ///  ① SAIN 只给名为 "ArenaFighter" 的 brain 挂战斗层：
    ///       AddCustomLayersToBloodHounds() { var brains = [nameof(EBrain.ArenaFighter)]; ... }
    ///  ② ABPS 追加生成的 arenaFighterEvent bot 实际用 StandartBotBrain
    ///     （SPT 的 bot.json 只有 assaultBrainType / playerScavBrainType，没有该角色的映射 → 落到默认 brain）
    ///  ③ BigBrain 的匹配规则（ExcludeLayerHelpers.IsAffectedBySettings）：
    ///       roles.Contains(botOwner.Profile.Info.Settings.Role)
    ///    && brainNames.Contains(botOwner.Brain.BaseBrain.ShortName())
    ///  ④ 层注入时机（BotBaseBrainActivatePatch）：只在 BaseBrain.Activate() 那一刻注入已注册的层
    ///     → **必须在 bot 生成之前注册好**
    ///  ⑤ 探针实测：BotActive=False / SAINLayersActive=False / ActiveLayer=None / CurrentAction=null
    ///     → 认知链全绿（EnemyKnown=True）却没有任何战斗动作可执行 → 完全无视玩家
    ///
    /// ── 修复 ──
    ///  在 bot 生成前，用 BigBrain 的 **public static** AddCustomLayer 注册 SAIN 的战斗层；
    ///  brain 名先用候选列表覆盖（StandartBotBrain 的 ShortName 事先无法确知），
    ///  等第一只活动敌种出现后再用它的真实 ShortName 补注册（为后续波次生效）。
    /// </summary>
    internal static class SainLayerBinder
    {
        private static readonly HashSet<string> _bound = new(StringComparer.OrdinalIgnoreCase);
        private static MethodInfo _addStatic;                 // ★ public static AddCustomLayer(Type, List<string>, int, List<WildSpawnType>)
        private static readonly List<Type> _layerTypes = new();
        private static readonly List<int> _layerPrios = new();
        private static bool _initTried, _initOk;
        private static object _roleList;                      // List<WildSpawnType>

        /// <summary>
        /// 候选 brain 短名 = SAIN 的 EBrain 枚举全部值（源码：SAIN.Preset.Shared/Enums/EBrain.cs）。
        /// ★ 不再猜：把全部 45 个都注册一遍。BigBrain 只按 ShortName 精确匹配，
        ///   不匹配的名字注册进去是空操作（不影响任何原版 bot），成本极低（一次性静态调用）。
        ///   实测走私者的 BaseBrain.ShortName() = 'PMC'，而 SAIN 给 PMC brain 挂层时带了角色限制，
        ///   不含 arenaFighterEvent → 所以必须自己按 "PMC" + arenaFighterEvent 补注册。
        /// </summary>
        private static readonly string[] CandidateBrains =
        {
            // ★ 掉帧修复 A：不再把 SAIN 层绑到 "ArenaFighter"（小队脑已是 ScourgeBrain；
            //   原版 arenaFighter 单位由 SAIN 自己的无门槛注册覆盖，不需要我们代绑）。
            "BossBully","BossGluhar","BossBoar","BossPartisan","Knight","BossKojaniy",
            "BossSanitar","BossKolontay","Tagilla","TagillaAgro","BossTest","Obdolbs","ExUsec","BigPipe",
            "BirdEye","FollowerBully","FollowerGluharAssault","FollowerGluharProtect","FollowerGluharScout",
            "FollowerKojaniy","FollowerSanitar","FlBoar","FlBoarCl","FlBoarSt","FlKlnAslt","KolonSec",
            "TagillaFollower","HelperAgro","Gifter","Killa","KillaAgro","Marksman","BoarSniper","PMC",
            "SectantPriest","SctPredvst","PrizrakSt","Oni","SectantWarrior","CursAssault","Assault",
            "PmcBear","PmcUsec","InfectedSlow",
            // 兜底：万一某些版本用类名
            "Standart","Standard","StandartBot","Default"
        };

        private static void Init()
        {
            // ★ 失败可重试（插件加载顺序不定：SAIN/BigBrain 可能还没加载完），但每次失败都记日志
            if (_initOk) return;
            try
            {
                var bmType = HostilityGuard.FindTypePublic("DrakiaXYZ.BigBrain.Brains.BrainManager");
                if (bmType == null) { NoteFail("找不到 DrakiaXYZ.BigBrain.Brains.BrainManager（BigBrain 未加载？）"); return; }

                // ★ public static，用 Invoke(null, ...)
                _addStatic = bmType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                   .FirstOrDefault(m => m.Name == "AddCustomLayer"
                                                     && m.GetParameters().Length == 4
                                                     && m.GetParameters()[0].ParameterType == typeof(Type));
                if (_addStatic == null) { NoteFail("找不到 static AddCustomLayer(4参)"); return; }

                void Add(string fullName, int prio)
                {
                    var t = HostilityGuard.FindTypePublic(fullName);
                    if (t != null) { _layerTypes.Add(t); _layerPrios.Add(prio); }
                    else NoteFail($"找不到层 {fullName}");
                }
                // ★ 命名空间以 SAIN 源码 SAIN/Layers/ 目录为准
                Add("SAIN.Layers.Debug.DebugLayer", 99);
                Add("SAIN.Layers.SAINAvoidThreatLayer", 80);
                Add("SAIN.Layers.Flashed.SAINFlashedLayer", 85);
                Add("SAIN.Layers.Extract.ExtractLayer", 50);
                Add("SAIN.Layers.Combat.Squad.CombatSquadLayer", 70);
                Add("SAIN.Layers.Combat.Solo.CombatSoloLayer", 60);

                if (_layerTypes.Count == 0) { HostilityGuard.DiagAlways("[SAIN层补齐] ❌ 一个 SAIN 层都没找到"); return; }

                // 角色列表（只对这些角色生效，不影响原版其他 bot）
                var roleType = HostilityGuard.FindTypePublic("EFT.WildSpawnType");
                if (roleType != null)
                {
                    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(roleType));
                    foreach (var r in HostilityGuard.HostileRolesList())
                    {
                        // ★ 灾厄小队（bossScourge/followerScourge）**不走 SAIN**：它们的 AI 由本 mod 的
                        //   BigBrain 战术层（ScourgeAi/，抄自 danauraborealis/Scourge）提供，且 SainExclude
                        //   已让 SAIN 排除这两个角色 ⇒ 这里不能再给它们挂 SAIN 战斗层（否则两套层互抢）。
                        if (string.Equals(r, "bossScourge", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(r, "followerScourge", StringComparison.OrdinalIgnoreCase))
                            continue;
                        try { list.Add(Enum.Parse(roleType, r, true)); } catch { }
                    }
                    if (list.Count > 0) _roleList = list;
                }

                _initOk = true;
                HostilityGuard.DiagAlways($"[SAIN层补齐] 初始化完成：{_layerTypes.Count} 个 SAIN 层，角色 {(_roleList == null ? "(全部)" : string.Join(",", HostilityGuard.HostileRolesList()))}");
            }
            catch (Exception ex) { HostilityGuard.DiagAlways("[SAIN层补齐] Init 异常: " + ex.Message); }
        }

        /// <summary>失败诊断（每次只报一次，避免刷屏）。</summary>
        private static readonly HashSet<string> _reported = new(StringComparer.Ordinal);
        private static void NoteFail(string why)
        {
            try { if (_reported.Add(why)) HostilityGuard.DiagAlways("[SAIN层补齐] ❌ " + why); } catch { }
        }

        /// <summary>在 bot 生成【之前】调用：按候选 brain 名预注册（关键，因为层只在 brain 激活时注入）。</summary>
        internal static void PreRegister()
        {
            Init();
            if (!_initOk) return;
            foreach (var b in CandidateBrains) Bind(b, "预注册");
        }

        // ══════════ ★★★ v1.0.1【性能】Ensure 的成员解析缓存 ══════════
        //   原来**每只 bot** 都做 3 次反射（`Brain` 属性 / `BaseBrain` 属性 / `ShortName` 方法）。
        //   这几处的 Type 在进程内恒定 ⇒ 按 Type 缓存解析结果即可（Type 变了自动重查）。
        //   ⚠ 注意 `Brain`/`BaseBrain`/`ShortName` **仍要每次 GetValue/Invoke**（值随 bot 变），
        //     这里只缓存"怎么找"，不缓存"找到的值" —— 语义与改前逐字相同。
        private static PropertyInfo _piBrain, _piBaseBrain;
        private static MethodInfo _miShortName;
        private static Type _tBotOwner, _tBrain, _tBaseBrain;

        /// <summary>看到真实的 bot 后调用：用它的真实 brain ShortName 补注册（对后续波次生效）。</summary>
        internal static void Ensure(object botOwner)
        {
            try
            {
                Init();
                if (!_initOk) return;
                if (botOwner == null) return;

                var ot = botOwner.GetType();
                if (ot != _tBotOwner)
                {
                    _piBrain = ot.GetProperty("Brain", BindingFlags.Public | BindingFlags.Instance);
                    _tBotOwner = ot;
                }
                var brain = _piBrain?.GetValue(botOwner);
                if (brain == null) return;

                var bt = brain.GetType();
                if (bt != _tBrain)
                {
                    _piBaseBrain = bt.GetProperty("BaseBrain", BindingFlags.Public | BindingFlags.Instance);
                    _tBrain = bt;
                }
                var baseBrain = _piBaseBrain?.GetValue(brain);
                if (baseBrain == null) return;

                var bbt = baseBrain.GetType();
                if (bbt != _tBaseBrain)
                {
                    _miShortName = bbt.GetMethod("ShortName", BindingFlags.Public | BindingFlags.Instance);
                    _tBaseBrain = bbt;
                }
                var shortName = _miShortName?.Invoke(baseBrain, null)?.ToString();
                if (string.IsNullOrEmpty(shortName)) return;
                Bind(shortName, "实测");
            }
            catch { }
        }

        /// <summary>
        /// ★ 2026-10-30【僵尸不挥刀】不给感染者/僵尸 brain 挂 SAIN 战斗层。
        ///   证据来源：本仓库 HostilityGuard.cs:1390-1400 的实测笔记——
        ///   「会打你的僵尸」和「不打你的僵尸」跑在不同脑层，实测**不打的那批正是 InfectedSlow**。
        ///   即：给 Infected* brain 挂 SAIN 层 ⇒ SAIN 的脑层接管决策 ⇒ 僵尸只追不挥刀。
        ///   关掉本开关可恢复旧行为（给感染者也挂 SAIN 层）。
        /// </summary>
        internal static bool SkipInfectedBrains = true;

        private static void Bind(string brainName, string why)
        {
            try
            {
                if (string.IsNullOrEmpty(brainName)) return;
                if (SkipInfectedBrains
                    && (brainName.IndexOf("Infected", StringComparison.OrdinalIgnoreCase) >= 0
                        || brainName.IndexOf("Zombie", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    if (_bound.Add("SKIP:" + brainName))
                        HostilityGuard.DiagAlways($"[SAIN层补齐] ⏭ 跳过感染者脑层 '{brainName}'（不给它挂 SAIN 战斗层 ⇒ 交还原版僵尸 AI，否则只追不挥刀）");
                    return;
                }
                // ★ 2026-10-31【灾厄掉帧】ScourgeBrain 是灾难小队专用脑（继承 ArenaFighterLayersStrategy，
                //   只换了 ShortName）⇒ SAIN 的战斗层不该再挂上去：小队由 ScourgeAi 的 3 层驱动。
                //   之前漏了这条，导致换名后又被"实测"路径补挂了 4 层 ⇒ 层重复依旧。
                if (brainName.IndexOf("Scourge", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (_bound.Add("SKIP:" + brainName))
                        HostilityGuard.DiagAlways($"[SAIN层补齐] ⏭ 跳过灾厄脑层 '{brainName}'（小队 AI 由 ScourgeAi 3 层驱动，不叠 SAIN 层）");
                    return;
                }

                if (!_bound.Add(brainName)) return;

                var brains = new List<string> { brainName };
                var args = _roleList != null
                    ? new object[] { null, brains, 0, _roleList }
                    : null;
                int ok = 0;
                for (int i = 0; i < _layerTypes.Count; i++)
                {
                    try
                    {
                        if (args != null)
                        {
                            args[0] = _layerTypes[i]; args[2] = _layerPrios[i];
                            _addStatic.Invoke(null, args);
                        }
                        else
                        {
                            // 退化：3 参重载（roles = 全部）
                            var m3 = _addStatic.DeclaringType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                              .FirstOrDefault(x => x.Name == "AddCustomLayer" && x.GetParameters().Length == 3);
                            if (m3 == null) return;
                            m3.Invoke(null, new object[] { _layerTypes[i], brains, _layerPrios[i] });
                        }
                        ok++;
                    }
                    catch (Exception ex2)
                    {
                        HostilityGuard.DiagAlways($"[SAIN层补齐] {_layerTypes[i].Name} 注册失败: {(ex2.InnerException ?? ex2).Message}");
                    }
                }
                HostilityGuard.DiagAlways($"[SAIN层补齐] ★ brain='{brainName}'（{why}）已注册 {ok}/{_layerTypes.Count} 个 SAIN 战斗层");
            }
            catch (Exception ex) { HostilityGuard.DiagAlways("[SAIN层补齐] Bind 异常: " + ex.Message); }
        }
    }
}
