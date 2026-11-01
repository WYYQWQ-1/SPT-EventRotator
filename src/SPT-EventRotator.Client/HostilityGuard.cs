using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EFT;

namespace EventRotator.Client;

/// <summary>
/// 【敌对性兜底】服务端的 AdditionalHostilitySettings 改了但客户端不一定按它走
/// （客户端判定链：BotsGroup.IsPlayerEnemy → BotSettings.IsPlayerEnemy →
///   Singleton&lt;IBotGame&gt;.Instance.BotsController.BotLocationModifier.AdditionalHostilitySettings，
///   而 BotLocationModifier 是 BotsController 上的**独立字段**，未必等于地图数据里的那一份）。
///
/// 所以这里在客户端直接兜底：只要"发起判定的那只 bot 的角色"属于活动指定的敌种，
/// 就判定玩家为敌人。**只影响列在 forceHostilityHostile 里的角色**，其他 bot 一律走原逻辑。
/// </summary>
internal static class HostilityGuard
{
    /// <summary>
    /// 【硬保险 · 永不敌对】这些是原版"中立/特殊"单位，无论 events.json 怎么配都不许动：
    ///   shooterBTR            BTR 装甲车（中立巡逻，打它是玩家主动敌对）
    ///   gifter                圣诞老人（送礼物的中立单位）
    ///   peacefullZryachiyEvent / ravangeZryachiyEvent   扎里亚奇事件单位
    ///   skier                 滑雪者（活动中立）
    ///   bossZryachiy / followerZryachiy   灯塔岛上的中立狙击手（纯守卫）
    /// 目的：保证本 mod 只影响"活动敌种"，绝不污染原版中立行为。
    /// </summary>
    /// <summary>`bossSpawns`/`bossChance` 里出现 "*" 时代表"全部首领"，用这份安全名单展开（已剔除中立单位）。</summary>
    private static readonly string[] AllBossRoles =
    {
        "bossBully", "followerBully", "bossKilla", "bossKojaniy", "followerKojaniy",
        "bossGluhar", "followerGluharAssault", "followerGluharSecurity", "followerGluharScout", "followerGluharSnipe",
        "bossSanitar", "followerSanitar", "bossTagilla", "bossKnight", "followerBigPipe", "followerBirdEye",
        "bossPartisan", "sectantPriest", "sectantWarrior", "bossBoar", "followerBoar", "bossBoarSniper",
        "pmcBot", "exUsec", "cursedAssault", "marksman", "assault",
    };

    private static readonly HashSet<string> NeverHostile = new(StringComparer.OrdinalIgnoreCase)
    {
        "shooterBTR", "gifter", "peacefullZryachiyEvent", "ravangeZryachiyEvent",
        "skier", "bossZryachiy", "followerZryachiy",
    };

    /// <summary>需要强制敌视玩家的 bot 角色名（小写比较）。</summary>
    internal static readonly HashSet<string> HostileRoles = new(StringComparer.OrdinalIgnoreCase);

    internal static bool Enabled = true;

    /// <summary>★ v0.1.5 自动诊断：本帧捕获的待 dump 的"活动敌种"（最多 2 只）。</summary>
    private static readonly List<object> _dumpQueue = new();
    private static float _lastAutoDump;
    /// <summary>★ v0.1.5：上次"重申敌对"的时间（每 15 秒清空 _done，重新给所有活动敌种加一次敌）。</summary>
    private static float _lastReassert;

    internal static void Load(string modDir, ManualLogSource log)
    {
        try
        {
            _loaded = true;                 // 标记已载入（幂等，避免重复读文件）
            var f = Path.Combine(modDir, "events.json");
            if (!File.Exists(f)) { log.LogWarning("[EventRotator.Client] 敌对性兜底：找不到 events.json"); return; }

            var txt = File.ReadAllText(f);
            // 轻量解析：找 forceAbpsSpawns / forceHostility / forceBossName 里的角色名
            foreach (var key in new[] { "forceAbpsSpawns", "forceHostility" })
            {
                var i = 0;
                while ((i = txt.IndexOf("\"" + key + "\"", i, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var lb = txt.IndexOf('{', i);
                    if (lb < 0) break;
                    int d = 0, rb = -1;
                    for (int p = lb; p < txt.Length; p++)
                    {
                        if (txt[p] == '{') d++;
                        else if (txt[p] == '}') { d--; if (d == 0) { rb = p; break; } }
                    }
                    if (rb < 0) break;
                    var block = txt.Substring(lb, rb - lb + 1);
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(block, "\"([A-Za-z][A-Za-z0-9]*)\"\\s*:"))
                    {
                        var name = m.Groups[1].Value;
                        if (name.Equals("forceAbpsSpawns", StringComparison.OrdinalIgnoreCase)) continue;
                        HostileRoles.Add(name);
                    }
                    i = rb;
                }
            }

            // ★★★ v0.1.5 修正（客户端日志铁证：`活动敌种=0 已加敌=0` ⇒ 敌对性兜底全程空转）：
            //   旧解析只看 forceAbpsSpawns / forceHostility；而现在活动实际用的是
            //   **bossSpawns（map → [角色…]）**、**bossChance（map → {角色: 概率}）**、**nativeZombies: true**。
            //   这三类都补上（纯手工扫描，不用正则，避免转义踩坑）。
            foreach (var field in new[] { "bossSpawns", "bossChance" })
            {
                int i = 0;
                while ((i = txt.IndexOf("\"" + field + "\"", i, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    int lb = txt.IndexOf('{', i);
                    if (lb < 0) break;
                    int rb = MatchBrace(txt, lb);
                    if (rb < 0) break;
                    var blk = txt.Substring(lb, rb - lb + 1);
                    if (blk.IndexOf('*') >= 0)                       // "*" = 全部首领
                        foreach (var r in AllBossRoles) HostileRoles.Add(r);
                    AddQuotedNames(blk);                             // 块内所有 "xxx" 形式的角色名/键
                    i = rb;
                }
            }
            // nativeZombies: true ⇒ 原生感染者 5 种必须敌视玩家（本轮用户实测"僵尸不打人"的关键一环）
            {
                int i = 0;
                while ((i = txt.IndexOf("\"nativeZombies\"", i, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var seg = txt.Substring(i, Math.Min(40, txt.Length - i));
                    if (seg.IndexOf("true", StringComparison.OrdinalIgnoreCase) >= 0)
                        foreach (var z in new[] { "infectedAssault", "infectedPmc", "infectedCivil", "infectedLaborant", "infectedTagilla" })
                            HostileRoles.Add(z);
                    i += 14;
                }
            }
            // 只保留看起来像 bot 角色名的（排除地图名）
            var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bigmap","factory4_day","factory4_night","interchange","laboratory","lighthouse",
              "rezervbase","sandbox","sandbox_high","shoreline","tarkovstreets","woods","labyrinth","develop","hideout" };
            foreach (var m in maps) HostileRoles.Remove(m);

            // ★★ v0.1.5 安全修正：普通 SCAV（assault / marksman）**不能**被强制敌对。
            //   官方万圣节/感染者活动里，SCAV 与首领对玩家是 **中立** 的（我们 activity 的 desc 也这么写）；
            //   而 `bossSpawns` 的 "*" 展开会把 assault/marksman 一并带进来 ⇒ 这里必须剔除，
            //   否则会与官方行为相反（把中立的 SCAV 变成主动打玩家）。首领/追随者/邪教/劫掠者/感染者保留。
            foreach (var neutral in new[] { "assault", "marksman" })
                if (HostileRoles.Remove(neutral))
                    log.LogInfo($"[EventRotator.Client] 已从强制敌对名单剔除普通 SCAV '{neutral}'（官方活动里它对玩家是中立）");

            // ★ 硬保险：中立/特殊单位永不敌对（BTR、圣诞老人、扎里亚奇等）
            foreach (var n in NeverHostile)
                if (HostileRoles.Remove(n)) log.LogWarning($"[EventRotator.Client] 已从敌对名单剔除中立单位 '{n}'（保护原版中立行为）");

            log.LogInfo($"[EventRotator.Client] 敌对性兜底已启用，强制敌视玩家的角色: {string.Join(", ", HostileRoles)}");
        }
        catch (Exception ex) { log.LogError($"[EventRotator.Client] 敌对性兜底加载失败: {ex.Message}"); }
    }

    /// <summary>从 open 处的 '{' 找到配对的 '}'（找不到返回 -1）。</summary>
    private static int MatchBrace(string s, int open)
    {
        int d = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '{') d++;
            else if (s[i] == '}') { d--; if (d == 0) return i; }
        }
        return -1;
    }

    /// <summary>把字符串里所有 "标识符" 形式的引号内容加进敌对名单（跳过纯数字/地图名由后面的排除表处理）。</summary>
    private static void AddQuotedNames(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '"') continue;
            int j = s.IndexOf('"', i + 1);
            if (j < 0) break;
            var v = s.Substring(i + 1, j - i - 1);
            if (v.Length > 1 && char.IsLetter(v[0])) HostileRoles.Add(v);
            i = j;
        }
    }

    /// <summary>★ v0.1.5 诊断用：取 bot 当前脑层名（`Brain.BaseBrain.ShortName()`；字段/属性都查）。</summary>
    private static string BrainLayer(object botOwner)
    {
        try
        {
            object FP(object o, string n)
            {
                if (o == null) return null;
                var t = o.GetType();
                return t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o)
                    ?? t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(o);
            }
            var brain = FP(botOwner, "Brain");
            var baseBrain = FP(brain, "BaseBrain");
            if (baseBrain == null) return null;
            try { return baseBrain.GetType().GetMethod("ShortName", BindingFlags.Public | BindingFlags.Instance)?.Invoke(baseBrain, null)?.ToString(); }
            catch { return baseBrain.GetType().Name; }
        }
        catch { return null; }
    }

    /// <summary>★ v0.1.5 诊断用：取 bot 当前武器的**类型名**（Pistol / AssaultRifle / MeleeWeapon / null=空手）。</summary>
    private static string WeaponTypeName(object botOwner)
    {
        try
        {
            var wm = botOwner?.GetType().GetProperty("WeaponManager", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botOwner);
            var cw = wm?.GetType().GetProperty("CurrentWeapon", BindingFlags.Public | BindingFlags.Instance)?.GetValue(wm);
            return cw?.GetType().Name;
        }
        catch { return null; }
    }

    /// <summary>这只 bot 的角色是否在强制敌对名单里。</summary>

    // ══════════ ★★★ v1.0.1【性能】热路径成员解析缓存（每帧每 bot 原本 5~6 次反射 ⇒ 0 次）══════════
    //   安全性：BotOwner / BotGame / BotsGroup 的 Type 在进程内恒定 ⇒ 同 Type ⇒ 同 MemberInfo ⇒ 同值；
    //   Type 变了自动重查（同 `_groupType` 判断），换程序集/热重载也安全。
    internal static FieldInfo _settingsField;
    internal static Type _settingsOwnerType;
    internal static PropertyInfo _piProfileId, _piGetPlayer, _piProfile;
    internal static FieldInfo _fiProfileIdBacking;
    internal static Type _idOwnerType;
    internal static PropertyInfo _piProfileProfileId, _piProfileId2;
    internal static Type _profileType;

