using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace EventRotator.Client;

/// <summary>
/// ★★★ v0.1.5【同案例修复】SAIN 的僵尸近战补丁被我们自己摘掉。
///
/// **同案例来源（社区已确认）**：
///   · SAIN issue #278「Infected/Zombies no longer attempt to attack or swing」
///     —— 症状与用户实测**完全一致**："They still seek you out, but passively push you."（会走过来，但不打）
///   · issue #278 的评论（2025-10-28）给出根因：
///     「**RunToEnemyUpdatePatch** is the reason that zombies don't attack the player.
///       After removing this patch, I made 5 raids and zombies could always attack me.」
///   · PR #328「Fix zombies unable to attack the player」给出官方修法（`Patches/FixPatches.cs`）：
///       [PatchPrefix] public static bool Patch(BotMeleeWeaponData __instance) {
///           if (__instance.botOwner_0.AIData.UseZombieSimpleAnimator) return true;   // ← 僵尸放行原版近战
///           if (SAINEnableClass.GetSAIN(...) && bot.SAINLayersActive) { ... return false; }   // ← 非僵尸才接管
///       }
///     ⇒ 即 SAIN 的 prefix 对**僵尸**也 return false，把原版近战整段跳过 ⇒ 僵尸永远不挥刀。
///
/// **我们怎么用**：我们改不了 SAIN 的源码，所以在**客户端**把那个 prefix 从
/// `BotMeleeWeaponData.RunToEnemyUpdate` 上摘掉（等价于社区的验证做法"removing this patch"）。
/// 只摘**SAIN 的**、只摘**近战**那一个 prefix —— 其它 SAIN 行为一律不动。
/// 可用 F12 的「修复 SAIN 僵尸近战」开关关闭。
///
/// <para><b>★ 2026-10-30 修（首选路径被降级的根因）</b>：旧代码用
/// <c>aiDataType.GetField("UseZombieSimpleAnimator")</c> 取判据，而 <c>BotOwner.AIData</c> 的
/// 声明类型是**接口 <c>IAIData</c>**，该标志在接口上是**属性**
/// （实测元数据：<c>Boolean UseZombieSimpleAnimator { get; }</c>；实现类 <c>AIData</c> 的 getter
/// 读 <c>_botOwner.Profile.Info.Settings.UseSimpleAnimator</c>）⇒ GetField 永远 null ⇒ 每次启动都
/// 打 "找不到" 并降级到次选。现在改成 <see cref="FindFlagMember"/>（属性/字段 + 接口链 + 基类链）
/// 取**取值器**，首选路径（游戏方法 <c>BotMeleeWeaponData.RunToEnemyUpdate</c> 上的
/// Priority.Last / after=me.sol.sain 闸门）才能真正挂上。</para>
/// </summary>
internal static class SainZombieMeleeFix
{
    /// <summary>
    /// ★★ v1.3.12：**默认开启**。判据只认 `AIData.UseZombieSimpleAnimator`，对普通 bot 完全无影响，
    /// 所以即使本 mod 当前没有僵尸活动，开着也无害（有僵尸才有作用）。
    /// 想关可以用 F12 的「修复 SAIN 僵尸近战」开关。
    /// </summary>
    public static bool Enabled = true;

    private static bool _done;
    private static float _lastTry;
    private static ManualLogSource _log;

    /// <summary>告警：同时进 BepInEx 日志与控制台（安装失败时控制台也能直接看到原因）。</summary>
    private static void Warn(string msg)
    {
        try { Console.WriteLine("[EventRotator.Client]" + msg); } catch { }
        try { _log?.LogWarning(msg); } catch { }
    }

    /// <summary>本次采用的修复路径（只打一次，方便实机日志一眼看出走的哪条）。</summary>
    private static void LogChosen(string path)
        => _log?.LogInfo("[EventRotator.Client][SAIN僵尸近战修复] 修复路径=" + path + "（已生效，本局不再尝试其它路径）");

    internal static void SetLog(ManualLogSource log) => _log = log;

    /// <summary>周期性尝试（SAIN 的补丁可能比我们后加载 ⇒ 需要反复试，成功后不再做）。</summary>
    private static bool _tickLogged;
    private static float _lastErrLog;

    internal static void Tick()
    {
        try
        {
            if (!_tickLogged)
            {
                _tickLogged = true;
                _log?.LogInfo("[SAIN僵尸近战修复] Tick 已开始运行（协程活着）——Enabled=" + Enabled);
            }
            if (!Enabled || _done) return;
            if (UnityEngine.Time.realtimeSinceStartup - _lastTry < 5f) return;
            _lastTry = UnityEngine.Time.realtimeSinceStartup;
            // ★★★ v1.3.12【首选 · 与 PPES 1.4.0 / SAIN PR #328 同款的最小侵入做法】
            //   直接给**游戏方法** `BotMeleeWeaponData.RunToEnemyUpdate` 挂一个 Priority.Last 的 prefix，
            //   `after = ["me.sol.sain"]`（排在 SAIN 的 prefix 之后跑）。
            //   SAIN 的 prefix 对**所有** bot 都 `return false`（跳过原版近战）⇒ 僵尸追到人却不挥刀；
            //   我们这层只在「该 bot 用僵尸动画器」时把 `__runOriginal` 改回 true ⇒ 原版近战重新执行。
            //   优点：不依赖 SAIN 内部的类型/方法名（那些随版本改），只依赖游戏方法本身。
            if (TryGamePrefix()) { _done = true; InstallZombieHitSettlement(); LogChosen("首选(游戏方法闸门)"); return; }
            // ★ 次选：给 SAIN 的 prefix 外面再套一层僵尸闸门（依赖反射找 SAIN 的方法，版本一变就可能找不到）
            if (TrySurgical()) { _done = true; InstallZombieHitSettlement(); LogChosen("次选(SAIN prefix 闸门)"); return; }
            // 兜底：找不到就退回"直接摘除"（社区验证过的做法；代价：普通 bot 也回退到原版近战）
            if (TryRemove()) { _done = true; InstallZombieHitSettlement(); LogChosen("兜底(摘除 SAIN prefix)"); }
        }
        catch (Exception ex)
        {
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastErrLog > 30f)
            {
                _lastErrLog = now;
                _log?.LogWarning("[SAIN僵尸近战修复] Tick 异常（30 秒内只报一次）：" + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    // ───────────────────────── 首选：游戏方法上的 __runOriginal 闸门 ─────────────────────────
    // ★ 出处与署名（诚实标注）：
    //   这套"最小侵入"做法**不是我们自己想出来的**，也不是逐字抄的代码，而是照下面两个来源重写的实现：
    //     · SAIN PR #328「Fix zombies unable to attack the player」（提出判据）
    //     · PPES 1.4.0 `client/Plugin/Plugin.cs` 的 `InfectedMeleeFix`（MIT；把判据落成
    //       Priority.Last + after=["me.sol.sain"] 的 prefix + __runOriginal 闸门）
    //   我们的差异：① 用反射取类型/字段（不直接绑定 BotMeleeWeaponData 类型字面量），
    //               ② 保留原先两条降级路径（给 SAIN 的 prefix 套闸门 / 摘除），
    //               ③ 成功与否都打日志，失败可重试。
    //   许可：SAIN 与 PPES 均为 MIT，本文件属本 mod 代码，亦为 MIT。
    private static Type _meleeDataType;
    private static FieldInfo _ownerField;
    private static PropertyInfo _aiDataProp;
    /// <summary>僵尸判据成员名。<c>IAIData</c> 上它是**属性**（Boolean UseZombieSimpleAnimator { get; }），不是字段。</summary>
    private const string ZombieFlagName = "UseZombieSimpleAnimator";
    /// <summary>
    /// 僵尸标志的**取值器**（字段/属性统一封装）。
    /// <para>★★ 2026-10-30 修：旧实现是 <c>aiDataType.GetField("UseZombieSimpleAnimator")</c>，
    /// 而 <c>BotOwner.AIData</c> 的声明类型是**接口** <c>IAIData</c> —— 接口上没有字段 ⇒ 永远返回 null
    /// ⇒ 每次启动都打 "IAIData 上找不到 UseZombieSimpleAnimator" 然后降级到次选。
    /// 实测元数据：<c>IAIData.Boolean UseZombieSimpleAnimator { get; }</c>（实现类 <c>AIData</c> /
    /// <c>StubAIData</c> 同名公有属性）⇒ 必须用 <c>GetProperty</c>，并且要递归接口链/基类。</para>
    /// </summary>
    private static Func<object, object> _zombieFlagGetter;
    /// <summary>取值器的来源描述（打进日志，便于以后排查"到底读的哪个成员"）。</summary>
    private static string _zombieFlagWhere;

    /// <summary>
    /// 在类型上找成员（**字段或属性都接受**）：自身 → 接口链（递归）→ 基类链（递归）。
    /// <para>为什么三者都要试：接口上只有属性；实现类上可能显式实现（非公有）；不同游戏版本还可能挪到基类。</para>
    /// </summary>
    private static MemberInfo FindFlagMember(Type t, string name, int depth = 0)
    {
        if (t == null || depth > 8) return null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Instance | BindingFlags.Static;
        try
        {
            var p = t.GetProperty(name, flags);
            if (p != null && p.GetIndexParameters().Length == 0) return p;
            var f = t.GetField(name, flags);
            if (f != null) return f;
            foreach (var it in t.GetInterfaces())
            {
                var hit = FindFlagMember(it, name, depth + 1);
                if (hit != null) return hit;
            }
            return FindFlagMember(t.BaseType, name, depth + 1);
        }
        catch { return null; }
    }

    /// <summary>把 FieldInfo / PropertyInfo 统一成取值器（静态成员忽略 target）。取不到 getter 时返回 null。</summary>
    private static Func<object, object> MakeGetter(MemberInfo m)
    {
        try
        {
            if (m is FieldInfo f)
                return o => f.GetValue(f.IsStatic ? null : o);
            if (m is PropertyInfo p)
            {
                var get = p.GetGetMethod(true);
                if (get == null) return null;
                return o => get.Invoke(get.IsStatic ? null : o, null);
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 解析僵尸标志取值器：**声明类型**上找（接口 IAIData ⇒ 属性）→ 拿到字段/属性都行。
    /// <para>返回 null 才认定"首选路径失败"（由调用方降级）；非 null 时顺便把来源写进 <paramref name="where"/>。</para>
    /// </summary>
    private static Func<object, object> ResolveZombieFlagGetter(Type declaredType, out string where)
    {
        where = null;
        var m = FindFlagMember(declaredType, ZombieFlagName);
        if (m == null) return null;
        var getter = MakeGetter(m);
        if (getter == null) return null;
        where = (m.DeclaringType?.Name ?? "?") + "." + m.Name + "（" + (m is PropertyInfo ? "属性" : "字段") + "）";
        return getter;
    }

    /// <summary>取一只 bot 的「僵尸动画器」布尔值（取不到一律 false ⇒ 对普通 bot 完全无影响）。</summary>
    private static bool IsZombieAiData(object aid)
    {
        if (aid == null) return false;
        var getter = _zombieFlagGetter;
        if (getter == null) return false;
        return getter(aid) is true;
    }

    // ─────────────── 命中结算：补上被 UsedSimplifiedSkeleton 跳过的那一步 ───────────────
    // ★ 2026-10-30 实证（Assembly-CSharp IL，不是猜）：
    //   BaseKnifeController.IEventsConsumerOnFiringBullet():
    //        if (_player.UsedSimplifiedSkeleton) return;     ← 僵尸/感染者（ZOMBIE_BUNDLE）走这条，直接跳过
    //        OnFire();                                       ← 只有非简化骨架才结算命中
    //   BaseKnifeController.IEventsConsumerOnFireEnd(): 同样的 UsedSimplifiedSkeleton 早退
    //   BaseKnifeController.OnZombieFireBullet() → OnFire(); OnZombieFireEnd() → OnFireEnd();   ← 僵尸专用动画事件
    //   Kick.OnFire() → _knifeCollider.OnFire() → ProcessOverlap → ProcessHit
    //                → DamageInfo{DamageType=Melee(1024), Damage=刀模板 KnifeHitSlashDam/StabDam}
    //                → GameWorld.HackShot → BallisticCollider.ApplyHit → 掉血
    //   而 LocalPlayer/CG_Create: useSimplifiedSkeleton = profile.Info.Settings.UseSimpleAnimator
    //   ⇒ 感染者 bot 的 Player.UsedSimplifiedSkeleton == true ⇒ 上面那条 return 把整条伤害链掐断
    //     （动作有、血条几乎不掉，与实测完全一致）。
    //   修法：**只对**「UsedSimplifiedSkeleton + UseZombieSimpleAnimator」的 bot 把这一步补回去，
    //         等价于非僵尸 bot 的既有行为；其它 bot 原样不动。若僵尸专用事件同一击已结算过则跳过（防双倍）。
    private static bool _hitSettleInstalled;
    private static FieldInfo _knifePlayerField;      // ItemHandsController._player（public）
    private static PropertyInfo _playerAiDataProp;   // EFT.Player.AIData
    private static PropertyInfo _usedSimpleProp;     // EFT.Player.UsedSimplifiedSkeleton
    private static MethodInfo _onFireMethod;         // BaseKnifeController.OnFire
    private static MethodInfo _onFireEndMethod;      // BaseKnifeController.OnFireEnd
    private static bool _hitPathLogged;
    private static bool _hitErrLogged;
    private static readonly ConditionalWeakTable<object, float[]> _zombieEventStamp = new();

    private static void InstallZombieHitSettlement()
    {
        if (_hitSettleInstalled || !Enabled) return;
        try
        {
            var bkc = AccessTools.TypeByName("BaseKnifeController");
            var ihc = AccessTools.TypeByName("ItemHandsController");
            var playerType = AccessTools.TypeByName("EFT.Player") ?? AccessTools.TypeByName("Player");
            if (bkc == null || ihc == null || playerType == null)
            {
                Warn("[SAIN僵尸近战修复] 命中结算接管失败：找不到 BaseKnifeController / ItemHandsController / Player 类型");
                return;
            }
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            _knifePlayerField ??= ihc.GetField("_player", F);
            _playerAiDataProp ??= playerType.GetProperty("AIData", F);
            _usedSimpleProp ??= playerType.GetProperty("UsedSimplifiedSkeleton", F);
            _onFireMethod ??= bkc.GetMethod("OnFire", F);
            _onFireEndMethod ??= bkc.GetMethod("OnFireEnd", F);
            _knifeCompProp ??= bkc.GetProperty("Knife", F)?.GetGetMethod(true);
            if (_knifePlayerField == null || _usedSimpleProp == null || _onFireMethod == null || _onFireEndMethod == null)
            {
                Warn("[SAIN僵尸近战修复] 命中结算接管失败：成员缺失"
                               + $"（_player={_knifePlayerField != null}, UsedSimplifiedSkeleton={_usedSimpleProp != null},"
                               + $" OnFire={_onFireMethod != null}, OnFireEnd={_onFireEndMethod != null}）");
                return;
            }

            var h = new Harmony("com.kirisame.eventrotator.zombieknifehit");
            var prefix = new HarmonyMethod(typeof(SainZombieMeleeFix)
                .GetMethod(nameof(ZombieKnifeHitPrefix), BindingFlags.Public | BindingFlags.Static));
            int n = 0;
            foreach (var name in new[] { "IEventsConsumerOnFiringBullet", "IEventsConsumerOnFireEnd" })
            {
                var m = AccessTools.Method(bkc, name);
                if (m == null) { _log?.LogWarning("[SAIN僵尸近战修复] 找不到 BaseKnifeController." + name + "（跳过该条）"); continue; }
                h.Patch(m, prefix: prefix);
                n++;
            }
            var stamp = new HarmonyMethod(typeof(SainZombieMeleeFix)
                .GetMethod(nameof(ZombieNativeEventPrefix), BindingFlags.Public | BindingFlags.Static));
            foreach (var name in new[] { "OnZombieFireBullet", "OnZombieFireEnd" })
            {
                var m = AccessTools.Method(bkc, name);
                if (m != null) h.Patch(m, prefix: stamp);
            }
            _hitSettleInstalled = n > 0;
            InstallTryHitSettlement();
            InstallZombieSyntheticDamage();
            _log?.LogInfo("[EventRotator.Client][SAIN僵尸近战修复] ★命中结算已接管 BaseKnifeController.{IEventsConsumerOnFiringBullet, IEventsConsumerOnFireEnd}"
                        + "（只对 UsedSimplifiedSkeleton=true 且 UseZombieSimpleAnimator=true 的 bot 补 OnFire/OnFireEnd；其它 bot 原样）");
        }
        catch (Exception ex)
        {
            _log?.LogWarning("[SAIN僵尸近战修复] 命中结算接管异常: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// 原方法开头就是 <c>if (_player.UsedSimplifiedSkeleton) return;</c>（僵尸必中）⇒ 我们在这里代替它做结算。
    /// 返回 false = 跳过原方法（原方法对僵尸本来也只 return，不会重复结算）。
    /// </summary>
    public static bool ZombieKnifeHitPrefix(object __instance, MethodBase __originalMethod)
    {
        try
        {
            if (!Enabled || __instance == null) return true;
            var player = _knifePlayerField?.GetValue(__instance);
            if (player == null) return true;
            if (_usedSimpleProp?.GetValue(player) is not true) return true;      // 非简化骨架：原逻辑照旧
            var aid = _playerAiDataProp?.GetValue(player);
            if (!IsZombieAiData(aid)) return true;                              // 不是僵尸：不碰
            // 僵尸专用动画事件（OnZombieFire*）同一击已结算过 ⇒ 交给原逻辑（它会 return，不会重复）
            if (_zombieEventStamp.TryGetValue(player, out var stamp)
                && UnityEngine.Time.time - stamp[0] < 0.2f)
                return true;
            // 与 TryHit 兜底共用同一个去重窗口（按 controller 实例）
            if (_lastSettle.TryGetValue(__instance, out var st) && UnityEngine.Time.time - st[0] < 0.35f)
                return false;                       // 本击已由 TryHit 那条结算过 ⇒ 跳过原方法（原方法只会 return）
            _lastSettle.GetValue(__instance, _ => new float[1])[0] = UnityEngine.Time.time;

            var isFireEnd = (__originalMethod?.Name ?? "").IndexOf("FireEnd", StringComparison.Ordinal) >= 0;
            (isFireEnd ? _onFireEndMethod : _onFireMethod)?.Invoke(__instance, null);
            if (!_hitPathLogged)
            {
                _hitPathLogged = true;
                _log?.LogInfo("[SAIN僵尸近战修复] ✔ 僵尸刀命中结算已补上（原逻辑因 UsedSimplifiedSkeleton=true 跳过）"
                            + " ⇒ 之后走 Kick.OnFire → ProcessOverlap → ProcessHit → GameWorld.HackShot（只处理僵尸 bot，日志只打一次）");
            }
            return false;
        }
        catch (Exception ex)
        {
            if (!_hitErrLogged)
            {
                _hitErrLogged = true;
                _log?.LogWarning("[SAIN僵尸近战修复] 命中结算补丁异常（只报一次）: " + ex.GetType().Name + ": " + ex.Message);
            }
            return true;
        }
    }

    /// <summary>记录"僵尸专用动画事件"是否在跑（若在跑，说明原版路径已结算，我们的补丁就自动让路）。</summary>
    public static void ZombieNativeEventPrefix(object __instance)
    {
        try
        {
            var p = _knifePlayerField?.GetValue(__instance);
            if (p == null) return;
            _zombieEventStamp.GetValue(p, _ => new float[1])[0] = UnityEngine.Time.time;
        }
        catch { }
    }

    // ─────────── 第二道保险：TryHit 后直接结算（不依赖动画事件是否被派发） ───────────
    // 为什么需要：**动画事件是否派发**取决于僵尸 bundle 的 animator，不同版本可能不发 "FiringBullet"；
    // 而 `BotMeleeWeaponData.TryHit` 是僵尸挥刀的**唯一驱动点**（RunToEnemyUpdate → TryHit → MakeKnifeKick），
    // 在它返回时补一次 `OnFire()`（= 被跳过的那步）就能保证伤害结算，且天然只在"该挥刀"的时刻触发。
    // 与事件那条共用"同一次挥刀只结算一次"的时间戳（0.35s 窗口），两者不会叠加。
    private static bool _tryHitInstalled;
    private static PropertyInfo _knifeControllerProp;    // BotMeleeWeaponData.KnifeController
    private static readonly ConditionalWeakTable<object, float[]> _lastSettle = new();

    private static void InstallTryHitSettlement()
    {
        if (_tryHitInstalled || !Enabled) return;
        try
        {
            var mwd = AccessTools.TypeByName("BotMeleeWeaponData");
            if (mwd == null) return;
            // ★ 兜底路径也要能判僵尸：把 owner/AIData/取值器补齐（首选路径里已解析过就不重复）
            _meleeDataType ??= mwd;
            var botOwnerType = AccessTools.TypeByName("BotOwner");
            _ownerField ??= AccessTools.FindIncludingBaseTypes(mwd,
                t => AccessTools.GetDeclaredFields(t).FirstOrDefault(f => f.FieldType == botOwnerType));
            _aiDataProp ??= botOwnerType?.GetProperty("AIData");
            if (_zombieFlagGetter == null && _aiDataProp != null)
            {
                string ignored = null;
                _zombieFlagGetter = ResolveZombieFlagGetter(_aiDataProp.PropertyType, out ignored);
            }
            if (_ownerField == null || _aiDataProp == null || _zombieFlagGetter == null)
            {
                _log?.LogWarning("[SAIN僵尸近战修复] TryHit 结算兜底未装：僵尸判据未解析"
                               + $"（owner={_ownerField != null}, AIData={_aiDataProp != null}, flag={_zombieFlagGetter != null}）");
                return;
            }
            var tryHit = AccessTools.Method(mwd, "TryHit");
            if (tryHit == null) { _log?.LogWarning("[SAIN僵尸近战修复] 找不到 BotMeleeWeaponData.TryHit（命中结算兜底未装）"); return; }
            _knifeControllerProp ??= mwd.GetProperty("KnifeController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_knifeControllerProp == null) { _log?.LogWarning("[SAIN僵尸近战修复] 找不到 BotMeleeWeaponData.KnifeController（命中结算兜底未装）"); return; }
            var postfix = new HarmonyMethod(typeof(SainZombieMeleeFix)
                .GetMethod(nameof(ZombieTryHitPostfix), BindingFlags.Public | BindingFlags.Static));
            new Harmony("com.kirisame.eventrotator.zombietryhit").Patch(tryHit, postfix: postfix);
            _tryHitInstalled = true;
            _log?.LogInfo("[EventRotator.Client][SAIN僵尸近战修复] ★命中结算兜底已装：BotMeleeWeaponData.TryHit postfix"
                        + "（僵尸 TryHit 后补一次 OnFire，与动画事件那条共用去重窗口）");
        }
        catch (Exception ex) { Warn("[SAIN僵尸近战修复] TryHit 结算兜底安装异常: " + ex.GetType().Name + ": " + ex.Message); }
    }

    // ─────────── 合成伤害（用户口径 C：斜砍 70 / 刺 55）───────────
    // 背景（实机诊断 2026-10-30，逐字）：
    //   [诊断] 僵尸近战：MeleeWeaponEquipped=True HaveMelee=True Knife=KnifeTemplate 斜砍= 刺=
    //   ⇒ 感染者的 KnifeTemplate.KnifeHitSlashDam / KnifeHitStabDam 都是 0/未赋值 ⇒ 原版
    //     BaseKnifeController.ProcessHit 组出来的 DamageInfo.Damage = 0 ⇒ HackShot 打了但 0 伤害。
    // 口径来源：**用户 2026-10-30 拍板「C 档 = 斜砍 70 / 刺 55」**（写死在这里，将来有真值会被自动让路）。
    // 做法：在**原版结算链内部**的 ProcessHit 上加 prefix（不是另开一条触发路径）：
    //   · 该击实际会用的模板伤害 >= C 档下限     ⇒ 原样放行（不降伤害、不覆盖真值）；
    //   · 模板伤害取不到 / 低于 C 档下限         ⇒ 抬到 70/55，用这份 DamageInfo（其余字段照抄原版：
    //       DamageType=Melee(1024)、HitCollider/HitPoint/HitNormal、Direction=GlobalSwingDirection、
    //       Player=GameWorld.GetEverExistedBridgeByProfileID(attacker.ProfileId)、Weapon=Knife.Item、
    //       HittedBallisticCollider、IsForwardHit=true、ArmorDamage=1、StaminaBurnRate=模板值）
    //     然后调 GameWorld.HackShot(damageInfo) —— 护甲/部位/流血（ZombieBleedMul=10）全部仍走原逻辑。
    // ★ 我们**不自己加流血**：流血概率由 ActiveHealthController.ApplyDamage 里的 ZombieBleedMul 处理。
    private const int SyntheticSlashDamage = 70;   // 用户口径 C：**下限**（斜砍）
    private const int SyntheticStabDamage = 55;    // 用户口径 C：**下限**（刺）
    private static bool _syntheticInstalled;
    private static readonly ConditionalWeakTable<object, float[]> _lastSynth = new();
    private static MethodInfo _processHitMethod;
    private static Type _damageInfoType;
    private static Type _damageTypeEnum;
    private static PropertyInfo _playerGameWorldProp;  // EFT.Player.GameWorld（非静态，最稳）
    private static MethodInfo _gwHackShot;
    private static MethodInfo _gwBridgeByProfile;
    private static PropertyInfo _profileIdProp;
    private static FieldInfo _hitColliderField, _hitPointField, _hitNormalField;
    private static FieldInfo _knifeItemField;          // KnifeComponent.Item
    private static FieldInfo _swingDirField;           // BaseKnifeController.GlobalSwingDirection
    private static FieldInfo _lastKickTypeField;       // BaseKnifeController.LastKickType
    private static MethodInfo _slashPenGet, _stabPenGet, _staminaBurnGet;
    private static bool _syntheticLogged, _templateLogged;

    private static void InstallZombieSyntheticDamage()
    {
        if (_syntheticInstalled || !Enabled) return;
        try
        {
            var bkc = AccessTools.TypeByName("BaseKnifeController");
            var gwType = AccessTools.TypeByName("GameWorld");
            var hitType = AccessTools.TypeByName("KnifeRaycastHit");
            var kcType = AccessTools.TypeByName("KnifeComponent");
            var playerType = AccessTools.TypeByName("EFT.Player") ?? AccessTools.TypeByName("Player");
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            _processHitMethod ??= bkc?.GetMethod("ProcessHit", F);
            _damageInfoType ??= AccessTools.TypeByName("DamageInfo");
            // ★ GameWorld 是 MonoBehaviour（**不是** Singleton<T> 子类，没有 Instance）：实例从攻击者 Player 上取，
            //   实测元数据：EFT.Player 有非静态属性 GameWorld（get_GameWorld）⇒ 不需要任何泛型。
            _playerGameWorldProp ??= playerType?.GetProperty("GameWorld", F);
            _gwHackShot ??= gwType?.GetMethod("HackShot", F);
            _gwBridgeByProfile ??= gwType?.GetMethod("GetEverExistedBridgeByProfileID", F);
            _profileIdProp ??= AccessTools.TypeByName("EFT.Player")?.GetProperty("ProfileId", F);
            _hitColliderField ??= hitType?.GetField("collider", F);
            _hitPointField ??= hitType?.GetField("point", F);
            _hitNormalField ??= hitType?.GetField("normal", F);
            _knifeItemField ??= kcType?.GetField("Item", F);
            // 模板字段要在安装时就解析好（否则真值版本会被误判为"取不到"而用合成值）
            _knifeTemplateField ??= kcType?.GetField("Template", F);
            _knifeCompProp ??= bkc?.GetProperty("Knife", F)?.GetGetMethod(true);   // ProcessHit 里取 KnifeComponent 用
            _swingDirField ??= bkc?.GetField("GlobalSwingDirection", F);
            _lastKickTypeField ??= bkc?.GetField("LastKickType", F);
            if (_damageInfoType != null)
            {
                _damageTypeEnum ??= _damageInfoType.GetField("DamageType", F)?.FieldType;
                foreach (var p in _damageInfoType.GetFields(F))
                {
                    if (p.Name == "Damage" && p.FieldType != typeof(float)) { }
                }
            }
            // 模板伤害属性（Int32）
            if (_slashDamGet == null || _stabDamGet == null || _slashPenGet == null || _stabPenGet == null || _staminaBurnGet == null)
            {
                var tplIface = AccessTools.TypeByName("IKnifeComponentTemplate");
                if (tplIface != null)
                {
                    foreach (var p in tplIface.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        switch (p.Name)
                        {
                            case "KnifeHitSlashDam": _slashDamGet ??= p.GetGetMethod(); break;
                            case "KnifeHitStabDam": _stabDamGet ??= p.GetGetMethod(); break;
                            case "SlashPenetration": _slashPenGet ??= p.GetGetMethod(); break;
                            case "StabPenetration": _stabPenGet ??= p.GetGetMethod(); break;
                            case "StaminaBurnRate": _staminaBurnGet ??= p.GetGetMethod(); break;
                        }
                    }
                }
            }
            var missing = new List<string>();
            if (_processHitMethod == null) missing.Add("BaseKnifeController.ProcessHit");
            if (_damageInfoType == null) missing.Add("DamageInfo");
            if (_damageTypeEnum == null) missing.Add("DamageInfo.DamageType");
            if (_gwHackShot == null) missing.Add("GameWorld.HackShot");
            if (_playerGameWorldProp == null) missing.Add("Player.GameWorld");
            if (_gwBridgeByProfile == null) missing.Add("GameWorld.GetEverExistedBridgeByProfileID");
            if (_profileIdProp == null) missing.Add("Player.ProfileId");
            if (_hitColliderField == null || _hitPointField == null || _hitNormalField == null) missing.Add("KnifeRaycastHit.collider/point/normal");
            if (_knifeCompProp == null) missing.Add("BaseKnifeController.Knife");
            if (missing.Count > 0)
            {
                Warn("[SAIN僵尸近战修复] 合成伤害未装：缺成员 => " + string.Join("、", missing)
                               + "（GameWorld 类型=" + (gwType?.FullName ?? "null")
                               + "；Player 类型=" + (playerType?.FullName ?? "null") + "）");
                return;
            }
            try
            {
                var postfix = new HarmonyMethod(typeof(SainZombieMeleeFix)
                    .GetMethod(nameof(ZombieSyntheticDamagePostfix), BindingFlags.Public | BindingFlags.Static));
                new Harmony("com.kirisame.eventrotator.zombiedamage").Patch(_processHitMethod, postfix: postfix);
            }
            catch (Exception hex)
            {
                Warn("合成伤害未装：Harmony 打补丁失败 = " + Describe(hex));
                return;
            }
            _syntheticInstalled = true;
            _log?.LogInfo("[EventRotator.Client][SAIN僵尸近战修复] ★合成伤害已装：BaseKnifeController.ProcessHit postfix"
                        + "（模板伤害<=0 时用 70/55，有真值时不覆盖；GameWorld 取法=Player.GameWorld，无泛型）");
        }
        catch (Exception ex)
        {
            Warn("合成伤害未装：安装异常 = " + Describe(ex));
        }
    }

    /// <summary>把异常压成一行：类型 + 消息 + 最内层两帧（方便实机日志直接定位）。</summary>
    private static string Describe(Exception ex)
    {
        var e = ex;
        while (e.InnerException != null) e = e.InnerException;
        var frames = (e.StackTrace ?? "").Split(new[] { (char)10 }, StringSplitOptions.RemoveEmptyEntries);
        return ex.GetType().Name + ": " + e.Message + "｜最内层 " + e.GetType().Name + ": " + e.Message
             + (frames.Length > 0 ? "｜@ " + frames[0].Trim() : "");
    }

    /// <summary>
    /// 只在"僵尸 + 该击实际用的模板伤害 &lt;= 0"时补一份 70/55 的 DamageInfo 并走原版 HackShot。
    /// 用 **postfix**：原版 ProcessHit 先跑完（它自己打 0 伤害、返回有效的 PlayerHitInfo，
    /// 不会破坏 ClientKnifeController 的 KnifePacket 记录），我们再补真正的伤害。
    /// </summary>
    public static void ZombieSyntheticDamagePostfix(object __instance, object __0, object __1)
    {
        try
        {
            if (!Enabled || __instance == null) return;
            var player = _knifePlayerField?.GetValue(__instance);
            if (player == null) return;
            var aid = _playerAiDataProp?.GetValue(player);
            if (!IsZombieAiData(aid)) return;                             // 非僵尸：完全不动

            var knifeComp = _knifeCompProp?.Invoke(__instance, null);
            var tpl = knifeComp == null ? null : _knifeTemplateField?.GetValue(knifeComp);
            int slash = ToInt(_slashDamGet, tpl), stab = ToInt(_stabDamGet, tpl);
            bool useStab = _lastKickTypeField != null && !Equals(_lastKickTypeField.GetValue(__instance), Enum.ToObject(_lastKickTypeField.FieldType, 0));
            int tplDam = useStab ? stab : slash;
            int target = useStab ? SyntheticStabDamage : SyntheticSlashDamage;

            if (tplDam >= target)
            {
                if (!_templateLogged)
                {
                    _templateLogged = true;
                    _log?.LogInfo($"[SAIN僵尸近战修复][诊断] 僵尸近战结算：模板=(斜 {slash}/刺 {stab}) ⇒ 保留模板 {tplDam}"
                                + $"（本击 {(useStab ? "刺" : "斜")}；不低于下限，不改）");
                }
                return;                                                   // 模板不低于 C 档下限：原版已结算，不覆盖
            }

            // ★ 防双倍：同一次挥刀可能经 OnFire / OnFireEnd / ComboPlanning 多次进 ProcessHit（原版也如此），
            //   合成路径用自己的 0.2s 窗口（**不能**复用 _lastSettle：那是"已触发 OnFire"的戳，会把自己挡掉）。
            if (_lastSynth.TryGetValue(__instance, out var syn) && UnityEngine.Time.time - syn[0] < 0.2f) return;
            _lastSynth.GetValue(__instance, _ => new float[1])[0] = UnityEngine.Time.time;

            int dmg = ClampToFloor(tplDam, target);   // 走到这里 tplDam < target ⇒ 结果 == target
            var gw = _playerGameWorldProp?.GetValue(player);
            if (gw == null)
            {
                if (!_hitErrLogged) { _hitErrLogged = true; _log?.LogWarning("[SAIN僵尸近战修复] 合成伤害跳过：Player.GameWorld 为空"); }
                return;
            }

            var di = Activator.CreateInstance(_damageInfoType);            // 装箱的 struct
            SetDi(di, "DamageType", Enum.ToObject(_damageTypeEnum, 1024)); // Melee
            SetDi(di, "Damage", (float)dmg);
            SetDi(di, "PenetrationPower", (float)ToInt(useStab ? _stabPenGet : _slashPenGet, tpl));
            SetDi(di, "ArmorDamage", 1f);
            SetDi(di, "IsForwardHit", true);
            SetDi(di, "HitCollider", _hitColliderField.GetValue(__0));
            SetDi(di, "HitPoint", _hitPointField.GetValue(__0));
            SetDi(di, "HitNormal", _hitNormalField.GetValue(__0));
            SetDi(di, "Direction", _swingDirField?.GetValue(__instance));
            SetDi(di, "HittedBallisticCollider", __1);
            SetDi(di, "Player", _gwBridgeByProfile.Invoke(gw, new object[] { _profileIdProp.GetValue(player) }));
            SetDi(di, "Weapon", _knifeItemField?.GetValue(knifeComp));
            SetDi(di, "StaminaBurnRate", tpl != null ? Convert.ToSingle(_staminaBurnGet?.Invoke(tpl, null) ?? 0f) : 0f);
            _gwHackShot.Invoke(gw, new[] { di });

            if (!_syntheticLogged)
            {
                _syntheticLogged = true;
                _log?.LogInfo($"[SAIN僵尸近战修复][诊断] 僵尸近战结算：模板=(斜 {slash}/刺 {stab}) 目标=(斜 {SyntheticSlashDamage}/刺 {SyntheticStabDamage})"
                            + $" ⇒ 使用 {dmg}（本击 {(useStab ? "刺" : "斜")}；C 档下限）");
            }
        }
        catch (Exception ex)
        {
            if (!_hitErrLogged)
            {
                _hitErrLogged = true;
                _log?.LogWarning("[SAIN僵尸近战修复] 合成伤害异常（只报一次）: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    /// <summary>C 档下限：模板低于目标才抬到目标；等于/高于保留模板（不降伤害）。</summary>
    private static int ClampToFloor(int tplDam, int target) => tplDam >= target ? tplDam : target;

    private static int ToInt(MethodInfo getter, object target)
    {
        try { var v = getter?.Invoke(target, null); return v == null ? 0 : Convert.ToInt32(v); }
        catch { return 0; }
    }

    private static void SetDi(object di, string field, object value)
    {
        try { _damageInfoType?.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(di, value); }
        catch { }
    }

    // ─────────── 一次性诊断：把"僵尸到底有没有刀/伤害模板是多少"打进日志 ───────────
    // 用户反馈"有动作没伤害"时，这一行能直接回答：MeleeWeaponEquipped / HaveMelee / Knife / 刀模板伤害。
    private static bool _diagLogged;
    private static MethodInfo _meleeEquippedProp;   // BotMeleeWeaponData.MeleeWeaponEquipped
    private static MethodInfo _haveMeleeProp;       // BotMeleeWeaponData.HaveMelee
    private static MethodInfo _knifeCompProp;       // BaseKnifeController.Knife
    private static FieldInfo _knifeTemplateField;   // KnifeComponent.Template
    private static MethodInfo _slashDamGet;         // IKnifeComponentTemplate.KnifeHitSlashDam
    private static MethodInfo _stabDamGet;          // IKnifeComponentTemplate.KnifeHitStabDam

    private static void LogMeleeDiag(object mwd, object kc)
    {
        if (_diagLogged) return;
        _diagLogged = true;
        try
        {
            var tMwd = mwd.GetType();
            _meleeEquippedProp ??= tMwd.GetProperty("MeleeWeaponEquipped")?.GetGetMethod(true);
            _haveMeleeProp ??= tMwd.GetProperty("HaveMelee")?.GetGetMethod(true);
            var knife = _knifeCompProp?.Invoke(kc, null);
            string knifeInfo = "Knife=null";
            if (knife != null)
            {
                var compType = knife.GetType();
                _knifeTemplateField ??= compType.GetField("Template", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var tpl = _knifeTemplateField?.GetValue(knife);
                if (tpl != null)
                {
                    var tplType = tpl.GetType();
                    if (_slashDamGet == null)
                        foreach (var p in tplType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (p.Name == "KnifeHitSlashDam") _slashDamGet = p.GetGetMethod();
                            else if (p.Name == "KnifeHitStabDam") _stabDamGet = p.GetGetMethod();
                        }
                    var slash = _slashDamGet?.Invoke(tpl, null);
                    var stab = _stabDamGet?.Invoke(tpl, null);
                    knifeInfo = $"Knife={tplType.Name} 斜砍={slash} 刺={stab}";
                }
                else knifeInfo = "Knife=有但Template=null";
            }
            var eq = _meleeEquippedProp?.Invoke(mwd, null);
            var have = _haveMeleeProp?.Invoke(mwd, null);
            _log?.LogInfo($"[SAIN僵尸近战修复][诊断] 僵尸近战：MeleeWeaponEquipped={eq} HaveMelee={have} {knifeInfo}");
        }
        catch (Exception ex) { _log?.LogWarning("[SAIN僵尸近战修复][诊断] 采集失败: " + ex.GetType().Name + ": " + ex.Message); }
    }

    /// <summary>僵尸 TryHit 后：若玩家是简化骨架（僵尸）且本击还没结算过，就补一次 OnFire()。</summary>
    public static void ZombieTryHitPostfix(object __instance)
    {
        try
        {
            if (!Enabled || __instance == null) return;
            var owner = _ownerField?.GetValue(__instance);                    // BotData._owner
            if (owner == null) return;
            if (!IsZombieAiData(_aiDataProp?.GetValue(owner))) return;        // 只处理僵尸
            var kc = _knifeControllerProp?.GetValue(__instance);              // IKnifeController
            if (kc == null) return;
            // 只有真正的 BaseKnifeController 才有 OnFire（Observed 等实现不碰）
            if (_onFireMethod == null || !_onFireMethod.DeclaringType.IsInstanceOfType(kc)) return;
            LogMeleeDiag(__instance, kc);
            var player = _knifePlayerField?.GetValue(kc);
            if (player == null) return;
            if (_usedSimpleProp?.GetValue(player) is not true) return;        // 非简化骨架：正常事件会结算，别抢
            // ★ 只在"动画事件那条没装上"时才用这里兜底：正常情况由 IEventsConsumerOnFiringBullet 统一结算
            //   （那里才是原版的时间点；这里提前结算会早于刀扫到人的那一帧，而且会和事件那条撞车）。
            if (_hitSettleInstalled) return;
            if (_zombieEventStamp.TryGetValue(player, out var stamp) && UnityEngine.Time.time - stamp[0] < 0.2f) return;
            if (_lastSettle.TryGetValue(kc, out var st) && UnityEngine.Time.time - st[0] < 0.35f) return;
            _lastSettle.GetValue(kc, _ => new float[1])[0] = UnityEngine.Time.time;
            _onFireMethod?.Invoke(kc, null);
            if (!_hitPathLogged)
            {
                _hitPathLogged = true;
                _log?.LogInfo("[SAIN僵尸近战修复] ✔ 僵尸刀命中结算已接管（TryHit→OnFire→ProcessOverlap→ProcessHit→HackShot；只处理僵尸，日志只打一次）");
            }
        }
        catch (Exception ex)
        {
            if (!_hitErrLogged)
            {
                _hitErrLogged = true;
                _log?.LogWarning("[SAIN僵尸近战修复] TryHit 结算异常（只报一次）: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    private static bool TryGamePrefix()
    {
        _meleeDataType ??= AccessTools.TypeByName("BotMeleeWeaponData");
        if (_meleeDataType == null) { _log?.LogWarning("[SAIN僵尸近战修复] 找不到 BotMeleeWeaponData 类型"); return false; }
        var target = AccessTools.Method(_meleeDataType, "RunToEnemyUpdate");
        if (target == null) { _log?.LogWarning("[SAIN僵尸近战修复] 找不到 BotMeleeWeaponData.RunToEnemyUpdate"); return false; }

        // owner 字段在基类上，名字随游戏版本变 ⇒ 按「类型是 BotOwner」找
        var botOwnerType = AccessTools.TypeByName("BotOwner");
        _ownerField ??= AccessTools.FindIncludingBaseTypes(_meleeDataType,
            t => AccessTools.GetDeclaredFields(t).FirstOrDefault(f => f.FieldType == botOwnerType));
        if (_ownerField == null) { _log?.LogWarning("[SAIN僵尸近战修复] BotMeleeWeaponData 里找不到 BotOwner 字段"); return false; }
        _aiDataProp ??= botOwnerType?.GetProperty("AIData");
        if (_aiDataProp == null) { _log?.LogWarning("[SAIN僵尸近战修复] BotOwner 上找不到 AIData"); return false; }
        var aiDataType = _aiDataProp.PropertyType;
        // ★★ 2026-10-30 修：用 FindFlagMember（属性/字段 + 接口链 + 基类链）取**取值器**；
        //   旧写法 `aiDataType.GetField(...)` 在接口 IAIData 上必然拿不到 ⇒ 永远降级。
        string flagWhere = null;
        if (_zombieFlagGetter == null) _zombieFlagGetter = ResolveZombieFlagGetter(aiDataType, out flagWhere);
        if (_zombieFlagGetter == null)
        {
            _log?.LogWarning($"[SAIN僵尸近战修复] {aiDataType.Name} 上找不到 {ZombieFlagName}"
                           + "（已依次试过：属性 / 字段 / 接口链 / 基类链）");
            return false;
        }
        _zombieFlagWhere ??= flagWhere;
        _log?.LogInfo($"[SAIN僵尸近战修复] 僵尸判据定位：{aiDataType.Name}.{_zombieFlagWhere}"
                    + "（读的是" + (aiDataType.IsInterface ? "接口属性，运行期按实现类分派" : "成员") + "）");

        var h = new Harmony("com.kirisame.eventrotator.infectedmelee");
        var prefix = new HarmonyMethod(typeof(SainZombieMeleeFix)
            .GetMethod(nameof(InfectedRunOriginalPrefix), BindingFlags.Public | BindingFlags.Static))
        {
            priority = Priority.Last,
            after = new[] { "me.sol.sain" },
        };
        h.Patch(target, prefix: prefix);
        // ★ 这一行是实机排查的关键锚点：**逐字**按约定打，后面不追加内容（grep 用）
        _log?.LogInfo("[EventRotator.Client][SAIN僵尸近战修复] ★已给游戏方法 BotMeleeWeaponData.RunToEnemyUpdate 挂上僵尸闸门（Priority.Last / after=me.sol.sain）");
        _log?.LogInfo("[EventRotator.Client][SAIN僵尸近战修复] 只对 UseZombieSimpleAnimator=true 的 bot 放行原版近战"
                    + "（判据 " + _zombieFlagWhere + "）｜修复路径=首选(游戏方法闸门)");
        return true;
    }

    /// <summary>僵尸闸门：SAIN 的 prefix 把 <c>__runOriginal</c> 置 false 后，这里只对僵尸改回 true。</summary>
    public static void InfectedRunOriginalPrefix(object __instance, ref bool __runOriginal)
    {
        try
        {
            if (__runOriginal || __instance == null) return;      // 没人拦 ⇒ 原版本来就会跑，不用管
            var owner = _ownerField?.GetValue(__instance);
            if (owner == null) return;
            var aid = _aiDataProp?.GetValue(owner);
            if (aid == null) return;
            if (IsZombieAiData(aid))                              // ★ 用取值器（接口属性/字段都行），不再用 GetField
            {
                __runOriginal = true;                             // 僵尸 ⇒ 恢复原版近战
                if (!_loggedOnce)
                {
                    _loggedOnce = true;
                    _log?.LogInfo("[SAIN僵尸近战修复] ✔ 已放行一只僵尸的原版近战（判据 " + _zombieFlagWhere
                                + "；之后不再逐只打印）｜修复路径=首选(游戏方法闸门)");
                }
            }
        }
        catch { }
    }

    private static bool _loggedOnce;

    private static bool TrySurgical()
    {
        int patched = 0;
        foreach (var item in FindSainMeleePrefixes())
        {
            try
            {
                var h = new Harmony("com.kirisame.eventrotator.sainzombiemeleeprefix");
                var prefixMethod = new HarmonyMethod(typeof(SainZombieMeleeFix)
                    .GetMethod(nameof(ZombieGatePrefix), BindingFlags.Public | BindingFlags.Static));
                h.Patch(item.m, prefix: prefixMethod);
                patched++;
                _log?.LogInfo($"[EventRotator.Client][SAIN僵尸近战修复] ★已给 SAIN 补丁 {item.t.Name}.{item.m.Name} 套上僵尸闸门"
                            + "（僵尸跳过 SAIN 接管、普通 bot 行为不变；等价于官方 PR #328 的判据）");
            }
            catch (Exception ex) { _log?.LogWarning($"[EventRotator.Client][SAIN僵尸近战修复] 套闸门失败 {item.t.Name}.{item.m.Name}: {ex.Message}"); }
        }
        if (patched > 0) { _log?.LogInfo($"[EventRotator.Client][SAIN僵尸近战修复] 共套上 {patched} 个僵尸闸门 ｜修复路径=次选(SAIN prefix 闸门)"); return true; }
        return false;
    }

    /// <summary>
    /// 套在 SAIN 近战 prefix 外面的"僵尸闸门"（等于官方 PR #328 的判据 + 我们自己的角色判据）：
    ///   · 僵尸/感染者 ⇒ 返回 false（跳过 SAIN 的 prefix）⇒ 原版近战继续 ⇒ 僵尸会挥刀
    ///   · 其他        ⇒ 返回 true（SAIN 的 prefix 照常运行）⇒ 普通 bot 行为完全不变
    ///
    /// ★★ v0.1.5 实测修正：SAIN 的 `RunToEnemyUpdatePatch.Patch` 是**静态**方法，首参是
    ///   `BotMeleeWeaponData __instance`（那是 Harmony 的注入名）。我第一版把这个参数也命名成 `__instance`，
    ///   Harmony 对**静态目标方法**不会注入它 ⇒ 实测 `AIData=null、role=空` ⇒ 闸门永远放行（等于没装）。
    ///   正确写法是按位置取实参 **`__0`**。
    /// </summary>
    public static bool ZombieGatePrefix(object __0)
    {
        try
        {
            var inst = __0;
            // ★★ v0.1.5 实测修正 2：**不要按名字找**！`botOwner_0` 是 ILSpy 的臆造名（真实字段名不是它），
            //   所以改成**按类型名自动搜索**成员的持有者（字段+属性，含基类），并把找到的名字打进日志。
            var owner = FindOwner(inst);
            var aid = FindByTypeName(owner, "AIData");
            var flag = aid == null ? null : (FFP(aid, "UseZombieSimpleAnimator") ?? FindBoolFlag(aid));
            bool isZombieFlag = flag is bool b && b;
            string role = null;
            try { role = HostilityGuard.RoleOfSettings(FFP(owner, "Settings") ?? FindByTypeName(owner, "BotSettings")); } catch { }
            bool isInfectedRole = !string.IsNullOrEmpty(role)
                && (role.StartsWith("infected", StringComparison.OrdinalIgnoreCase)
                    || HostilityGuard.HostileRoles.Contains(role));
            bool skip = isZombieFlag || isInfectedRole;
            try
            {
                if (UnityEngine.Time.realtimeSinceStartup - _lastGateLog > 5f)
                {
                    _lastGateLog = UnityEngine.Time.realtimeSinceStartup;
                    _log?.LogInfo($"[EventRotator.Client][SAIN僵尸近战修复] 闸门测量：inst={(inst == null ? "null ❌" : inst.GetType().Name)}"
                                + $"　owner={(owner == null ? "null ❌" : owner.GetType().Name)}"
                                + $"　role={(string.IsNullOrEmpty(role) ? "(空)" : role)}"
                                + $"　AIData={(aid == null ? "null" : aid.GetType().Name)}"
                                + $"　UseZombieSimpleAnimator={(flag == null ? "取不到" : flag.ToString())}"
                                + $"　⇒ {(skip ? "★跳过 SAIN（僵尸/感染者）" : "放行 SAIN（普通 bot）")}");
                }
            }
            catch { }
            return !skip;
        }
        catch { return true; }
    }

    /// <summary>在对象（含基类）的字段/属性里找一个"值是 BotOwner"的成员。</summary>
    private static object FindOwner(object o)
    {
        if (o == null) return null;
        return FindByTypeName(o, "BotOwner");
    }

    /// <summary>在对象的字段/属性（含基类）里，找**值的类型名**包含 typePart 的第一个成员并返回其值。</summary>
    private static object FindByTypeName(object o, string typePart)
    {
        if (o == null) return null;
        var t = o.GetType();
        var seen = new HashSet<string>();
        while (t != null && t != typeof(object))
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!seen.Add(f.Name)) continue;
                if (f.FieldType.Name.IndexOf(typePart, StringComparison.OrdinalIgnoreCase) < 0) continue;
                try { var v = f.GetValue(o); if (v != null) return v; } catch { }
            }
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (!seen.Add(p.Name)) continue;
                if (p.PropertyType.Name.IndexOf(typePart, StringComparison.OrdinalIgnoreCase) < 0) continue;
                try { var v = p.GetValue(o); if (v != null) return v; } catch { }
            }
            t = t.BaseType;
        }
        return null;
    }

    /// <summary>在 AIData 里找任意一个"名字含 Zombie/SimpleAnimator"的 bool 标志（判据自动发现）。</summary>
    private static object FindBoolFlag(object aid)
    {
        if (aid == null) return null;
        foreach (var f in aid.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (f.FieldType != typeof(bool)) continue;
            if (f.Name.IndexOf("Zombie", StringComparison.OrdinalIgnoreCase) < 0
                && f.Name.IndexOf("SimpleAnimator", StringComparison.OrdinalIgnoreCase) < 0) continue;
            try { return f.GetValue(aid); } catch { }
        }
        foreach (var p in aid.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.PropertyType != typeof(bool)) continue;
            if (p.Name.IndexOf("Zombie", StringComparison.OrdinalIgnoreCase) < 0
                && p.Name.IndexOf("SimpleAnimator", StringComparison.OrdinalIgnoreCase) < 0) continue;
            try { return p.GetValue(aid); } catch { }
        }
        return null;
    }

    private static float _lastGateLog;

    private static object FFP(object o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        return t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o)
            ?? t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(o);
    }

    /// <summary>找出 SAIN 针对 `BotMeleeWeaponData` 的近战 prefix（类型名含 RunToEnemyUpdate / FixPatches）。</summary>
    private static IEnumerable<(Type t, MethodInfo m, Type targetType, MethodInfo targetMethod)> FindSainMeleePrefixes()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            bool isSain;
            try { isSain = asm.GetName().Name?.IndexOf("SAIN", StringComparison.OrdinalIgnoreCase) >= 0; } catch { isSain = false; }
            if (!isSain) continue;
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types)
            {
                if (t.Name.IndexOf("RunToEnemyUpdate", StringComparison.OrdinalIgnoreCase) < 0
                    && t.Name.IndexOf("FixPatches", StringComparison.OrdinalIgnoreCase) < 0) continue;
                MethodInfo[] methods;
                try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance); }
                catch { continue; }
                foreach (var m in methods)
                {
                    var ps = m.GetParameters();
                    if (ps.Length == 0) continue;
                    if (ps[0].ParameterType.Name.IndexOf("BotMeleeWeaponData", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var targetType = ps[0].ParameterType;
                    foreach (var tm in targetType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        HarmonyLib.Patches info = null;
                        try { info = Harmony.GetPatchInfo(tm); } catch { }
                        if (info?.Prefixes == null) continue;
                        if (!info.Prefixes.Any(p => p.PatchMethod == m)) continue;
                        yield return (t, m, targetType, tm);
                    }
                }
            }
        }
    }

    private static bool TryRemove()
    {
        var sainAsms = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => { try { return a.GetName().Name?.IndexOf("SAIN", StringComparison.OrdinalIgnoreCase) >= 0; } catch { return false; } })
            .ToList();
        if (sainAsms.Count == 0) return false;

        // 需要移除的 prefix：声明类型名含 RunToEnemyUpdate / FixPatches，且参数里带 BotMeleeWeaponData
        var mine = new Harmony("com.kirisame.eventrotator.sainzombiefix");
        int removed = 0;

        foreach (var asm in sainAsms)
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types)
            {
                if (t.Name.IndexOf("RunToEnemyUpdate", StringComparison.OrdinalIgnoreCase) < 0
                    && t.Name.IndexOf("FixPatches", StringComparison.OrdinalIgnoreCase) < 0) continue;

                MethodInfo[] methods;
                try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance); }
                catch { continue; }

                foreach (var m in methods)
                {
                    var ps = m.GetParameters();
                    if (ps.Length == 0) continue;
                    if (ps[0].ParameterType.Name.IndexOf("BotMeleeWeaponData", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    // 目标方法：BotMeleeWeaponData 上被打了这个 prefix 的那个
                    var targetType = ps[0].ParameterType;
                    foreach (var tm in targetType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        HarmonyLib.Patches info = null;   // ★ Harmony 2.x：GetPatchInfo 返回 Patches（含 Prefixes/Postfixes）
                        try { info = Harmony.GetPatchInfo(tm); } catch { }
                        if (info?.Prefixes == null) continue;
                        if (!info.Prefixes.Any(p => p.PatchMethod == m)) continue;
                        try
                        {
                            mine.Unpatch(tm, m);
                            removed++;
                            _log?.LogInfo($"[EventRotator.Client][SAIN僵尸近战修复] ★已摘除 SAIN 补丁 {t.Name}.{m.Name} ← {targetType.Name}.{tm.Name}"
                                        + "（同案例：SAIN issue #278 / PR #328；摘掉后僵尸会恢复原版近战攻击）");
                        }
                        catch (Exception ex) { _log?.LogWarning($"[EventRotator.Client][SAIN僵尸近战修复] 摘除失败 {t.Name}.{m.Name}: {ex.Message}"); }
                    }
                }
            }
        }

        if (removed > 0)
        {
            _log?.LogInfo($"[EventRotator.Client][SAIN僵尸近战修复] 共摘除 {removed} 个 prefix（SAIN 其它补丁未动）｜修复路径=兜底(摘除 SAIN prefix)");
            return true;
        }
        return false;
    }
}