/// <summary>★ v1.0.1【性能 B】按 Type 刷新 BotOwner 的 id 相关成员缓存（Type 未变则零开销）。</summary>
    private static void RefreshIdMemberCache(Type t)
    {
        if (t == null || t == _idOwnerType) return;
        _piProfileId = t.GetProperty("ProfileId", BindingFlags.Public | BindingFlags.Instance);
        _fiProfileIdBacking = t.GetField("<ProfileId>k__BackingField",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _piGetPlayer = t.GetProperty("GetPlayer", BindingFlags.Public | BindingFlags.Instance);
        _piProfile = t.GetProperty("Profile", BindingFlags.Public | BindingFlags.Instance);
        _idOwnerType = t;
    }
    internal static bool IsHostileRole(object botOwner)
        => HostileRoles.Count > 0 && HostileRoles.Contains(RoleOf(botOwner));

    /// <summary>取 bot 的唯一 id（BotOwner.ProfileId 属性优先，退化 GetPlayer.ProfileId）。</summary>
    // ══════════ ★★★ v1.0.1【性能】BotsGroup.Members 的 PropertyInfo 共享缓存 ══════════
    //   原来全库有 6 处 `x.GetType().GetProperty("Members", ...)` 每次都重做成员查找。
    //   BotsGroup 只有一种类型 ⇒ 一次解析即可（Type 变了自动重查）。
    private static PropertyInfo _membersPropCache;
    private static Type _membersTypeCache;

    /// <summary>★ v1.0.1【性能】取 BotsGroup.Members 的 PropertyInfo（按 Type 缓存）。</summary>
    internal static PropertyInfo MembersProp(Type t)
    {
        if (t == null) return null;
        if (t != _membersTypeCache)
        {
            _membersPropCache = t.GetProperty("Members", BindingFlags.Public | BindingFlags.Instance);
            _membersTypeCache = t;
        }
        return _membersPropCache;
    }

    // ══════════ ★★★ v1.0.1【性能】BotsGroup._initialBot 的 FieldInfo 缓存 ══════════
    private static FieldInfo _initialBotField;

    /// <summary>★ v1.0.1【性能】取 BotsGroup._initialBot 的 FieldInfo（只解析一次）。</summary>
    internal static FieldInfo InitialBotField =>
        _initialBotField ??= AccessTools.Field(typeof(BotsGroup), "_initialBot");

    // ══════════ ★★★ v1.0.1【性能·第十一轮·玩家实测「瞬时掉帧」】bot id 按实例缓存 ══════════
    //   **这是本 mod 最大的 GC 源**：`BotIdOf` 在 `BotOwnerFixedUpdatePatch` 里**每帧每只 bot** 被调一次，
    //   而它最后要 `GetValue(...)?.ToString()` —— **每次都造一个新字符串**：
    //       370 只 bot × 50 Hz = **每秒 18,500 次字符串分配** ⇒ 持续 GC ⇒ 瞬时掉帧。
    //   一只 bot 的 id 在其生命周期内恒定 ⇒ 按实例缓存（`ConditionalWeakTable`：bot 被回收时自动释放，不泄漏）。
    //   ⚠ 只缓存**非空**结果：早期 profile 未就绪时返回 ""，之后能拿到时仍会正常返回（与改前行为一致）。
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, string> _botIdCache = new();

    internal static string BotIdOf(object botOwner)
    {
        if (botOwner == null) return "";
        if (_botIdCache.TryGetValue(botOwner, out var hit)) return hit;
        var v0 = BotIdOfUncached(botOwner);
        if (!string.IsNullOrEmpty(v0)) { try { _botIdCache.Add(botOwner, v0); } catch { } }
        return v0;
    }

    private static string BotIdOfUncached(object botOwner)
    {
        try
        {
            if (botOwner == null) return "";
            var t = botOwner.GetType();
            // ★ v1.0.1【性能 B】把 4 个成员解析缓存起来（原来每次调用做 6 次 GetProperty/GetField）
            RefreshIdMemberCache(t);
            var v = _piProfileId?.GetValue(botOwner)?.ToString()
                 ?? _fiProfileIdBacking?.GetValue(botOwner)?.ToString();
            if (!string.IsNullOrEmpty(v)) return v;
            var p = _piGetPlayer?.GetValue(botOwner) ?? _piProfile?.GetValue(botOwner);
            if (p == null) return "";
            var pt = p.GetType();
            if (pt != _profileType)
            {
                _piProfileProfileId = pt.GetProperty("ProfileId", BindingFlags.Public | BindingFlags.Instance);
                _piProfileId2 = pt.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                _profileType = pt;
            }
            return _piProfileProfileId?.GetValue(p)?.ToString() ?? _piProfileId2?.GetValue(p)?.ToString() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>取 bot 的角色名。</summary>
    internal static string RoleOf(object botOwner)
    {
        try
        {
            // ★ v1.0.1【性能 A】`Settings` 的 FieldInfo 缓存（原来每帧每 bot 都 GetField 反射）
            //   安全性：BotOwner 无子类 ⇒ Type 恒定；Type 变了自动重查（同 Type ⇒ 同 FieldInfo ⇒ 同值）
            var boType = botOwner?.GetType();
            if (boType != null && boType != _settingsOwnerType)
            {
                _settingsField = boType.GetField("Settings", BindingFlags.Public | BindingFlags.Instance);
                _settingsOwnerType = boType;
            }
            var settings = _settingsField?.GetValue(botOwner);
            return RoleOfSettings(settings);
        }
        catch { return ""; }
    }

    // ══════════ ★★★ v0.1.7【Y4】role 反射缓存（≈5000 次反射/秒 → 每 bot 每 5 秒最多 1 次）══════════
    //   为什么可以缓存（两条铁证，已核）：
    //     ① `_role` 全程序**只有一个写入点** = `BotSettings` 的构造函数（Assembly-CSharp:138256-138264，`_role = role;` :138259）
    //        ⇒ 一个 BotSettings 实例的角色**构造后不再改变**；
    //     ② `BotOwner.Settings` 全程序**只有一个赋值点** = bot 生成时（Assembly-CSharp:607185）
    //        ⇒ Settings 实例与 bot 一一对应、同寿命；若哪天换了新实例，**缓存键就变了 ⇒ 自动失效**。
    //   三条护栏（缺一就会改行为）：
    //     ① **只缓存非空结果** —— 否则一次瞬时失败被永久化（原逻辑是"每次都重试"）；
    //     ② **逐局清空**（`ClearRoleCache()` 挂在每局重置点，与 `_done.Clear()` 同处；换局瞬间没有 bot 在跑 ⇒ 不会尖峰）；
    //     ③ **超 512 条自清**（异常局的防御）。
    //   5 秒兜底 ⇒ **逐条、按访问惰性过期**（不是整表清空）⇒ **不会出现"每 5 秒几百条同时失效"的反射尖峰** ✔
    private sealed class RoleCacheEntry { public string Role; public float At; }

    private const float RoleCacheMaxAgeSec = 5f;      // 单条最长信任 5 秒（到期后**该条**在下次访问时才重取）
    private const int RoleCacheMaxEntries = 512;      // 防御上限

    private sealed class RoleRefComparer : IEqualityComparer<object>
    {
        public static readonly RoleRefComparer Instance = new RoleRefComparer();
        public new bool Equals(object a, object b) => ReferenceEquals(a, b);
        public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
    }

    private static readonly Dictionary<object, RoleCacheEntry> _roleCache = new Dictionary<object, RoleCacheEntry>(RoleRefComparer.Instance);

    /// <summary>★ 护栏②：逐局清空（只清缓存，不碰任何判定状态）。</summary>
    internal static void ClearRoleCache()
    {
        try { _roleCache.Clear(); } catch { }
    }

    /// <summary>
    /// ★★★ v0.1.7【Y4】纯缓存逻辑（**时间与反射都由调用方注入 ⇒ 可单测、无 Unity 依赖**）。
    /// 命中且未过期 ⇒ **零反射**；未命中/已过期 ⇒ 走 `reflect`（= 原来的反射逻辑）；失败（空串）**不缓存**。
    /// </summary>
    internal static string RoleCached(object settings, float now, Func<object, string> reflect)
    {
        if (settings == null) return "";
        if (_roleCache.TryGetValue(settings, out var hit) && hit != null && (now - hit.At) < RoleCacheMaxAgeSec)
            return hit.Role;
        var role = reflect == null ? "" : (reflect(settings) ?? "");
        if (!string.IsNullOrEmpty(role))                       // ★ 护栏①：失败不缓存 ⇒ 与原来"每次都重试"一致
        {
            // ★ v0.1.8【复核 R2-B4】护栏③：超上限时**淘汰最旧一条**，不再 `Clear()` 整表。
            //   为什么行为不变：`_role` 不可变（客户端反编译 `Assembly-CSharp.decompiled.cs:138145` 声明 /
            //   `:138259` 唯一写入在构造函数），且 `GetSettings` 返回 `.Copy()`（`:137703`）⇒ 每个 settings 实例
            //   的角色恒定 ⇒ **淘汰掉谁都不影响判定结果**，只是少了"下一次全反射"的脉冲。
            //   `_roleCache` 的上限是 512、实际并发实例远小于它（该分支近乎不可达），故用 O(n) 找最旧足矣。
            if (_roleCache.Count > RoleCacheMaxEntries) EvictOldestRole();
            _roleCache[settings] = new RoleCacheEntry { Role = role, At = now };
        }
        return role;
    }

    /// <summary>★ v0.1.8【复核 R2-B4】淘汰 `At` 最旧的一条缓存（替代 `Clear()` 整表）。找不到就退化为清空。</summary>
    private static void EvictOldestRole()
    {
        try
        {
            object oldestKey = null; float oldestAt = float.MaxValue; bool any = false;
            foreach (var kv in _roleCache)
            {
                if (kv.Value == null) continue;
                if (!any || kv.Value.At < oldestAt) { oldestAt = kv.Value.At; oldestKey = kv.Key; any = true; }
            }
            if (oldestKey != null) _roleCache.Remove(oldestKey);
            else _roleCache.Clear();          // 全为 null 值 ⇒ 只能清空
        }
        catch { try { _roleCache.Clear(); } catch { } }
    }

    /// <summary>从 BotSettings 对象取角色名（_role 是私有字段）—— ★ **原逻辑一字未改**，只是抽成独立方法供缓存调用。</summary>
    private static string ReflectRoleOfSettings(object settings)
    {
        try
        {
            if (settings == null) return "";
            var t = settings.GetType();
            var f = t.GetField("_role", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(settings)?.ToString() ?? "";
            var p = t.GetProperty("Role", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p?.GetValue(settings)?.ToString() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>外部入口（行为与优化前一致：同样返回角色名/空串；只是命中缓存时不再反射）。</summary>
    internal static string RoleOfSettings(object settings)
    {
        try { return RoleCached(settings, UnityEngine.Time.realtimeSinceStartup, ReflectRoleOfSettings); }
        catch { return ReflectRoleOfSettings(settings); }   // 时间 API 不可用（非 Unity 上下文）⇒ 直接反射，行为同原来
    }

    internal static string ProfileIdOf(object botOwner)
    {
        try
        {
            var p = botOwner?.GetType().GetProperty("GetPlayer", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botOwner)
                 ?? botOwner?.GetType().GetProperty("Profile", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botOwner);
            return p?.GetType().GetProperty("ProfileId", BindingFlags.Public | BindingFlags.Instance)?.GetValue(p)?.ToString() ?? "?";
        }
        catch { return "?"; }
    }

    /// <summary>SAIN 是否接管了这只 bot（反射查 SAIN.SAINEnableClass.IsBotExcluded(BotOwner)）。</summary>
    // ══════════ ★★★ v1.0.1【性能·第十一轮·玩家实测】IsSainActive 的类型解析缓存 ══════════
    //   **这是本 mod 单次开销最大的一处**：原来每次调用都
    //       AppDomain.CurrentDomain.GetAssemblies()
    //         .SelectMany(a => a.GetTypes())        ← 扫【全部程序集】的【全部类型】
    //         .FirstOrDefault(x => x.Name == "SAINEnableClass" && x.Namespace == "SAIN")
    //   它挂在 `BotSettingsIsPlayerEnemyPatch`（每次敌我判定）的诊断串里。
    //   第四轮审计实测这一条"单次几百微秒级"，玩家把「诊断日志」打开后 ⇒ 每局几十万次全程序集扫描。
    //   ⇒ 类型解析与 `IsBotExcluded` 的 MethodInfo 都按 Type 缓存（SAIN 装没装、装在哪，进程内恒定）。
    private static Type _sainEnableType;
    private static bool _sainTypeSearched;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, MethodInfo> _sainExclMethod = new();
    private static Type _gwTypeCache;
    private static bool _gwTypeCacheSearched;

    internal static string IsSainActive(object botOwner)
    {
        try
        {
            if (!_sainTypeSearched)
            {
                _sainTypeSearched = true;
                try
                {
                    _sainEnableType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                        .FirstOrDefault(x => x != null && x.Name == "SAINEnableClass" && x.Namespace == "SAIN");
                }
                catch { }
            }
            var t = _sainEnableType;
            if (t == null) return "SAIN未装";
            if (botOwner == null) return "取不到接口";
            var ot = botOwner.GetType();
            var m = _sainExclMethod.GetOrAdd(ot, static oty =>
            {
                try
                {
                    return t_static.GetMethod("IsBotExcluded", BindingFlags.Public | BindingFlags.Static, null, new[] { oty }, null);
                }
                catch { return null; }
            });
            if (m == null) return "取不到接口";
            var excluded = (bool)m.Invoke(null, new[] { botOwner });
            return excluded ? "否(SAIN排除)" : "是(SAIN接管)";
        }
        catch (Exception ex) { return "查询失败:" + ex.GetType().Name; }
    }

    private static Type t_static => _sainEnableType;

    /// <summary>
    /// 诊断：客户端实际读到的 BotLocationModifier.AdditionalHostilitySettings。
    /// 这是回答"我的服务端敌对数到底有没有到客户端"的唯一硬证据。
    /// </summary>
    internal static string DumpHostility(object botOwner)
    {
        try
        {
            var blm = CurrentBotLocationModifier();
            if (blm == null) return "(拿不到 BotLocationModifier)";
            var arr = blm.AdditionalHostilitySettings;
            if (arr == null) return "(敌对数数组=null)";

            var role = RoleOf(botOwner);
            foreach (var e in arr)
            {
                if (e == null) continue;
                if (!string.Equals(e.BotRole.ToString(), role, StringComparison.OrdinalIgnoreCase)) continue;
                return $"[{e.BotRole} S={e.SavagePlayerBehaviour} B={e.BearPlayerBehaviour} U={e.UsecPlayerBehaviour} 敌={e.AlwaysEnemies?.Length ?? 0} 友={e.AlwaysFriends?.Length ?? 0}]";
            }
            return $"(共{arr.Length}条,但没有 '{role}' 的条目)";
        }
        catch (Exception ex) { return "(诊断异常:" + ex.GetType().Name + ")"; }
    }

    /// <summary>取当前战局客户端实际使用的 BotLocationModifier（GameWorld.BotsController.BotLocationModifier）。</summary>
    internal static BotLocationModifier CurrentBotLocationModifier()
    {
        try
        {
            // ★★★ v1.0.1【性能·第十一轮】原来每次调用都扫【全部程序集 + 全部类型】找 GameWorld。
            //   GameWorld 是游戏的核心类型，进程内恒定 ⇒ 只需找一次。
            if (_gwTypeCacheSearched == false)
            {
                _gwTypeCacheSearched = true;
                try
                {
                    _gwTypeCache = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                        .FirstOrDefault(x => x?.Name == "GameWorld");
                }
                catch { }
            }
            var gwType = _gwTypeCache;
            var gw = gwType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var bots = gw?.GetType().GetProperty("BotsController", BindingFlags.Public | BindingFlags.Instance)?.GetValue(gw);
            return bots?.GetType().GetField("BotLocationModifier", BindingFlags.Public | BindingFlags.Instance)?.GetValue(bots) as BotLocationModifier;
        }
        catch { return null; }
    }

    /// <summary>
    /// 【核心修复】在 BotsController.Init 之后，直接检查并把敌对性写进客户端**实际使用**的
    /// BotsController.BotLocationModifier.AdditionalHostilitySettings。
    /// 这是 IsPlayerEnemy 唯一读取的那份数据 —— 服务端改的那份未必是它。
    ///
    /// 客户端模型（全部是 public 字段）：
    ///   BotRole(枚举) / AlwaysEnemies(WildSpawnType[]) / AlwaysFriends / Neutral / Warn /
    ///   SavagePlayerBehaviour(Bear/Usec 同)(EWarnBehaviour) / *EnemyChance(int)
    /// </summary>
    internal static void EnsureHostility(BotLocationModifier blm, string logTag)
    {
        if (blm == null || HostileRoles.Count == 0) return;
        try
        {
            var arr = blm.AdditionalHostilitySettings;
            int before = arr?.Length ?? -1;
            var list = arr == null ? new List<AdditionalHostilitySettings>() : new List<AdditionalHostilitySettings>(arr);

            // 合法的敌种枚举全集（用于填 AlwaysEnemies，只认自己人）
            var allTypes = (WildSpawnType[])Enum.GetValues(typeof(WildSpawnType));

            foreach (var role in HostileRoles)
            {
                WildSpawnType wt;
                if (!Enum.TryParse(role, true, out wt)) { if (DiagOn) Diag($"[HOST] {logTag} '{role}' 不是合法 WildSpawnType，跳过"); continue; }

                AdditionalHostilitySettings entry = null;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && list[i].BotRole == wt) { entry = list[i]; break; }

                bool added = false;
                if (entry == null) { entry = new AdditionalHostilitySettings(); list.Add(entry); added = true; }

                var prev = $"{entry.SavagePlayerBehaviour}/{entry.BearPlayerBehaviour}/{entry.UsecPlayerBehaviour}";

                entry.BotRole = wt;
                entry.SavagePlayerBehaviour = EWarnBehaviour.AlwaysEnemies;
                entry.BearPlayerBehaviour = EWarnBehaviour.AlwaysEnemies;
                entry.UsecPlayerBehaviour = EWarnBehaviour.AlwaysEnemies;
                entry.SavageEnemyChance = 100;
                entry.BearEnemyChance = 100;
                entry.UsecEnemyChance = 100;
                // 只认自己人：敌列表=除自己外全部；友列表=只有自己
                entry.AlwaysEnemies = allTypes.Where(t => t != wt).ToArray();
                entry.AlwaysFriends = new[] { wt };
                entry.Neutral = Array.Empty<WildSpawnType>();
                entry.Warn = Array.Empty<WildSpawnType>();
                // ChancedEnemies：类型名不确定，用反射按字段真实类型清空
                try
                {
                    var f = typeof(AdditionalHostilitySettings).GetField("ChancedEnemies");
                    if (f != null && f.GetValue(entry) != null)
                    {
                        var elem = f.FieldType.GetElementType();
                        if (elem != null) f.SetValue(entry, Array.CreateInstance(elem, 0));
                    }
                }
                catch { }

                if (DiagOn) Diag($"[HOST] {logTag} 注入敌对性 role={role} {(added ? "新增条目" : "改已有条目")} 行为 {prev}→AlwaysEnemies (改前条目数={before} 现在={list.Count})");
            }

            // ★★★ bossScourge（2026-10-23）：冠军 + arenaFighterEvent 小弟是**同一小队**。
            //   上面的"统一规则"是「除自己外全部敌对」⇒ bossScourge 与 arenaFighterEvent 会被互相列进
            //   AlwaysEnemies（两个不同枚举值）⇒ 冠军会打自己的小弟、小弟也会打冠军 ✗。
            //   这里事后把两者改成互相友军（它们各自条目里只移除对方、追加对方到友军，其它角色不动）。
            BossScourgeClient.EnsureSquadFriendly(list);
            blm.AdditionalHostilitySettings = list.ToArray();
        }
        catch (Exception ex) { if (DiagOn) Diag($"[HOST] {logTag} EnsureHostility 异常: {ex.GetType().Name}: {ex.Message}"); }
    }
    /// <summary>只报"关键结果"的直写日志（限频，避免刷屏）。</summary>
    private static float _lastKeyLog;
    internal static void DiagKey(string msg)
    {
        if (DiagOn) Diag(msg);
        try
        {
            if (UnityEngine.Time.time - _lastKeyLog < 5f) return;   // 至少间隔 5 秒
            _lastKeyLog = UnityEngine.Time.time;
            _log?.LogInfo("[EventRotator] " + msg);
        }
        catch { }
    }

    /// <summary>打印扫描汇总（限频 10 秒，避免刷屏）。</summary>
    private static float _lastSumLog;
    internal static void DiagSum(string msg)
    {
        if (DiagOn) Diag(msg);
        try
        {
            if (UnityEngine.Time.time - _lastSumLog < 10f) return;
            _lastSumLog = UnityEngine.Time.time;
            _log?.LogInfo("[EventRotator] " + msg);
        }
        catch { }
    }

    /// <summary>★ v1.0.1【性能】诊断日志总开关（默认 false）。热路径必须写 `if (DiagOn) Diag($"…")`，否则 `$"…"` 的求值先于 Diag（:1767 在 FixedUpdate ⇒ 每帧×每 bot）。</summary>
    internal static bool DiagOn = false;

    internal static void Diag(string msg)
    {
        try
        {
            // ★ 只写文件，**不刷 BepInEx 日志**（否则会疯狂输出、干扰其他 mod 的日志与界面观感）
            lock (_diagBuf)
            {
                if (_diagBuf.Count > 100) _diagBuf.RemoveRange(0, 50);   // ★ v1.0.1：400/200 → 100/50
                _diagBuf.Add(DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg);
            }
        }
        catch { }
    }

    /// <summary>
    /// 诊断：这只 bot 的敌人状态 —— ① BotSettings 里的敌/友名单 ② 玩家是否已在该 BotsGroup 的敌人里
    /// ③ BotsGroup 里已有几个敌人 ④ 玩家的 Side。用来回答"到底卡在哪一步"。
    /// </summary>
    internal static string DumpEnemyState(object botOwner, IPlayer player)
    {
        try
        {
            var settings = botOwner?.GetType().GetField("Settings", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botOwner);
            string enemyTypes = "?", friendTypes = "?", warnTypes = "?";
            if (settings != null)
            {
                foreach (var mn in new[] { "GetEnemyBotTypes", "GetFriendlyBotTypes", "GetWarnBotTypes" })
                {
                    try
                    {
                        var m = settings.GetType().GetMethod(mn, BindingFlags.Public | BindingFlags.Instance);
                        var v = m?.Invoke(settings, null) as System.Collections.IEnumerable;
                        var s = v == null ? "null" : string.Join(",", v.Cast<object>().Select(x => x?.ToString()));
                        if (mn == "GetEnemyBotTypes") enemyTypes = s;
                        else if (mn == "GetFriendlyBotTypes") friendTypes = s;
                        else warnTypes = s;
                    }
                    catch { }
                }
            }

            // BotsGroup 的敌人名单
            var group = botOwner?.GetType().GetField("BotsGroup", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(botOwner);
            string groupInfo = "无组";
            if (group != null)
            {
                var enemiesProp = group.GetType().GetProperty("Enemies", BindingFlags.Public | BindingFlags.Instance);
                var enemies = enemiesProp?.GetValue(group) as System.Collections.IDictionary;
                groupInfo = enemies == null ? "Enemies=null" : $"敌人数={enemies.Count}";
            }

            return $"Side={player?.Side} 敌名单=[{enemyTypes}] 友名单=[{friendTypes}] 警告名单=[{warnTypes}] {groupInfo}";
        }
        catch (Exception ex) { return "(状态异常:" + ex.GetType().Name + ")"; }
    }

    /// <summary>取 IPlayer 的角色名（AI 才有意义）。</summary>
    internal static string RoleOfPlayer(IPlayer p)
    {
        try
        {
            return p?.Profile?.Info?.Settings?.Role.ToString() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 【重试入口】每帧由 Plugin 调用：进图后反复尝试"把玩家写进敌对组"，直到成功或超时。
    /// 原因：AddActivePLayer 触发时 bot 组还没创建（实测"扫描 0 个组"），
    /// 走私者要几十秒后才刷出来，所以必须持续尝试。
    /// </summary>
    private static float _nextTry;
    private static int _tryCount;

    internal static void Tick(BotsController bc, IPlayer player)
    {
        try
        {
            if (!Enabled) return;
            if (player == null || bc == null) { ResetTry(); return; }

            var t = UnityEngine.Time.time;
            if (t < _nextTry) return;
            _nextTry = t + 2.0f;                 // 每 2 秒试一次

            if (_tryCount == 0) if (DiagOn) Diag($"[HOST] 开始重试加敌（玩家 {player.ProfileId} Side={player.Side}）");
            _tryCount++;
            if (_tryCount > 60) { if (_tryCount == 61) if (DiagOn) Diag("[HOST] 重试 60 次后停止"); return; }

            ForcePlayerIntoEnemyGroups(bc, player);
        }
        catch { }
    }

    internal static void ResetTry() { _tryCount = 0; _nextTry = 0f; }

    /// <summary>
    /// 【★★★★★ 兜底：在自己插件的 Update 里每帧遍历 BotsController.Bots ★★★★★】
    ///
    /// 前面所有 Harmony 事件钩子要么挂载失败、要么不被调用（BotOwner.FixedUpdate 挂载成功但回调不触发）。
    /// 而插件自身的 Update 是**确认在运行**的（F10 热键就靠它）。
    ///
    /// 所以：每帧从 BotsController.Bots(BotsList) 里找出活动敌种，直接操作它的组。
    /// 逐级降级并打印各级计数，这样即使失败也能看到卡在哪一级。
    /// </summary>
    /// <summary>
    /// 是否真正进入了 raid（部署界面/加载界面必须返回 false）。
    /// 判据：GameWorld 存在 + 玩家已在世界里 + BotsController 已就绪 + 有位置信息。
    /// </summary>
    /// <summary>游戏世界是否存在（每 1 秒才真正探测一次；主界面时开销≈0）。</summary>
    private static float _nextGwProbe;
    private static bool _gwPresent;
    // ★★★ v1.0.1【BugFix·全量审计#1】`_gwPresent` 原来是**粘性**的（一旦 true 永不再探测），
    //   但换局时 GameWorld 确实会消失 ⇒ 从第 2 局起它永远为 true ⇒ 下面这些全部失效：
    //     · `if (!IsGameWorldPresentCached()) { _raidSeenAt = -1f; _opFailures = 0; }` **变成不可达**
    //       ⇒ `_raidSeenAt` 永不复位 ⇒ 「进图后 15 秒预热期」只在第 1 局生效
    //       ⇒ 第 2 局起在**部署界面**就对半初始化的 bot 组做 AddEnemy（注释自己说这会 NRE + 卡部署倒计时）
    //     · `_opFailures` 不复位 ⇒ 累计到 5 就**整个会话永久停手**（熔断本意是按局）
    //     · `_injectQueue/_injectSeen` 永不清 ⇒ 3~17 局后塞满 512 ⇒ 新局 bot 进不了队列
    //       ⇒ `InjectEnemyKnown` / `SainLayerBinder.Ensure` 对新局 bot **静默失效**（"bot 又不打人"复发）
    //   修法：用**世界引用比对**代替布尔粘性，并在同处做每局复位。
    private static object _raidGwOwner;          // 本局归属的 GameWorld 引用

    /// <summary>★ 每局一次性复位（换局时调用）。**只清"本局状态"，不动任何跨局应保留的缓存。**</summary>
    private static void OnNewRaid(object gw)
    {
        try
        {
            _raidGwOwner = gw;
            _gwPresent = gw != null;
            _nextGwProbe = 0f;
            _raidSeenAt = -1f;            // 重新进入 15 秒预热
            _opFailures = 0;              // 熔断按局归零
            _injectQueue.Clear();
            _injectSeen.Clear();
            _injectCursor = 0;
            _dumpQueue.Clear();
            _zoneKeys = null;
            _zoneKeysCount = -1;
            _zoneCursor = 0;
            _done.Clear();                // "已加敌"标记按局清（键是 profileId，跨局本就不会重复，清掉更省内存）
            ClearRoleCache();
        }
        catch { }
    }
    private static bool IsGameWorldPresentCached()
    {
        try
        {
            // ★★★ v1.0.1【性能·第十七轮·PerfOSD 实测定位】
            //   实测：`ScanStage stage=6 world=47.9ms`（18/20 次）—— 98% 的耗时都在这个函数里。
            //   原因：`GameWorldInstance()` 返回 null ⇒ `_gwPresent` 一直 false ⇒ `_raidSeenAt` 一直 -1
            //   ⇒ 每次调用都重新探测，每 3 秒一次**全场景 FindObjectOfType**（几十毫秒）且仍然拿不到。
            //   **修法一：粘性。** 一旦确认为 true，本次对局内**永不再探测**（世界不会"消失"）。
            // ★ v1.0.1【BugFix】不再粘性：改用"世界引用是否变化"判定换局。
            //   缓存期内（0.5 秒）直接返回上次结果，避免每帧探测；但**世界一旦换掉，立刻复位本局状态**。
            object gwNow = GameWorldInstance();
            if (!ReferenceEquals(gwNow, _raidGwOwner))
            {
                OnNewRaid(gwNow);                 // ★ 换局 ⇒ 每局复位
                return _gwPresent;
            }
            if (UnityEngine.Time.unscaledTime < _nextGwProbe) return _gwPresent;
            _nextGwProbe = UnityEngine.Time.unscaledTime + 3.0f;
            _gwPresent = gwNow != null;
            return _gwPresent;
        }
        catch { return false; }
    }

    /// <summary>
    /// 是否真正进入了 raid（部署界面/加载界面必须返回 false）。
    /// ★ 带缓存：FindObjectOfType 是重操作，不能每帧调用。每 1 秒才刷新一次。
    /// </summary>
    private static float _nextRaidProbe;
    private static bool _cachedInRaid;
    private static bool IsInRaid()
    {
        try
        {
            if (UnityEngine.Time.unscaledTime < _nextRaidProbe) return _cachedInRaid;
            _nextRaidProbe = UnityEngine.Time.unscaledTime + 1.0f;

            _cachedInRaid = false;
            if (GameWorldInstance() == null) { NoteBlocked("GameWorld=null"); return false; }
            if (GetMainPlayer() == null) { NoteBlocked("玩家=null"); return false; }
            if (GetBotsController() == null) { NoteBlocked("BotsController=null"); return false; }

            _cachedInRaid = true;
            return true;
        }
        catch { return false; }
    }

    /// <summary>记录"被闸门挡住"的原因。★ 用帧计数限频（原来用 Time.time 判断是错的，导致每帧刷一条）。</summary>
    private static int _blockFrame;
    private static string _lastBlockReason;
    private static int _blockCount;
    private static void NoteBlocked(string why)
    {
        try
        {
            _blockCount++;
            if (_blockCount > 5) return;                       // 最多报 5 次，之后彻底安静
            if (_lastBlockReason == why && _blockCount > 1) return;
            _lastBlockReason = why;
            DiagKey("闸门阻挡: " + why);
        }
        catch { }
    }

    /// <summary>给补丁用的公开闸门（IsInRaid 的内部封装）。</summary>
    internal static bool IsInRaidSafe()
    {
        try { return IsInRaid(); } catch { return false; }
    }

    private static float _nextScan;

    // ★★★ v1.0.1【性能·第十三轮】ReapplyZoneAddPlayer 的反射缓存
    private static PropertyInfo _bcGroupsPi;
    private static PropertyInfo _spawnerGroupsPi;
    private static Type _spawnerGroupsType;
    private static readonly object[] _addPlayerArg = new object[1];
    private static int _zoneCursor;   // ★ 分批轮转游标
    private static List<object> _zoneKeys;
    private static int _zoneKeysCount = -1;
    private static readonly Dictionary<Type, MethodInfo> _addPlayerMi = new Dictionary<Type, MethodInfo>();

    /// <summary>按 Type 缓存 BotZoneGroup.AddPlayer(IPlayer)。首次仍按原逻辑解析（保证语义不变）。</summary>
    private static MethodInfo AddPlayerMiOf(Type t)
    {
        try
        {
            if (_addPlayerMi.TryGetValue(t, out var hit)) return hit;
            MethodInfo m = null;
            try
            {
                var ip = typeof(IPlayer);
                m = t.GetMethod("AddPlayer", BindingFlags.Public | BindingFlags.Instance, null, new[] { ip }, null);
            }
            catch { }
            if (m == null)
            {
                try
                {
                    m = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .FirstOrDefault(x => x.Name == "AddPlayer" && x.GetParameters().Length == 1);
                }
                catch { }
            }
            _addPlayerMi[t] = m;    // null 也缓存：同一 Type 反复解析无意义
            return m;
        }
        catch { return null; }
    }

    // ══════════ ★★★ v1.0.1【性能·第十八轮】扫描循环的反射缓存 ══════════
    private static readonly string[] _enemyCauseNames = { "initial", "addPlayer", "addBotNoGroup", "AddEnemyToAllGroups", "initCauseEnemy", "AddNewMember" };
    private static readonly Dictionary<Type, MethodInfo> _addEnemyMi = new Dictionary<Type, MethodInfo>();
    private static readonly Dictionary<Type, Type> _addEnemyCauseTy = new Dictionary<Type, Type>();
    private static readonly Dictionary<Type, (FieldInfo first, FieldInfo members)> _groupFields = new Dictionary<Type, (FieldInfo, FieldInfo)>();
    private static readonly object[] _addEnemyArgs = new object[2];   // ★ 复用参数数组

    /// <summary>按 Type 缓存 BotsGroup 的 `_isFirstMemberAdded` / `_members` 字段（原来每只 bot 5 次裸反射）。</summary>
    private static (FieldInfo first, FieldInfo members) GroupFieldsOf(Type gt)
    {
        try
        {
            if (_groupFields.TryGetValue(gt, out var hit)) return hit;
            FieldInfo f1 = null, f2 = null;
            try { f1 = gt.GetField("_isFirstMemberAdded", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); } catch { }
            try { f2 = gt.GetField("_members", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); } catch { }
            var v = (f1, f2);
            _groupFields[gt] = v;
            return v;
        }
        catch { return (null, null); }
    }

    /// <summary>按 Type 缓存 AddEnemy 第二个参数（EBotEnemyCause）的类型（原来每只 bot 调 GetParameters()）。</summary>
    private static Type AddEnemyCauseTypeOf(Type gt, MethodInfo mi)
    {
        try
        {
            if (_addEnemyCauseTy.TryGetValue(gt, out var hit)) return hit;
            Type ct = null;
            try { ct = mi?.GetParameters()[1].ParameterType; } catch { }
            _addEnemyCauseTy[gt] = ct;
            return ct;
        }
        catch { return null; }
    }
    private static readonly Dictionary<Type, Dictionary<string, object>> _causeCache = new Dictionary<Type, Dictionary<string, object>>();

    /// <summary>按 Type 缓存 BotsGroup.AddEnemy(IPlayer, EBotEnemyCause)，语义与原 `.FirstOrDefault` 完全一致。</summary>
    private static MethodInfo AddEnemyMiOf(Type gt)
    {
        try
        {
            if (_addEnemyMi.TryGetValue(gt, out var hit)) return hit;
            MethodInfo mi = null;
            try
            {
                mi = gt.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                       .FirstOrDefault(x => x.Name == "AddEnemy" && x.GetParameters().Length == 2);
            }
            catch { }
            _addEnemyMi[gt] = mi;
            return mi;
        }
        catch { return null; }
    }

    /// <summary>按「枚举类型 + 名字」缓存 Enum.Parse 结果（原来每只 bot 每个候选都解析一次）。</summary>
    private static object CauseValueOf(Type causeType, string cname)
    {
        try
        {
            if (!_causeCache.TryGetValue(causeType, out var map))
            {
                map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                _causeCache[causeType] = map;
            }
            if (map.TryGetValue(cname, out var v)) return v;
            object parsed = null;
            try { parsed = Enum.Parse(causeType, cname, true); } catch { }
            map[cname] = parsed;   // null 也缓存：同名同类型反复失败无意义
            return parsed;
        }
        catch { return null; }
    }

    // ══════════ ★★★ v1.0.1【性能·第十三轮·PerfOSD 实测数据驱动】每帧轮转注入 ══════════
    //   **实测证据**（PerfOSD）：
    //     战局内 frame 稳定在 134~150ms、间隔约 1 秒、`gc0=+0`（全程不 GC）
    //     ⇒ 这是**固定工作量的同步阻塞**，不是 GC、不是随机抖动。
    //   根因：`InjectEnemyKnown`（每只 3 次 MethodInfo.Invoke ≈ 1.5ms）+ `SainLayerBinder.Ensure`
    //         在**扫描循环里对全部 bot 一次性做完** ⇒ 一帧内 ~370×1.5ms ≈ 140ms。
    //   第十二轮我加的"错峰"只有 4 档（`Count & 3`）⇒ 每组仍有 ~92 只同时到期 ⇒ 132ms，**没修好**。
    //   现在改成**结构性的做法**：扫描只把待处理的 bot 放进队列，
    //         真正的重活由 `TickInjections()` **每帧只处理 2 只** ⇒ 单帧增量 ≈ 3ms。
    private static readonly List<object> _injectQueue = new(512);
    private static readonly HashSet<object> _injectSeen = new(512);
    private static int _injectCursor;
    private const int InjectPerFrame = 2;

    /// <summary>★ 每帧调用（从 Plugin.Update）：把 SAIN 注入/脑层补齐的活按帧摊开，削掉周期性尖峰。</summary>
    internal static void TickInjections()
    {
        try
        {
            int cnt = _injectQueue.Count;
            if (cnt == 0) return;
            int budget = InjectPerFrame;
            while (budget-- > 0 && cnt > 0)
            {
                if (_injectCursor >= cnt) _injectCursor = 0;
                var bot = _injectQueue[_injectCursor];
                _injectCursor++;
                if (bot == null) continue;
                try { SainProbe.InjectEnemyKnown(bot); } catch { }
                try { SainLayerBinder.Ensure(bot); } catch { }
            }
        }
        catch { }
    }

    // ★ v1.0.1【性能·第十二轮】`BotOwner.BotsGroup` 的成员缓存（原来每只 bot 每轮 2 次裸反射）
    private static PropertyInfo _botGroupPi;
    private static FieldInfo _botGroupFi;
    private static Type _botGroupType;
    private static float _raidSeenAt = -1f;      // 检测到游戏世界的时刻（-1 = 还没）
    private static int _opFailures;              // 组操作异常计数（熔断用）
    private static readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>懒加载保险：任何入口需要名单时，若还没载入就立刻载入（幂等）。</summary>
    private static bool _loaded;
    private static string _loadDir;
    internal static void SetDir(string dir) => _loadDir = dir;
    internal static void EnsureLoaded()
    {
        try
        {
            if (_loaded) return;
            if (string.IsNullOrEmpty(_loadDir)) return;
            _loaded = true;
            Load(_loadDir, _log);
        }
        catch { }
    }

    /// <summary>
    /// ★★★ 正解：调用**游戏自己的** BotZoneGroups.AddPlayer(IPlayer)。
    ///
    /// 反编译确认这就是"把玩家写进各组敌人表"的权威入口：
    ///   foreach (g in _bossGroup)
    ///       if (g.IsPlayerEnemy(player) || g.ForcedAggressiveForNewPlayers)
    ///           g.AddEnemy(player, EBotEnemyCause.addPlayerToBoss /*=3*/);
    ///           g.AddEnemyGroupIfAllowed(player.GroupId, player.Side);
    ///       else g.AddNeutral(player);
    ///   （data 字典同样处理一遍）
    ///
    /// 它只在"玩家进入该 zone"时被游戏调用一次；而活动刷出的组是几十秒后才创建的，
    /// 所以那些组从来没被处理过。这里周期性地补调，走的是完全正规的路径 ——
    /// 所有校验（IsPlayerEnemy / MarkOfUnknown / 阵营规则）都由游戏自己判定，不需要我反射硬塞。
    /// </summary>
    internal static void ReapplyZoneAddPlayer()
    {
        try
        {
            // ★★★ 先判节流再取玩家！
            //   GetMainPlayer() 内部有 FindObjectOfType（全场景扫描），极其昂贵。
            //   之前它在 Update 里每帧被调用 → 主线程被拖垮 → 卡加载界面。
            var t = UnityEngine.Time.unscaledTime;
            if (t < _nextZoneReapply) return;
            _nextZoneReapply = t + 5f;                 // 每 5 秒补调一次

            // ★★★ v1.0.1【性能·第十三轮·PerfOSD 数据驱动】本方法的反射全部缓存 ══════════════
            //   **原实现**：循环里对**每个 BotZoneGroup** 都做
            //       ① GetType() ② GetMethod("AddPlayer", flags, binder, Type[])  ← 最慢重载
            //       ③ 参数里再 GetInterfaces().FirstOrDefault(...)             ← 又多一次分配
            //       ④ 兜底 GetMethods().FirstOrDefault(...)                    ← 全方法表 + LINQ
            //       ⑤ new object[] { player }                                  ← 每次分配
            //   Tarkov 一张图**几十个刷怪区** ⇒ 每 5 秒一次「几十 × 方法表扫描」≈ 100~150ms 尖峰。
            //   **现在**：按 Type 缓存 MethodInfo、缓存两级 Groups 属性、复用同一个参数数组 ⇒ 循环里只剩 Invoke。
            if (_bcGroupsPi == null)
            {
                var bc0 = GetBotsController();
                if (bc0 == null) return;
                _bcGroupsPi = bc0.GetType().GetProperty("Groups", BindingFlags.Public | BindingFlags.Instance);
            }
            var bc = GetBotsController();
            var spawner = _bcGroupsPi?.GetValue(bc);
            if (spawner == null) return;
            if (_spawnerGroupsPi == null || _spawnerGroupsType != spawner.GetType())
            {
                _spawnerGroupsPi = spawner.GetType().GetProperty("Groups", BindingFlags.Public | BindingFlags.Instance);
                _spawnerGroupsType = spawner.GetType();
            }
            var dict = _spawnerGroupsPi?.GetValue(spawner);
            if (!(dict is System.Collections.IDictionary dd)) return;

            var player = GetMainPlayer();
            if (player == null) return;
            _addPlayerArg[0] = player;

            int called = 0;   // ★ v0.1.7【清零 CS0219】原为 `int called = 0, enemies = 0;`：`enemies` 全方法**从未被赋值也从未被读取**（无任何累加行）⇒ 无可恢复的统计意图，按批准删除
            // ★★★ v1.0.1【性能·第十九轮·实测 ReapplyZoneAddPlayer = 51~64ms】
            //   原来每 5 秒把**全部刷怪区**一次性 AddPlayer 一遍 ⇒ 单次 50ms+ 尖峰。
            //   现在**分批轮转**：每轮最多 BatchPerCall 个区，游标跨轮推进 ⇒
            //   **覆盖范围完全不变**（几轮内所有区都会被补一遍），只是把尖峰摊平。
            //   AddPlayer 本身幂等 ⇒ 分批不改变最终效果。
            const int BatchPerCall = 1;   // 实测单个区 AddPlayer ≈30ms ⇒ 每次只做 1 个
            // ★★★ v1.0.1【性能·第二十一轮·实测 ReapplyZoneAddPlayer = 54ms 且不在循环里】
            //   上一版为了"只处理 2 个"，**每次都把整个字典遍历一遍 + new 一个 List**
            //   ⇒ 字典大的时候（几十上百个组容器），这个开销比处理 2 个还大。
            //   现在：**key 列表缓存**，只在字典条目数变化时重建。
            if (dd.Count > 0)
            {
                if (_zoneKeys == null || _zoneKeysCount != dd.Count)
                {
                    _zoneKeys = new List<object>(dd.Count);
                    foreach (System.Collections.DictionaryEntry kv0 in dd) if (kv0.Value != null) _zoneKeys.Add(kv0.Value);
                    _zoneKeysCount = dd.Count;
                    _zoneCursor = 0;
                }
                int n2 = _zoneKeys.Count;
                int take = Math.Min(BatchPerCall, n2);
                for (int k2 = 0; k2 < take; k2++)
                {
                    if (_zoneCursor >= n2) _zoneCursor = 0;
                    var bzg = _zoneKeys[_zoneCursor];
                    _zoneCursor++;
                    if (bzg == null) continue;
                    var m = AddPlayerMiOf(bzg.GetType());
                    if (m == null) continue;
                    try { m.Invoke(bzg, _addPlayerArg); } catch { continue; }
                    called++;
                }
            }
            if (called > 0) DiagSum($"★补调 BotZoneGroups.AddPlayer: 处理 {called} 个 zone 组容器（玩家 {player.ProfileId}）");
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            DiagKey("补调 AddPlayer 异常: " + inner.GetType().Name + ": " + inner.Message);
        }
    }
    private static float _nextZoneReapply;

    // ═══ 插桩：每一步只报一次，卡住时能看到"最后到达哪一步" ═══

    /// <summary>
    /// ★ 2026-10-31【仪式开始后仍然不打人】用户实测：仪式完成后 exUsec/祭司一组仍不敌对。
    /// 官方 hostilitySettingsForEvent.summon 把这批角色设成 AlwaysFriends，仪式结束后不会自动翻转；
    /// 这里在仪式开始时把它们显式加进"强制敌对"集合，由既有扫描器（每 1.5s）逐只 AddEnemy。
    /// </summary>
    internal static void ForceRitualHostility()
    {
        try
        {
            string[] roles = { "ravangeZryachiyEvent", "sectantPriest", "sectantWarrior", "sectantOni",
                               "sectantPredvestnik", "sectantPrizrak", "exUsec" };
            int added = 0;
            foreach (var r in roles) if (HostileRoles.Add(r)) added++;
            if (added > 0) DiagAlways($"[仪式诊断] 已把 {added} 个仪式相关角色加入强制敌对（{string.Join(",", roles)}）");
            else DiagAlways("[仪式诊断] 仪式相关角色已在强制敌对集合里（无需新增）");
        }
        catch { }
    }

    internal static void ScanAndForceEnemy()
    {
        try
        {
            if (!Enabled) return;
            if (!EventRotator.Client.Plugin.ForceHostileOn()) return;   // ★ 开关关闭时完全不扫描（零风险）
            EnsureLoaded();                      // ★ 保险：确保名单已载入
            if (_opFailures >= 5) return;        // ★ 异常熔断：连续失败太多就彻底停手，避免破坏对局

            // ★★ 预热期：部署界面时 GameWorld 可能已存在、但 bot 组处于"半初始化"状态，
            //    此时 AddMember/AddEnemy 会抛 NullReferenceException 并卡住部署倒计时。
            //    所以检测到游戏世界后，先等 15 秒再开始动手。
            if (_raidSeenAt < 0f)
            {
                if (!IsGameWorldPresentCached()) return;
                _raidSeenAt = UnityEngine.Time.unscaledTime;
                if (DiagOn) Diag("检测到游戏世界，进入 15 秒预热期（避开部署界面）");
                return;
            }
            if (UnityEngine.Time.unscaledTime - _raidSeenAt < 15f) return;
            if (!IsGameWorldPresentCached()) { _raidSeenAt = -1f; _opFailures = 0; return; }

            var t = UnityEngine.Time.time;
            if (t < _nextScan) return;
            // ★★★ v1.0.1【性能·第十二轮·玩家实测「走一两秒卡一下」】
            //   原来「每 2 秒扫一次、一次扫完 370 只 bot」⇒ 一帧内有约 5500 次反射调用（≈3~4ms）⇒ 每 2 秒卡一下。
            //   **拉长间隔不能削峰**（只是让尖峰变稀），必须**提高频率 + 让每只 bot 的工作错峰**：
            //     · 扫描 2s → 0.5s（4 倍频率，但每次只处理"令牌到期"的那部分 bot）
            //     · `InjectEnemyKnown` 的 2 秒令牌**按插入顺序错峰 0~2 秒** ⇒ 每轮只有约 1/4 的 bot 到期
            //   ⇒ 峰值从 ~3~4ms 降到 ~0.5ms，而**每只 bot 的有效刷新频率仍然是 2 秒**（行为不变）。
            // ★★★ v1.0.1【性能·第十五轮】0.5s → 1.5s：
            //   第十二轮把扫描加密到 0.5s 是为了"让每只 bot 的工作错峰"，但那招没成功（只分了 4 档）。
            //   现在 SAIN 注入已改成**每帧轮转 2 只**（不依赖扫描频率），扫描本身只负责刷新 bot 列表
            //   ⇒ 1.5 秒一次足够，直接把扫描的每秒开销再降 3 倍。
            _nextScan = t + 1.5f;

            var bc = GetBotsController();
            if (bc == null) return;

            var player = GetMainPlayer();
            if (player == null) return;

            // ★★ 安全闸门 2：玩家必须在世界里（已落地/已出生），否则说明还在加载
            try
            {
                var tr = player.GetType().GetProperty("Transform", BindingFlags.Public | BindingFlags.Instance)?.GetValue(player)
                      ?? player.GetType().GetProperty("PlayerTransform", BindingFlags.Public | BindingFlags.Instance)?.GetValue(player);
                if (tr == null) return;
            }
            catch { }

            // BotsController.Bots 是 BotsList —— **它本身不是 IEnumerable**，
            // 真正的列表是它的 BotOwners 属性（IEnumerable<BotOwner>）或 _botOwners 字段。
            var botsObj = bc.GetType().GetField("Bots", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(bc)
                       ?? bc.GetType().GetProperty("Bots", BindingFlags.Public | BindingFlags.Instance)?.GetValue(bc);
            if (botsObj == null) { if (DiagOn) Diag("[HOST] 扫描: Bots=null（字段名可能不同）"); return; }

            var bots = botsObj.GetType().GetProperty("BotOwners", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botsObj) as System.Collections.IEnumerable
                    ?? botsObj.GetType().GetField("_botOwners", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(botsObj) as System.Collections.IEnumerable
                    ?? botsObj as System.Collections.IEnumerable;
            if (bots == null)
            {
                var cnt = botsObj.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botsObj);
                if (DiagOn) Diag($"[HOST] 扫描: BotsList 取不到 BotOwners（Count={cnt}）");
                return;
            }   // ★ 分段探针：loop
            int total = 0, hostile = 0, noGroup = 0, forced = 0;
            // ★★ 权威的角色分布统计：读的是 raid 里【真实存在】的 bot（服务端那个"刷新统计"会虚高，
            //    因为 PrepareAndGenerateBot 的去重因 Nullable<MongoId>.ToString() 返回 "" 而失效）。
            var roleDist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (bots != null)
            {
                foreach (var bot in bots)
                {
                    if (bot == null) continue;
                    total++;
                    var r0 = RoleOf(bot);
                    if (!string.IsNullOrEmpty(r0)) roleDist[r0] = roleDist.TryGetValue(r0, out var rc) ? rc + 1 : 1;
                    var role = r0;   // ★ v1.0.1【M】删重复 RoleOf
                    if (string.IsNullOrEmpty(role) || !HostileRoles.Contains(role)) continue;
                    hostile++;
                    // ★★ v0.1.5 修正（用户实测："近战的僵尸一直有，有空手的也有带近战武器的"）：
                    //   原来只留前 8 只 ⇒ 拿枪的一多，**近战型永远进不了采样** ⇒ 日志一直显示"近战 0 只"。
                    //   现在放大到 40 只全员入队，dump 时再按"是否枪械"挑选。
                    if (_dumpQueue.Count < 40) _dumpQueue.Add(bot);

                    // ★★★ v1.0.1【性能·第十三轮】**改成入队，不在这里当场做**。
                    //   原来这两行在扫描循环里对全部 bot 一次性执行 ⇒ 一帧 370×1.5ms ≈ 140ms（PerfOSD 实测）。
                    //   现在只入队，真正的调用由 TickInjections() 每帧摊 2 只 ⇒ 单帧增量 ≈ 3ms。
                    //   ⚠ 行为等价：这两件事都**只依赖 bot 自身**（不依赖本轮扫描的中间状态），
                    //      延后到后续帧执行结果完全相同；且同一只 bot 的 2 秒节流仍在 InjectEnemyKnown 内部生效。
                    if (_injectQueue.Count < 512 && _injectSeen.Add(bot)) _injectQueue.Add(bot);   // HashSet 去重（O(1)）

                    var id = BotIdOf(bot);
                    // ★ v1.0.1【性能】`BotsGroup` 的成员按 Type 缓存（原来每只 bot 每轮做 2 次裸 GetProperty/GetField）
                    var bt1 = bot.GetType();
                    if (bt1 != _botGroupType)
                    {
                        _botGroupPi = bt1.GetProperty("BotsGroup", BindingFlags.Public | BindingFlags.Instance);
                        _botGroupFi = bt1.GetField("BotsGroup", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        _botGroupType = bt1;
                    }
                    var group = _botGroupPi?.GetValue(bot) ?? _botGroupFi?.GetValue(bot);
                    if (group == null)
                    {
                        noGroup++;
                        // 无组也尝试直接建立敌对关系（Memory.SetEnemy / GoalEnemy）
                        TrySetGoalEnemy(bot, player);
                        continue;
                    }
                    // ★★ v0.1.5 修正：原来加过一次就永久跳过（_done）⇒ 但实测 `[敌方状态] ②-b 玩家在【组敌人表】里 = 否 ❌`
                    //   ⇒ 说明"加敌"会**丢失**（组可能在 bot 生成后被替换/重建）。现在改成**周期性重申**：
                    //   每 15 秒清空一次已处理集合，让加敌重新走一遍（幂等，成功后不会重复加）。
                    if (UnityEngine.Time.realtimeSinceStartup - _lastReassert > 15f)
                    {
                        _lastReassert = UnityEngine.Time.realtimeSinceStartup;
                        _done.Clear();
                        ClearRoleCache();     // ★ v0.1.7【Y4 护栏②】每局重置点：一并清空 role 缓存（换局瞬间无 bot 在跑 ⇒ 不会尖峰）
                    }
                    if (!string.IsNullOrEmpty(id) && _done.Contains(id)) continue;

                    // ★★★ 最终根因（日志实测）：走私者的 BotsGroup **成员数=0**
                    //   → _isFirstMemberAdded 永远 false → AddEnemy 一律被拒。
                    //   而设置该标志的唯一途径是 AddMember —— 这些 bot 从没被注册进组！
                    //   （ABPS 追加的 boss 条目不走 BotZoneGroups.AddBot，所以缺了注册这一步）
                    // ★★ 已移除反射调用 BotsGroup.AddMember(bot) ★★
                    //    原因：它会连带触发 BotsGroup.MakeStrategyDecision()（boss 组决策逻辑），
                    //    对非 boss 组强行调用会打乱 bot 的组状态与物理表现
                    //    （实测：AI 悬浮在半空、刷新不完全）。
                    //    现在完全依赖 SAIN 注入路径（探针已验证 EnemyKnown=True / IsCurrentEnemy=True）。

                    // ★★★ v1.0.1【性能·第十八轮】`GetMethods()` 全表扫描 + 对每个候选调 `GetParameters()`，
                    //   实测每只敌对 bot 都做一次 ⇒ 扫描循环的固定大头。改为按 Type 缓存（BotOwnerFixedUpdatePatch 早就这么做了）。
                    var m = AddEnemyMiOf(group.GetType());
                    if (m == null) { DiagKey("扫描: 找不到 AddEnemy(2参)"); continue; }

                    // ★ 关键：AddEnemy 开头会检查 _isFirstMemberAdded，false 时直接 return false
                    //   （把玩家丢进 _recheckPersonsAfterInit 就不管了）
                    //   组里已经有人（_members 非空）说明初始化其实已完成，这个标志漏设了 → 补上
                    try
                    {
                        var f = GroupFieldsOf(group.GetType()).first;   // ★ 缓存
                        if (f != null && f.GetValue(group) is bool b && !b)
                        {
                            var members = GroupFieldsOf(group.GetType()).members?.GetValue(group) as System.Collections.ICollection;   // ★ 缓存
                            // ★★ v0.1.5 修正（实测铁证：`所有 cause 都被拒 … 成员数=0 _isFirstMemberAdded=False`）
                            //   原实现只在"组内已有成员"时才补设该标志 ⇒ 服务端追加的刷点（组内 0 人）**永远补不上**
                            //   ⇒ AddEnemy 一律被拒 ⇒ 僵尸不知道玩家是敌人 ⇒ 不打人。
                            //   现在**无论成员数多少都补设**（只是打开一个门闩，不调用 AddMember ⇒ 不会触发组决策/悬浮）。
                            if (members != null)
                            {
                                f.SetValue(group, true);
                                if (DiagOn) _log?.LogInfo($"扫描: 补设 _isFirstMemberAdded=true（组内 {members.Count} 人）role={role}");
                            }
                        }
                    }
                    catch { }

                    // ★★ AddEnemy 返回 False 的两个原因（都已反编译确认）：
                    //   ① if (!_isFirstMemberAdded) { _recheckPersonsAfterInit.Add(p); return false; }
                    //   ② if (USE_ADD_TO_ENEMY_VALIDATION && !VALID_REASONS_TO_ADD_ENEMY.Contains(cause)) return false;
                    //  而 EBotEnemyCause 的 0 是 pairLogic（很可能不在合法列表）→ 必须试其它 cause
                    try
                    {
                        var fFirst = GroupFieldsOf(group.GetType()).first;   // ★ 缓存
                        if (fFirst != null && fFirst.GetValue(group) is bool b0 && !b0)
                        {
                            // _members 是 **public** 字段（之前用 NonPublic 取不到，导致成员数=-1）
                            var mems = GroupFieldsOf(group.GetType()).members?.GetValue(group) as System.Collections.ICollection;   // ★ 缓存
                            if (mems != null && mems.Count > 0)
                            {
                                fFirst.SetValue(group, true);
                                DiagKey($"★补设 _isFirstMemberAdded=true（组内 {mems.Count} 人）role={role}");
                            }
                        }
                    }
                    catch { }

                    // 逐个尝试合法的 EBotEnemyCause（0=pairLogic 很可能被拒）
                    var causeType = AddEnemyCauseTypeOf(group.GetType(), m);   // ★ 缓存
                    // ★ v1.0.1【性能·第十八轮】候选名数组改成静态常量（原来每次 new 一个数组）
                    var candidates = _enemyCauseNames;
                    bool done = false;
                    foreach (var cname in candidates)
                    {
                        object cause;
                        // ★ v1.0.1【性能·第十八轮】`Enum.Parse` 是反射（~10~50µs）⇒ 按「枚举类型+名字」缓存
                        cause = CauseValueOf(causeType, cname);
                        if (cause == null) continue;
                        try
                        {
                            _addEnemyArgs[0] = player; _addEnemyArgs[1] = cause; var ok = m.Invoke(group, _addEnemyArgs);   // ★ 复用数组
                            if (ok is bool bb && bb)
                            {
                                if (!string.IsNullOrEmpty(id)) _done.Add(id);
                                forced++; done = true;
                                DiagKey($"★扫描强制加敌成功: role={role} bot={id} cause={cname} AddEnemy→True");
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            _opFailures++;
                            var inner = ex.InnerException ?? ex;
                            DiagKey($"扫描: AddEnemy({cname}) 异常({_opFailures}): {inner.GetType().Name}: {inner.Message}");
                            break;
                        }
                    }
                    if (!done)
                    {
                        var mems3 = GroupFieldsOf(group.GetType()).members?.GetValue(group) as System.Collections.ICollection;   // ★ 缓存
                        var first = GroupFieldsOf(group.GetType()).first?.GetValue(group);   // ★ 缓存
                        // ★★★ v1.0.1【性能·第十一轮】原来这里「无条件写日志」——
                        //   它在 `if (!done)` 里，即「组这条路失败时」，会**每只 bot 每轮扫描都写一条**。
                        //   用户日志实证：`已加敌=0` 持续出现 ⇒ 这条每轮对每只 bot 都触发 ⇒ 战局内刷屏。
                        //   现在只有诊断开关打开时才写（要判断修复有没有跑，请在 F12 打开「★ 诊断日志」）。
                        if (DiagOn) _log?.LogInfo($"扫描: 所有 cause 都被拒 role={role} bot={id} 成员数={(mems3 == null ? -1 : mems3.Count)} _isFirstMemberAdded={first}");
                        // ★★ v0.1.5：组这条路走不通时，退到**每只 bot 自己的 Memory**（游戏自带 SetEnemy(IPlayer)，
                        //   安全、不会触发组决策）。实测很多"服务端追加的刷点"组是空组 ⇒ 只有这条兜底能让它认玩家为敌。
                        try
                        {
                            TrySetGoalEnemy(bot, player);
                            if (DiagOn) _log?.LogInfo($"扫描: AddEnemy 被拒 ⇒ 已对 Memory.SetEnemy(玩家) 兜底 role={role} bot={id}");
                        }
                        catch { }
                    }
                }
            }

            // ★★ 合成一行输出：扫描计数 + 真实角色分布
            //   （以前分两行走 DiagSum 的 10 秒限频，同一秒只写得进一条 → 两行来自不同时间窗，
            //    看起来像"活动敌种=0 但真实分布里却有祭司"这种自相矛盾，实际是指标错位）
            // ★★★ v1.0.1【性能·第十五轮】`distStr` 只在下方 `if (DiagOn)` 里用到，
            //   而这段 LINQ（Where + OrderByDescending + Select 拼串 + string.Join）原来**无条件执行**
            //   ⇒ 诊断关闭时完全是白算。现在整段挪进 DiagOn 分支。
            string distStr = "";
            if (DiagOn) try
            {
                var interesting = roleDist.Where(kv => HostileRoles.Contains(kv.Key) || kv.Key.StartsWith("boss") || kv.Key.StartsWith("sectant") || kv.Key.StartsWith("infected") || kv.Key.StartsWith("follower") || kv.Key.StartsWith("crazy") || kv.Key.StartsWith("arena"))
                                          .OrderByDescending(kv => kv.Value).Select(kv => kv.Key + "×" + kv.Value);
                distStr = string.Join(" ", interesting);
            }
            catch { }
            // ★★★ v0.1.5 自动诊断：每 10 秒 dump 前两只"活动敌种"的武器与战斗/目标状态（不需要按键）。
            //   目的：定位"拿枪的僵尸打我、近战的僵尸不打我"卡在哪一层
            //   （手中武器 / BotsGroup / GoalEnemy / SAIN EnemyKnown / 组内是否把玩家当敌人）。
            try
            {
                // ★★★ v1.0.1【性能·第十一轮】整块只在诊断开关打开时跑。
                //   这块会做：逐 bot 反射读 WeaponManager/CurrentWeapon/Melee/HaveMelee/BrainLayer
                //   + 两处 string.Join + 多行 LogInfo —— 是当初排查「僵尸近战不打人」时加的调试代码，
                //   **不该常驻在战局热路径上**（用户日志实证：一局刷了十几轮）。
                if (DiagOn && _dumpQueue.Count > 0 && UnityEngine.Time.realtimeSinceStartup - _lastAutoDump > 10f)
                {
                    _lastAutoDump = UnityEngine.Time.realtimeSinceStartup;
                    // ★ 分类：非枪械（近战/空手）优先 —— 实测"拿手枪的会打人、近战的不会"，必须先把近战那批 dump 出来
                    var nonGun = new List<object>(); var gun = new List<object>();
                    foreach (var b0 in _dumpQueue)
                    {
                        var tn = WeaponTypeName(b0) ?? "";
                        if (tn.Length == 0 || tn.IndexOf("Melee", StringComparison.OrdinalIgnoreCase) >= 0
                            || tn.IndexOf("Knife", StringComparison.OrdinalIgnoreCase) >= 0) nonGun.Add(b0);
                        else gun.Add(b0);
                    }
                    var picks = new List<object>();
                    picks.AddRange(nonGun.Take(2));
                    picks.AddRange(gun.Take(2));                 // ★ 枪械的也采 2 只 ⇒ 同一份日志里可以直接**对照**两种脑层/武器
                    if (picks.Count == 0) picks.AddRange(_dumpQueue.Take(2));
                    _log?.LogInfo($"[EventRotator][自动诊断] dump {picks.Count} 只（近战/非枪械 {nonGun.Count} 只、枪械 {gun.Count} 只）：");
                    // ★★ v0.1.5：**脑层分布统计**（覆盖全部采样，含未 dump 的）——用来直接对照
                    //   "会打你的僵尸" 与 "不打你的僵尸" 分别跑在哪个脑层（实测非攻击的那批是 InfectedSlow）。
                    try
                    {
                        var layers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (var b3 in _dumpQueue)
                        {
                            var l = BrainLayer(b3) ?? "(null)";
                            layers[l] = layers.TryGetValue(l, out var c3) ? c3 + 1 : 1;
                        }
                        _log?.LogInfo("[EventRotator][自动诊断] 脑层分布：" + string.Join("　", layers.OrderByDescending(x => x.Value).Select(x => x.Key + "×" + x.Value)));
                    }
                    catch { }
                    foreach (var b2 in picks)
                    {
                        try
                        {
                            var wm = b2.GetType().GetProperty("WeaponManager", BindingFlags.Public | BindingFlags.Instance)?.GetValue(b2);
                            var cw = wm?.GetType().GetProperty("CurrentWeapon", BindingFlags.Public | BindingFlags.Instance)?.GetValue(wm);
                            var nm = cw?.GetType().GetProperty("ShortName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(cw)
                                  ?? cw?.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(cw);
                            // ★★ v0.1.5：近战武器在 **WeaponManager.Melee**（客户端实证：`WeaponManager.Melee.HaveMelee`
                            //   / `.RunToEnemyUpdate()` / `.OnEnemyHitted`）。"空手"的僵尸到底是**真没有近战武器**，
                            //   还是只是没拿在手里 ⇒ 用 `HaveMelee` 一锤定音。
                            string meleeInfo = "Melee=取不到";
                            try
                            {
                                var mw = wm?.GetType().GetProperty("Melee", BindingFlags.Public | BindingFlags.Instance)?.GetValue(wm);
                                if (mw != null)
                                {
                                    var have = mw.GetType().GetProperty("HaveMelee", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mw);
                                    var mm = mw.GetType().GetProperty("CurrentMelee", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mw)
                                          ?? mw.GetType().GetProperty("MeleeWeapon", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mw);
                                    var mn = mm?.GetType().GetProperty("ShortName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mm)
                                          ?? mm?.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mm);
                                    meleeInfo = $"Melee.HaveMelee={have} 近战物品={mn?.ToString() ?? "-"}";
                                }
                            }
                            catch (Exception eM) { meleeInfo = "Melee=读取异常:" + eM.GetType().Name; }
                            _log?.LogInfo($"[EventRotator][自动诊断] role={RoleOf(b2)} 手中武器={nm?.ToString() ?? "（枪械槽为空 ⇒ 已切近战）"} 类型={cw?.GetType().Name ?? "-"} ｜ {meleeInfo}");
                        }
                        catch (Exception ew) { _log?.LogInfo($"[EventRotator][自动诊断] 取武器失败: {ew.GetType().Name}"); }
                        try { SainProbe.DumpEnemyState(_log, b2); } catch (Exception ed) { _log?.LogInfo($"[EventRotator][自动诊断] SAIN dump 失败: {ed.GetType().Name}"); }
                    }
                    _dumpQueue.Clear();
                }
            }
            catch { }

            // ★★★ v1.0.1【性能·第十一轮·用户实测反馈】原来这里是「**直写日志，不限频**」——
            //   扫描跑在重试计时器上，一局会写盘数百次（用户日志实证：一局 250 行 EventRotator，
            //   其中「扫描: bots总数=…」占了大部分）。**这是战局内卡顿的直接来源之一。**
            //   现在：只有诊断开关打开时才拼接 + 写盘；关闭时连 `line` 都不构造。
            if (DiagOn)
            {
                var line = $"扫描: bots总数={total} 活动敌种={hostile} 无组={noGroup} 已加敌={forced} 玩家={player.ProfileId}"
                         + (string.IsNullOrEmpty(distStr) ? "" : $" ｜ 真实分布: {distStr}");
                DiagSum(line);
                try { _log?.LogInfo("[EventRotator] " + line); } catch { }
            }
            if (total == 0) return;                  // ★ 没有 bot 就绝不做任何写入
            _opFailures = 0;                         // 有正常数据了 → 清空熔断计数
        }
        catch (Exception ex) { if (DiagOn) Diag("[HOST] ScanAndForceEnemy 异常: " + ex.GetType().Name + ": " + ex.Message); }
    }

    /// <summary>
    /// 无 BotsGroup 时的兜底。
    /// ★★ 已废弃原有的 Memory.GoalEnemy = player 赋值 ★★
    ///   原因：BotOwner.Memory.GoalEnemy 期望的是 EnemyInfo 类型，塞入 IPlayer 会导致
    ///   类型不匹配 → Memory 状态损坏 → AI 行为异常（悬空/僵住）。
    ///   现在只调用游戏自己的 Memory.SetEnemy(IPlayer)（若存在），否则什么都不做。
    /// </summary>
    private static void TrySetGoalEnemy(object bot, IPlayer player)
    {
        try
        {
            var mem = bot.GetType().GetProperty("Memory", BindingFlags.Public | BindingFlags.Instance)?.GetValue(bot);
            if (mem == null) return;
            var m = mem.GetType().GetMethod("SetEnemy", BindingFlags.Public | BindingFlags.Instance, null,
                                            new[] { typeof(IPlayer) }, null);
            if (m != null) { m.Invoke(mem, new object[] { player }); if (DiagOn) Diag("[HOST] 无组兜底: Memory.SetEnemy 已调用"); }
            // ★ 不再直接写 GoalEnemy（类型不匹配会损坏 Memory）
        }
        catch { }
    }

    /// <summary>
    /// 全反射版 Tick：用**游戏自己使用的路径**取 BotsController 与本地玩家。
    /// 关键：BotSettings.IsPlayerEnemy 里就是
    ///   Singleton&lt;IBotGame&gt;.Instance.BotsController
    /// 所以这里照搬 —— 比去找 GameWorld.Instance 可靠。
    /// </summary>
    internal static void TickFromGameWorld()
    {
        try
        {
            if (!Enabled) return;
            var bc = GetBotsController();
            if (bc == null) { ResetTry(); return; }
            var me = GetMainPlayer();
            if (me == null) return;
            Tick(bc, me);
        }
        catch { }
    }

    private static Type _ibotGameType;
    private static Type _botGameType;
    private static PropertyInfo _botCtlProp;
    private static bool _resolved;
    private static Type _singletonG;

    private static float _nextBcProbe;
    private static BotsController _cachedBc;

    private static BotsController GetBotsController()
    {
        // ★ 性能：缓存 2 秒。★ 但 null 不缓存（保证进图后能立刻取到）
        if (UnityEngine.Time.realtimeSinceStartup < _nextBcProbe && _cachedBc != null) return _cachedBc;
        _nextBcProbe = UnityEngine.Time.realtimeSinceStartup + 2f;
        var bc = ResolveBotsController();
        if (bc != null) _cachedBc = bc;
        return bc;
    }

    private static BotsController ResolveBotsController()
    {
        try
        {
            if (!_resolved)
            {
                _resolved = true;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] all;
                    try { all = a.GetTypes(); } catch { continue; }
                    foreach (var t in all)
                    {
                        if (t.Name == "IBotGame") _ibotGameType = t;
                        else if (t.Name == "BotGame") _botGameType = t;
                    }
                    if (_ibotGameType != null && _botGameType != null) break;
                }
                var src = _botGameType ?? _ibotGameType;
                _botCtlProp = src?.GetProperty("BotsController", BindingFlags.Public | BindingFlags.Instance);
            }

            // Singleton<IBotGame>.Instance（游戏自己用的路径）
            // ★ 缓存 Singleton`1 类型，避免每次调用都遍历程序集（曾经是严重性能问题）
            if (_singletonG == null)
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { var sg = a.GetType("Comfort.Common.Singleton`1", false); if (sg != null) { _singletonG = sg; break; } } catch { }
                }
            }
            // ★★★ v1.0.1【性能·第十六轮】原来这里**每次调用**都做
            //     `MakeGenericType` + `GetProperty("Instance")` —— 两者都是几十微秒~毫秒级。
            //   `GetBotsController()` 的缓存在 `_cachedBc == null` 时不生效 ⇒ 取不到时每次扫描都重做。
            //   现在把**闭合泛型类型 + Instance 属性**缓存下来 ⇒ MakeGenericType 只做一次。
            object inst = null;
            if (inst == null && _ibotGameType != null && _singletonG != null)
            {
                // 兜底：万一直接引用拿不到，才走反射（且把闭合类型缓存下来，不重复 MakeGenericType）
                try
                {
                    if (_closedBotGameType == null)
                    {
                        _closedBotGameType = _singletonG.MakeGenericType(_ibotGameType);
                        _closedBotGameInstProp = _closedBotGameType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    }
                    inst = _closedBotGameInstProp?.GetValue(null);
                }
                catch { }
            }
            if (inst == null && _botGameType != null)
                inst = _botGameType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (inst == null) return null;

            return _botCtlProp?.GetValue(inst) as BotsController
                ?? inst.GetType().GetProperty("BotsController", BindingFlags.Public | BindingFlags.Instance)?.GetValue(inst) as BotsController;
        }
        catch { return null; }
    }

    /// <summary>
    /// 取本地玩家。客户端 GameWorld 是 Unity 组件，**没有静态 Instance**，
    /// 所以用 FindObjectOfType 找它，再读它的 MainPlayer **字段**（不是属性）。
    /// 若失败再退化到 BotsController.Players。
    /// </summary>
    /// <summary>给延迟初始化用的公开判据：能否摸到本地玩家（= 真的进图了）。</summary>
    internal static bool HasLocalPlayer()
    {
        try { return GetMainPlayer() != null; } catch { return false; }
    }

    /// <summary>Singleton&lt;IBotGame&gt;.Instance 是否存在（= 真正进图了；主界面为 false）。
    /// ★ 类型只在第一次查找并缓存，之后每次调用只做一次属性读取，几乎零开销。</summary>
    private static Type _singletonType;
    private static Type _ibotGameTypeCached;
    private static bool _singletonTypesSearched;

    internal static bool HasBotGame()
    {
        try
        {
            if (!_singletonTypesSearched)
            {
                _singletonTypesSearched = true;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (_singletonType == null) _singletonType = a.GetType("Comfort.Common.Singleton`1", false);
                        if (_ibotGameTypeCached == null) _ibotGameTypeCached = a.GetType("EFT.IBotGame", false);
                    }
                    catch { }
                    if (_singletonType != null && _ibotGameTypeCached != null) break;
                }
            }
            if (_singletonType == null || _ibotGameTypeCached == null) return false;

            // ★ v1.0.1【性能·第十六轮】同样：缓存闭合类型，MakeGenericType 只做一次
            if (_closedBotGameType2 == null)
            {
                _closedBotGameType2 = _singletonType.MakeGenericType(_ibotGameTypeCached);
                _closedBotGameInstProp2 = _closedBotGameType2.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            }
            var inst = _closedBotGameInstProp2?.GetValue(null);
            return inst != null;
        }
        catch { return false; }
    }

    /// <summary>给 SAIN 探针用：取 BotsController。</summary>
    internal static BotsController GetBotsControllerPublic()
    {
        try { return GetBotsController(); } catch { return null; }
    }

    // ★★★ 性能修复：本地玩家缓存。
    //   GetMainPlayer() 内部有 FindObjectOfType（全场景扫描）+ FindType，极贵。
    //   之前它被每帧/每秒调用多次 → 部署倒计时卡顿、进图极慢。
    //   现在缓存 2 秒：期间直接返回上次结果。
    private static float _nextPlayerProbe;
    private static IPlayer _cachedPlayer;

    internal static IPlayer GetMainPlayer()
    {
        // 缓存命中（2 秒内）→ 但要先确认缓存的 Unity 对象还活着（跨对局时会失效）
        if (UnityEngine.Time.realtimeSinceStartup < _nextPlayerProbe && _cachedPlayer != null)
        {
            bool alive = true;
            try
            {
                if (_cachedPlayer is UnityEngine.Object uo && uo == null) alive = false;   // Unity 已销毁
            }
            catch { }
            if (alive) return _cachedPlayer;
        }
        _nextPlayerProbe = UnityEngine.Time.realtimeSinceStartup + 2f;
        var p = ResolveMainPlayer();
        // ★ 只缓存有效结果（null 不缓存，保证一旦进图就能立刻拿到玩家）
        if (p != null) _cachedPlayer = p;
        return p;
    }

    private static IPlayer ResolveMainPlayer()
    {
        // ① GameWorld.MainPlayer（★ 已改为 O(1) 单例取法，见 GameWorldInstance 顶部注释）
        try
        {
            var gwType = FindType("EFT.GameWorld");
            if (gwType != null)
            {
                var gw = GameWorldInstance();      // ★ 不再 FindObjectOfType
                if (gw != null)
                {
                    var p = gwType.GetField("MainPlayer", BindingFlags.Public | BindingFlags.Instance)?.GetValue(gw) as IPlayer
                         ?? gwType.GetProperty("MainPlayer", BindingFlags.Public | BindingFlags.Instance)?.GetValue(gw) as IPlayer;
                    if (p != null) return p;
                }
            }
        }
        catch { }

        // ② 退化：从 BotsController.Players 里找非 AI 的那个
        try
        {
            var bc = GetBotsController();
            var players = bc?.GetType().GetProperty("Players", BindingFlags.Public | BindingFlags.Instance)?.GetValue(bc);
            if (players != null)
            {
                if (players is System.Collections.IEnumerable en)
                    foreach (var x in en) { if (x is IPlayer ip && !ip.IsAI) return ip; }
                // 有些实现提供 GetPlayer / GetAlivePlayer
                foreach (var mn in new[] { "GetMainPlayer", "MainPlayer", "GetPlayer" })
                {
                    var v = players.GetType().GetMethod(mn, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(players, null) as IPlayer;
                    if (v != null) return v;
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>给 SAIN 层补齐用：活动敌种角色名列表。</summary>
    internal static List<string> HostileRolesList() { try { return new List<string>(HostileRoles); } catch { return new List<string>(); } }

    /// <summary>不做限频的关键日志（供一次性初始化/注册结果使用）。</summary>
    internal static void DiagAlways(string msg)
    {
        if (DiagOn) Diag(msg);
        try { _log?.LogInfo("[EventRotator] " + msg); } catch { }
    }

    /// <summary>共享的类型查找缓存（供 SainProbe 使用）。</summary>
    internal static Type FindTypePublic(string fullName) => FindType(fullName);

    // ★★★ v1.0.1【性能·第二十轮】`FindType` 的**负缓存**
    //   实测：`Inject stage=12 total=35.7ms` —— 是 `FindType("SAIN.…BotSpawnController")` 找不到时
    //   **每次重新遍历全部程序集**（原来只缓存成功结果，找不到就白扫）。
    //   现在：找不到的记下时间，**10 秒内不再重试**；10 秒后仍会重试（SAIN 晚加载也能恢复）。
    //   ⇒ 单次 35ms 的代价从"每只 bot 一次"降为"每 10 秒一次"。
    private static readonly Dictionary<string, float> _typeMissAt = new(StringComparer.Ordinal);

    private static Type FindType(string fullName)
    {
        if (_typeCache.TryGetValue(fullName, out var cached) && cached != null) return cached;
        // ★ 负缓存：10 秒内不重试
        if (_typeMissAt.TryGetValue(fullName, out var missAt)
            && UnityEngine.Time.realtimeSinceStartup - missAt < 10f) return null;
        Type found = null;
        try
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = a.GetType(fullName, false); if (t != null) { found = t; break; } } catch { }
            }
        }
        catch { }
        if (found != null) { _typeCache[fullName] = found; _typeMissAt.Remove(fullName); }
        else { _typeMissAt[fullName] = UnityEngine.Time.realtimeSinceStartup; }
        return found;
    }
    private static readonly Dictionary<string, Type> _typeCache = new(StringComparer.Ordinal);
    // ★ v1.0.1【性能·第十六轮】闭合泛型类型的缓存（MakeGenericType 只做一次）
    private static Type _closedBotGameType, _closedBotGameType2;
    private static PropertyInfo _closedBotGameInstProp, _closedBotGameInstProp2;

    private static Type _gwType;
    private static PropertyInfo _gwInstProp;

    // ══════════ ★★★ v1.0.1【性能·第十三轮·PerfOSD 实测定位】干掉 FindObjectOfType ══════════
    //   **实测证据**（PerfOSD）：战局内 `frame` 稳定在 134~150ms、约每秒 1 次、`gc0=+0`
    //     ⇒ 固定工作量的同步阻塞、且不触发 GC。
    //   **根因**：`ResolveMainPlayer()` ① 用 `UnityEngine.Object.FindObjectOfType(EFT.GameWorld)`
    //     —— 这会**遍历场景里每一个 GameObject**（战局里几千上万个对象）⇒ 单次几十~一百多毫秒。
    //     而 `GetMainPlayer()` 的缓存只有 2 秒 ⇒ **每 2 秒一次全场景扫描**。
    //   **修法**：改用 `Comfort.Common.Singleton<GameWorld>.Instance` —— 这是游戏自己的单例，
    //     **O(1)** 直接取，完全不做扫描。（本项目里 RitualExitLock / ToxinInfection / RandomDebuff
    //     早就在用它，只是这里当初写了 FindObjectOfType。）
    //   兜底：`FindObjectOfType` 只在单例拿不到时走，且**结果缓存 2 秒**（进图瞬间的过渡期用）。
    private static PropertyInfo _singletonGwProp;
    private static Type _singletonGwClosedType;
    private static object _gwCached;
    private static float _gwCachedAt = -1f;

    /// <summary>O(1) 取 GameWorld：优先 Singleton&lt;GameWorld&gt;.Instance，绝不扫场景。</summary>
    // ★★★ v1.0.1【性能·第十六轮·PerfOSD 实测定位】改用**直接类型引用**，不再反射 ══════════════
    //   **实测证据**：`ScanAndForceEnemy` 30 次里 28 次是 49~55ms，而那 2 次走到末尾的 pre=0.0ms
    //     ⇒ 50ms 全部花在「入口→节流」的 `IsGameWorldPresentCached()` → `GameWorldInstance()`
    //       → 本函数上，而且只在**失败回退路径**上。
    //   **根因**：上面那版用反射拿 `Comfort.Common.Singleton`1`.Instance`。
    //     一旦 `GetProperty("Instance")` 返回 null（Instance 在游戏里是不是属性并不确保），
    //     `_singletonGwProp` **永远是 null** ⇒ `if (_singletonGwProp == null)` **永远为真**
    //     ⇒ **每次调用都重跑 `FindType` ×2 + `MakeGenericType`** —— `MakeGenericType` 是几十毫秒级的操作，
    //       而它被 3 秒节流的 `IsGameWorldPresentCached()` 反复触发。
    //   **修法**：`Comfort.dll` 本来就在工程引用里（RitualExitLock 一直直接用真实类型），
    //     这里直接 `Comfort.Common.Singleton<GameWorld>.Instance` —— 编译期解析，O(1)，零反射。
    //     万一抛异常（例如类型缺失），用 `_singletonGwBroken` 一次性关掉后续重试，避免反复付代价。
    private static bool _singletonGwBroken;

    private static object TryGameWorldSingleton()
    {
        if (_singletonGwBroken) return null;
        try
        {
            return Comfort.Common.Singleton<GameWorld>.Instance;
        }
        catch
        {
            _singletonGwBroken = true;   // 只失败一次就彻底关掉，不再付代价
            return null;
        }
    }

    private static object GameWorldInstance()
    {
        try
        {
            // ★ 第一优先：单例（O(1)，零扫描）
            var o = TryGameWorldSingleton();
            if (o != null) { _gwCached = o; _gwCachedAt = UnityEngine.Time.realtimeSinceStartup; return o; }
            // ★ 第二优先：2 秒内的上次结果（进图过渡期单例可能还没就绪）
            if (_gwCached != null && (UnityEngine.Time.realtimeSinceStartup - _gwCachedAt) < 2f)
            {
                try { if (!(_gwCached is UnityEngine.Object uo2 && uo2 == null)) return _gwCached; } catch { }
            }
            // ★★★ v1.0.1【性能·第十七轮】**去掉全场景 FindObjectOfType 兜底**。
            //   实测它单次几十毫秒且**仍然返回 null**（所以每次都要重扫）⇒ 纯浪费。
            //   改用 `EFT.GameWorld.Instance` 静态属性（游戏自己的单例访问器，O(1)）。
            if (_gwType == null) _gwType = FindType("EFT.GameWorld");
            if (_gwType == null) return null;
            try
            {
                if (_gwInstProp == null) _gwInstProp = _gwType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                var viaProp = _gwInstProp?.GetValue(null);
                if (viaProp != null) { _gwCached = viaProp; _gwCachedAt = UnityEngine.Time.realtimeSinceStartup; }
                return viaProp;
            }
            catch { return null; }
        }
        catch { return null; }
    }

    /// <summary>
    /// 【核心动作】把玩家直接写进"活动敌种所在组"的敌人名单 —— 用游戏自己的 BotsGroup.AddEnemy。
    /// 遍历 BotsController 的两个组容器：Groups（_bossGroup 集合）与 BotZoneGroups.data（按角色分组）。
    /// </summary>
    internal static void ForcePlayerIntoEnemyGroups(BotsController bc, IPlayer player)
    {
        if (bc == null || player == null) return;
        int forced = 0, scanned = 0;

        void Handle(object group)
        {
            try
            {
                if (group == null) return;
                scanned++;
                var g = group as BotsGroup;
                if (g == null) return;

                // ① 该组是否是"活动敌种"的组？用组里第一个成员的角色判断
                var members = HostilityGuard.MembersProp(g.GetType())?.GetValue(g) as System.Collections.IEnumerable;
                bool hostileGroup = false;
                string groupRole = "";
                if (members != null)
                {
                    foreach (var m in members)
                    {
                        var r = RoleOf(m);
                        if (string.IsNullOrEmpty(r)) continue;
                        if (HostileRoles.Contains(r)) { hostileGroup = true; groupRole = r; break; }
                    }
                }
                if (!hostileGroup)
                {
                    // 退化：用 InitialBotType 再判一次
                    var t = g.GetType().GetProperty("InitialBotType", BindingFlags.Public | BindingFlags.Instance)?.GetValue(g)?.ToString();
                    if (t != null && HostileRoles.Contains(t)) { hostileGroup = true; groupRole = t; }
                }
                if (!hostileGroup) return;

                // ② 同队放过
                if (SameSquad(HostilityGuard.MembersProp(g.GetType())?.GetValue(g), player)) return;

                // ③ 直接加敌人（cause 用 3/4 之外的合法值：用枚举第 0 项最稳）
                var m2 = g.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                          .FirstOrDefault(x => x.Name == "AddEnemy" && x.GetParameters().Length == 2);
                if (m2 == null) { if (DiagOn) Diag("[HOST] 找不到 BotsGroup.AddEnemy"); return; }
                var causeType = m2.GetParameters()[1].ParameterType;
                object cause = Enum.GetValues(causeType).GetValue(0);
                var ok = m2.Invoke(g, new object[] { player, cause });
                forced++;
                if (DiagOn) Diag($"[HOST] ★AddEnemy 成功: 组角色={groupRole} 玩家={player.ProfileId} cause={cause} 返回={ok}");
            }
            catch (Exception ex) { if (DiagOn) Diag("[HOST] Handle(group) 异常: " + ex.GetType().Name + ": " + ex.Message); }
        }

        try
        {
            // ★ 正确链路（反编译确认）：
            //   BotsController.Groups → BotSpawner
            //     └ BotSpawner.Groups : BotZoneGroupsDictionary (Dictionary<BotZone, BotZoneGroups>)
            //         └ BotZoneGroups.data            Dictionary<WildSpawnType, BotsGroup>
            //           BotZoneGroups._bossGroup      HashSet<BotsGroup>
            //           BotZoneGroups._noConnectionGroups List<BotsGroup>

            object botSpawner = null;
            try { botSpawner = bc.GetType().GetProperty("Groups", BindingFlags.Public | BindingFlags.Instance)?.GetValue(bc); } catch { }
            if (botSpawner == null)
                botSpawner = bc.GetType().GetField("_botSpawner", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(bc);

            // BotSpawner.Groups = Dictionary<BotZone, BotZoneGroups>
            var bzgDict = botSpawner?.GetType().GetProperty("Groups", BindingFlags.Public | BindingFlags.Instance)?.GetValue(botSpawner)
                       ?? botSpawner?.GetType().GetField("<Groups>k__BackingField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(botSpawner);

            if (bzgDict is System.Collections.IDictionary dd)
            {
                foreach (System.Collections.DictionaryEntry kv in dd) ScanZoneGroups(kv.Value, Handle);
            }
            else if (bzgDict is System.Collections.IEnumerable de)
            {
                foreach (var v in de) ScanZoneGroups(v, Handle);
            }

            // 若拿不到 BotSpawner（旧版本），退化：直接在 controller 上找含 data 字段的对象
            if (botSpawner == null)
            {
                foreach (var f in bc.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var v = f.GetValue(bc);
                        if (v == null) continue;
                        if (v.GetType().GetField("data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null) ScanZoneGroups(v, Handle);
                    }
                    catch { }
                }
            }

            if (DiagOn) Diag($"[HOST] 第{_tryCount}次: 扫描 {scanned} 个组 → 加敌 {forced} 个（玩家 {player.ProfileId} Side={player.Side}）");
            if (forced > 0) ResetTry();      // 成功就不必再重试
        }
        catch (Exception ex) { if (DiagOn) Diag("[HOST] ForcePlayerIntoEnemyGroups 异常: " + ex.GetType().Name + ": " + ex.Message); }
    }

    /// <summary>从一个 BotZoneGroups 对象里取出全部 BotsGroup 交给 handle。</summary>
    private static void ScanZoneGroups(object zoneGroups, Action<object> handle)
    {
        if (zoneGroups == null) return;
        try
        {
            var t = zoneGroups.GetType();
            // data : Dictionary<WildSpawnType, BotsGroup>
            if (t.GetField("data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(zoneGroups) is System.Collections.IDictionary data)
                foreach (System.Collections.DictionaryEntry kv in data) handle(kv.Value);
            // _bossGroup : HashSet<BotsGroup>
            if (t.GetField("_bossGroup", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(zoneGroups) is System.Collections.IEnumerable boss)
                foreach (var g in boss) handle(g);
            // _noConnectionGroups : List<BotsGroup>
            if (t.GetField("_noConnectionGroups", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(zoneGroups) is System.Collections.IEnumerable nc)
                foreach (var g in nc) handle(g);
        }
        catch { }
    }

    /// <summary>
    /// 判断"目标"与"发起方"是否属于同一个小队（GroupId 相同且非空）。
    /// 同队 → 自己人，不打。
    /// </summary>
    // ══════════ ★★★ v1.0.1【性能】小队成员 id 解析缓存 ══════════
    //   `SameSquad` 在 4 个"每 bot × 每次敌我判定"的热点上被调用，原来**每个成员做 3 次反射**：
    //   GetProperty("ProfileId") → GetProperty("Id") → GetField("ProfileId")。
    //   小队成员类型在进程内恒定 ⇒ 按 Type 缓存解析结果即可（Type 变了自动重查）。
    private static PropertyInfo _piMemberProfileId, _piMemberId;
    private static FieldInfo _fiMemberProfileId;
    private static Type _memberTypeCache;

    /// <summary>★ v1.0.1【性能】取小队成员的 id（ProfileId 属性 → Id 属性 → ProfileId 字段，语义与改前逐字相同）。</summary>
    private static string MemberIdOf(object m)
    {
        if (m == null) return null;
        var t = m.GetType();
        if (t != _memberTypeCache)
        {
            _piMemberProfileId = t.GetProperty("ProfileId", BindingFlags.Public | BindingFlags.Instance);
            _piMemberId = t.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
            _fiMemberProfileId = t.GetField("ProfileId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _memberTypeCache = t;
        }
        return _piMemberProfileId?.GetValue(m)?.ToString()
            ?? _piMemberId?.GetValue(m)?.ToString()
            ?? _fiMemberProfileId?.GetValue(m)?.ToString();
    }

    internal static bool SameSquad(object membersObj, IPlayer target)
    {
        try
        {
            var tgtId = target?.ProfileId;
            if (string.IsNullOrEmpty(tgtId)) return false;

            // membersObj 可以是 BotOwner.BotsGroup（取它的 Members），也可以直接是成员表
            System.Collections.IEnumerable members = membersObj as System.Collections.IEnumerable;
            if (membersObj != null && !(membersObj is System.Collections.IEnumerable))
                members = HostilityGuard.MembersProp(membersObj.GetType())?.GetValue(membersObj) as System.Collections.IEnumerable;
            if (members == null) return false;

            foreach (var m in members)
            {
                if (m == null) continue;
                var mid = MemberIdOf(m);          // ★ v1.0.1【性能】按 Type 缓存（原来每个成员 3 次反射）
                if (string.Equals(mid, tgtId, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static readonly List<string> _diagBuf = new();
    private static ManualLogSource _log;

    internal static void SetLog(ManualLogSource log) => _log = log;

    // ★ v0.1.5：给每个敌对补丁加"到底有没有被调用"的一次性打点（最多 5 条），
    //   用于定位游戏实际走的是哪条判定路径（用户实测 BotSettings.IsPlayerEnemy 补丁 0 次触发）。


    /// <summary>★ v0.1.5：给补丁类用的**无条件**日志出口（不受"自检角标"开关影响，便于定位问题）。</summary>
    internal static void LogLine(string s) { try { _log?.LogInfo(s); } catch { } }

    /// <summary>把诊断缓冲写到 mod 目录（游戏内可随时看）。</summary>
    internal static void FlushDiag(string modDir)
    {
        try
        {
            List<string> copy;
            lock (_diagBuf) { if (_diagBuf.Count == 0) return; copy = new List<string>(_diagBuf); _diagBuf.Clear(); }
            File.AppendAllLines(Path.Combine(modDir, "敌对性诊断.txt"), copy);
        }
        catch { }
    }
}

/// <summary>
/// 【★ 辅助修复 ★】BotSettings.IsPlayerEnemy —— 人类玩家的"敌对数判定"入口。
/// 单独也补一道（双保险），只对活动敌种生效应为 true。
/// </summary>
internal sealed class BotSettingsIsPlayerEnemyPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(BotSettings), nameof(BotSettings.IsPlayerEnemy));

    [PatchPostfix]
    private static void Postfix(BotSettings __instance, IPlayer player, ref bool __result)
    {
        try
        {
            if (!HostilityGuard.Enabled) return;
            if (__result) return;                 // 本来就是敌人 → 不动
            if (player == null || player.IsAI) return;

            var role = HostilityGuard.RoleOfSettings(__instance);
            if (!HostilityGuard.HostileRoles.Contains(role)) return;

            // ★ v1.0.1【性能】原来这里**每次补丁生效都无条件插值 + 写日志**（每 bot × 每次敌我判定）。
            //   `LogLine` 是 `Diag` 之外的第二条日志通道，`DiagOn` 管不到它 ⇒ 现在一起受 `DiagOn` 管。
            if (HostilityGuard.DiagOn)
                HostilityGuard.LogLine($"[EventRotator][HOST] BotSettings.IsPlayerEnemy 强制 true ← 补丁生效 role={role} profile={player.ProfileId}");
            __result = true;
        }
        catch { }
    }
}

/// <summary>
/// 【★★★ 统一收尾修复 ★★★】让活动敌种"敌视一切非本小队成员"。
///
/// 完整还原的 BotsGroup.IsPlayerEnemy 判定链（反编译客户端，146 条 IL）：
///
///   result = false;                                   // IL_0000
///   if (player.IsAI) { ...阵营名单... }                // AI 分支
///   else result = BotSettings.IsPlayerEnemy(player);   // 人类：读敌对数（一直是对的）
///
///   switch (player.Side) {                             // IL_00c6  Usec=1 Bear=2 Savage=4
///     case Usec:   result |= IsPlayerBadUsec(player);      // ★ 单人必 false（叛变计数器）
///     case Bear:   result |= badge;                        // ★ 无额外检查
///     case Savage: result |= player.Loyalty.HostileScavs;
///   }
///
///   // IL_0145 最后一道清空（* 这是最阴的一个坑 *）：
///   if (!player.IsAI && _initialBotMindSettings.CHECK_MARK_OF_UNKNOWS
///       && player.Side ∈ {Usec,Bear} && GroupMarkOfUnknown.HasMarkOfUnknown(player))
///       result = false;                                // ★ 戴"未知标记"徽章 → 强制非敌人
///
///   return result;                                     // IL_0177
///
/// 收尾规则（位于以上全部逻辑之后）：
///   · 目标与发起方**同一个小队**(GroupId 相同且非空) → 不动（自己人不打）
///   · 其余一切（玩家 USEC/BEAR/SCAV、其他 AI） → 强制 true
/// 覆盖 USEC 的叛变计数器、BEAR 的空检查、SCAV 的忠诚度、以及"未知标记"徽章清空。
/// </summary>
internal sealed class BotsGroupIsPlayerEnemyPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.IsPlayerEnemy));

    [PatchPostfix]
    private static void Postfix(BotsGroup __instance, IPlayer player, ref bool __result)
    {
        try
        {
            if (!HostilityGuard.Enabled || __result) return;
            if (player == null) return;

            // ① 发起方（组里那只"首领 bot"）必须是活动敌种
            var owner = HostilityGuard.InitialBotField?.GetValue(__instance);
            if (owner == null) return;
            var role = HostilityGuard.RoleOf(owner);
            if (!HostilityGuard.HostileRoles.Contains(role)) return;

            // ② 同小队自己人放过（用该组的成员表比对）
            var mem = HostilityGuard.MembersProp(__instance.GetType())?.GetValue(__instance);
            if (HostilityGuard.SameSquad(mem, player)) return;

            // ③ 其余一律敌人（玩家 + 其他 AI）
            bool isAi = player.IsAI;
            if (HostilityGuard.DiagOn) HostilityGuard.Diag($"[HOST] ★强制敌视: role={role} 目标={(isAi ? "AI:" + HostilityGuard.RoleOfPlayer(player) : "玩家")}" +
                                $" Side={player.Side} profile={player.ProfileId} SAIN={HostilityGuard.IsSainActive(owner)}");
            __result = true;
        }
        catch { }
    }
}

/// <summary>
/// 【让活动敌种主动敌视其他 AI】把 BotSettings.GetEnemyBotTypes() 的返回补成"除自己以外全部角色"。
/// 这样它们会主动把 PMC/SCAV/邪教徒/其他首领都列为敌人（而不只是被动还击）。
/// </summary>
internal sealed class GetEnemyBotTypesPatch : ModulePatch
{
    // ══════════ ★★★ v1.0.1【性能】WildSpawnType 全量枚举缓存 ══════════
    //   原来 `GetEnemyBotTypesPatch` 每次调用都 `Enum.GetValues`（new 79 元素数组）
    //   + 循环里 79 次 `t.ToString()`（79 个新字符串）⇒ 3~5 KB/调用。枚举集合与名字进程内恒定。
    private static readonly WildSpawnType[] _allRoles =
        (WildSpawnType[])Enum.GetValues(typeof(WildSpawnType));
    private static readonly string[] _allRoleNames =
        _allRoles.Select(t => t.ToString()).ToArray();

    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(BotSettings), "GetEnemyBotTypes");

    [PatchPostfix]
    private static void Postfix(BotSettings __instance, ref List<WildSpawnType> __result)
    {
        try
        {
            if (!HostilityGuard.Enabled) return;
            var role = HostilityGuard.RoleOfSettings(__instance);
            if (!HostilityGuard.HostileRoles.Contains(role)) return;

            if (__result == null) __result = new List<WildSpawnType>();
            var self = (WildSpawnType)Enum.Parse(typeof(WildSpawnType), role, true);

            int add = 0;
            // ★ v1.0.1【性能】用缓存的全量枚举 + 名字表（省掉每次 1 个 79 元素数组 + 79 个 ToString 新串）
            for (int ri = 0; ri < _allRoles.Length; ri++)
            {
                var t = _allRoles[ri];
                if (t == self) continue;                  // 不把自己当敌人
                if (_allRoleNames[ri] == "marksman") continue; // 保留原版特例
                if (!__result.Contains(t)) { __result.Add(t); add++; }
            }
            if (add > 0) if (HostilityGuard.DiagOn) HostilityGuard.Diag($"[HOST] ★GetEnemyBotTypes 扩到全角色: role={role} 新增 {add} 项，共 {__result.Count}");
        }
        catch { }
    }
}

/// <summary>
/// 【★★★ 真正的时机：BotsGroup.AddMember ★★★】
///
/// 前面所有方案的失败原因统一是**时机**：
///   BotZoneGroups.AddPlayer(player) 只在"玩家进入该 zone"时调用一次，
///   而走私者的组是**玩家出生后 30 秒**才创建 —— 那个组从来没执行过 AddPlayer，
///   所以玩家永远没被列成它的敌人（日志证据：AddNeutral 从未被调用、IsPlayerEnemy 从未以玩家为参数）。
///
/// 而 BotZoneGroups.AddBot 在 bot 加入时就会调 BotsGroup.AddMember(bot, ...)：
///   IL_002b: ldloc.2 ; ldarg.1 ; ldc.i4.0 ; callvirt BotsGroup::AddMember(BotOwner, bool)
///
/// 所以挂在 AddMember 之后：只要这只 bot 是"活动敌种"，就把玩家设成该组的敌人。
/// 这样无论队伍什么时候创建都能覆盖。
/// </summary>
internal sealed class AddMemberForceEnemyPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.GetDeclaredMethods(typeof(BotsGroup))
                   .FirstOrDefault(m => m.Name == "AddMember" && m.GetParameters().Length == 2);

    [PatchPostfix]
    private static void Postfix(BotsGroup __instance, BotOwner __0)
    {
        try
        {
            if (!HostilityGuard.Enabled || __instance == null || __0 == null) return;
            if (!HostilityGuard.IsInRaidSafe()) return;      // ★ 部署界面不许动手

            var role = HostilityGuard.RoleOf(__0);
            if (string.IsNullOrEmpty(role) || !HostilityGuard.HostileRoles.Contains(role)) return;   // 只处理活动敌种

            var player = HostilityGuard.GetMainPlayer();
            if (player == null) return;

            // 同队放过（玩家不会在 bot 的成员表里，但留个保险）
            if (HostilityGuard.SameSquad(HostilityGuard.MembersProp(__instance.GetType())?.GetValue(__instance), player)) return;

            var m = __instance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .FirstOrDefault(x => x.Name == "AddEnemy" && x.GetParameters().Length == 2);
            if (m == null) { if (HostilityGuard.DiagOn) HostilityGuard.Diag("[HOST] AddMember 后找不到 AddEnemy"); return; }

            object cause = Enum.GetValues(m.GetParameters()[1].ParameterType).GetValue(0);
            var ok = m.Invoke(__instance, new object[] { player, cause });
            if (HostilityGuard.DiagOn) HostilityGuard.Diag($"[HOST] ★AddMember 后加敌: role={role} 玩家={player.ProfileId} Side={player.Side} AddEnemy→{ok}");
        }
        catch (Exception ex) { if (HostilityGuard.DiagOn) HostilityGuard.Diag("[HOST] AddMemberForceEnemy 异常: " + ex.GetType().Name + ": " + ex.Message); }
    }
}

/// <summary>
/// 【★★★★★ 最终方案：BotOwner.FixedUpdate 每帧强制 ★★★★★】
///
/// 前面试遍了所有"事件式"挂载点（AddPlayer / AddMember / AddNeutral / IsPlayerEnemy / Init），
/// 全都没打印 → 这些钩子在**走私者这条生成路径上根本没被经过**（它们是 ABPS 额外追加的条目，
/// 走的是 boss 波次通道，不经过 BotZoneGroups 的常规注册流程）。
///
/// 所以改用最直接、最不可能失败的方式：BotOwner 每帧都会跑 FixedUpdate，
/// 在这里对"活动敌种"的 bot 直接检查并强制写入敌人关系。
///   · 只处理 HostileRoles 名单内的角色（中立单位如 BTR 完全不受影响）
///   · 同队成员放过
///   · 一次性把本地玩家 AddEnemy 到该 bot 所在组
/// </summary>
internal sealed class BotOwnerFixedUpdatePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(BotOwner), "FixedUpdate");

    // 已处理的 bot，避免每帧重复调用
    private static readonly HashSet<string> Done = new(StringComparer.OrdinalIgnoreCase);
    private static float _lastLog;

    // ★ v1.0.1【性能 J+K+L】本 patch 热路径的成员解析缓存（每帧每 bot 原本 2 次反射 + 1 次 GetMethods 全量扫描）
    private static PropertyInfo _piMembers;
    private static MethodInfo _miAddEnemy;
    private static object _causeValue;
    private static Type _groupType;
    private static readonly object[] _addEnemyArgs = new object[2];   // ★ v1.0.1 复用参数数组（每帧每 bot 都调）

    [PatchPostfix]
    private static void Postfix(BotOwner __instance)
    {
        try
        {
            if (!HostilityGuard.Enabled || __instance == null) return;

            // ★★★ v1.0.1【性能 C · 顺序重排】把 `Done.Contains` 提到 `RoleOf` 之前
            //    等价性（已核）：`Done` 只在「同队放过」与「成功加敌」两处 Add ⇒ 命中即原来也是 return（只是晚 2 次反射）；
            //    未被 Add 的 bot（group/player/m 为空的早退）原来每帧重试 ⇒ 顺序改后同样重试 ⇒ 行为不变。
            //    `BotIdOf` 是纯读（GetValue/ToString）⇒ 无副作用。
            var id = HostilityGuard.BotIdOf(__instance);
            if (string.IsNullOrEmpty(id)) return;
            if (Done.Contains(id)) return;

            var role = HostilityGuard.RoleOf(__instance);
            if (string.IsNullOrEmpty(role) || !HostilityGuard.HostileRoles.Contains(role)) return;   // ★ 只处理活动敌种

            var group = __instance.BotsGroup;
            var player = HostilityGuard.GetMainPlayer();
            if (group == null || player == null) return;

            // ★ v1.0.1【性能 J】`Members` 的 PropertyInfo 缓存
            var gt = group.GetType();
            if (gt != _groupType)
            {
                _piMembers = gt.GetProperty("Members", BindingFlags.Public | BindingFlags.Instance);
                _miAddEnemy = gt.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                .FirstOrDefault(x => x.Name == "AddEnemy" && x.GetParameters().Length == 2);
                if (_miAddEnemy != null)
                    _causeValue = Enum.GetValues(_miAddEnemy.GetParameters()[1].ParameterType).GetValue(0);
                _groupType = gt;
            }

            // 同队放过
            if (HostilityGuard.SameSquad(_piMembers?.GetValue(group), player))
            { Done.Add(id); return; }

            // ★ v1.0.1【性能 K+L】`AddEnemy` 的 MethodInfo 与 cause 值已在上面的 gt 分支里缓存
            var m = _miAddEnemy;
            if (m == null) return;
            object cause = _causeValue;
            _addEnemyArgs[0] = player; _addEnemyArgs[1] = cause; var ok = m.Invoke(group, _addEnemyArgs);   // ★ 复用数组
            Done.Add(id);

            if (UnityEngine.Time.time - _lastLog > 0.5f)
            {
                _lastLog = UnityEngine.Time.time;
                if (HostilityGuard.DiagOn) HostilityGuard.Diag($"[HOST] ★★BotOwner.FixedUpdate 强制加敌: role={role} bot={id} 玩家={player.ProfileId} Side={player.Side} AddEnemy→{ok}");
            }
        }
        catch (Exception ex)
        {
            if (UnityEngine.Time.time - _lastLog > 0.5f)
            {
                _lastLog = UnityEngine.Time.time;
                if (HostilityGuard.DiagOn) HostilityGuard.Diag("[HOST] BotOwnerFixedUpdate 异常: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}

/// <summary>
/// 【优雅方案：劫持 AddNeutral】
/// BotZoneGroups.AddPlayer 里若判定非敌人就会调 AddNeutral(player)，
/// 这里就地改成 AddEnemy（仅限活动敌种的组）。
/// </summary>
internal sealed class AddNeutralToEnemyPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(BotsGroup), "AddNeutral");

    [PatchPrefix]
    private static bool Prefix(BotsGroup __instance, IPlayer player)
    {
        try
        {
            if (!HostilityGuard.Enabled || player == null) return true;
            if (player.IsAI) return true;                       // 只针对人类玩家

            // 该组是不是活动敌种的组？
            var members = HostilityGuard.MembersProp(__instance.GetType())?.GetValue(__instance) as System.Collections.IEnumerable;
            string role = "";
            bool hostile = false;
            if (members != null)
            {
                foreach (var m in members)
                {
                    var r = HostilityGuard.RoleOf(m);
                    if (string.IsNullOrEmpty(r)) continue;
                    if (HostilityGuard.HostileRoles.Contains(r)) { hostile = true; role = r; break; }
                }
            }
            if (!hostile)
            {
                var t = __instance.GetType().GetProperty("InitialBotType", BindingFlags.Public | BindingFlags.Instance)?.GetValue(__instance)?.ToString();
                if (t != null && HostilityGuard.HostileRoles.Contains(t)) { hostile = true; role = t; }
            }
            if (!hostile) return true;                          // 不是活动敌种 → 原逻辑

            // 就地改成"加敌人"
            var m2 = __instance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                      .FirstOrDefault(x => x.Name == "AddEnemy" && x.GetParameters().Length == 2);
            if (m2 == null)
            {
                if (HostilityGuard.DiagOn) HostilityGuard.Diag($"[HOST] AddNeutral 拦截: role={role} 但找不到 AddEnemy 方法");
                return true;
            }
            object cause = Enum.GetValues(m2.GetParameters()[1].ParameterType).GetValue(0);
            var ok = m2.Invoke(__instance, new object[] { player, cause });
            if (HostilityGuard.DiagOn) HostilityGuard.Diag($"[HOST] ★AddNeutral 拦截成功: role={role} 玩家={player.ProfileId} Side={player.Side} AddEnemy→{ok}");
            return false;                                        // 跳过原 AddNeutral
        }
        catch (Exception ex) { if (HostilityGuard.DiagOn) HostilityGuard.Diag("[HOST] AddNeutral 拦截异常: " + ex.GetType().Name + ": " + ex.Message); return true; }
    }
}

/// <summary>
/// 辅助：BotsController.Init 之后确认客户端实际使用的敌对数就位（诊断 + 兜底注入）。
/// </summary>
internal sealed class BotsControllerInitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(BotsController), "Init");

    [PatchPostfix]
    private static void Postfix(BotsController __instance)
    {
        try
        {
            var blm = __instance?.BotLocationModifier;
            if (blm == null) { if (HostilityGuard.DiagOn) HostilityGuard.Diag("[HOST] Init 后 BotLocationModifier 仍为 null"); return; }

            var list = blm.AdditionalHostilitySettings;
            if (HostilityGuard.DiagOn) HostilityGuard.Diag($"[HOST] BotsController.Init 完成：敌对数条目={list?.Length ?? -1}");

            HostilityGuard.EnsureHostility(blm, "Init");
        }
        catch (Exception ex) { if (HostilityGuard.DiagOn) HostilityGuard.Diag("[HOST] BotsControllerInitPatch 异常: " + ex.GetType().Name); }
    }
}
