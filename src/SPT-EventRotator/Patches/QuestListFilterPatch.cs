using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;

namespace EventRotator.Patches;

/// <summary>
/// ★★★ v2.6.15【再补一层】过滤掉「已接条目 + 被门控 + 所属活动没在跑」的任务，让它们**不出现在任务列表里**。
///
/// ─────────────────────────────────────────────────────────────────────────────────────────
/// 补的是什么（用户点名的"看得到点不了"）：
///   `QuestHelper.GetClientQuests`（QuestHelper.cs:870-988）的这条路径
///   <code>
///   QuestStatus questStatus = pmcProfile.Quests.FirstOrDefault(x => x.QId == quest.Id);
///   if (questStatus != null) { quest.SptStatus = questStatus.Status; list.Add(quest); }   // :882-886 ← 无条件
///   else { … :889 才走 ShowEventQuestToPlayer 等门控 … }
///   </code>
///   ⇒ **profile 里已有条目的任务完全绕过门控**：一条"已经接过"的活动任务，在活动关闭期间**照样显示在任务栏**
///     （上一轮的 `QuestAcceptGatePatch` 只能做到"点了才拒"，体验是"看得到点不了"）。
///   本补丁在**返回列表那一刻**把这批任务摘掉。**只摘"已有条目"的那批** —— 没有条目的那批本来就被
///   `ShowEventQuestToPlayer`（:280-295）挡在 `:889` 那行了，重复处理只会增加风险。
///
/// ─────────────────────────────────────────────────────────────────────────────────────────
/// ② "返回列表能不能就地改" —— 结论：**能**（就地改是安全的），但我仍然**不改原实例**（见下）。
///   依据（两条，都在 `QuestHelper.cs`）：
///     1. `:872` `List<Quest> list = new List<Quest>();` —— 方法**每次调用都新建**一个 list；
///     2. `:987` `return UpdateQuestsForGameEdition(cloner.Clone(list), pmcProfile.Info.GameVersion);`
///        —— 返回给调用方的是 **`cloner.Clone(list)`**（本次调用现克隆出来的新实例），
///        而 `UpdateQuestsForGameEdition`（:990+）只是就地改克隆里每个 quest 的 `Rewards` 后**原样返回**。
///     ⇒ 返回的 List 实例**只被本次调用的调用方持有**，没有任何别处持有它 ⇒ 就地 `RemoveAll` 不会影响别人。
///     （附带：连里面的 `Quest` 对象都是克隆体，所以 SPT 那句 `quest.SptStatus = …` 也不会污染 DB 对象。）
///   ⚠ 尽管如此，本补丁**仍不改原实例**：改为把"保留的那批"装进一个**新 list** 再赋给 `__result`
///     （`ref object __result`）。理由：万一将来 SPT 把返回改成某个缓存/共享实例，就地改会变成"跨请求污染"，
///     而新建 list 的开销只在**真的过滤掉东西时**才发生 —— 用一点分配换掉一整类未来风险。
///
/// ─────────────────────────────────────────────────────────────────────────────────────────
/// 降级路径（"绝不能因为补丁失败就把任务列表弄空或弄坏"）：
///   · 找不到 `QuestHelper` / `GetClientQuests` / 签名不符 ⇒ **不挂补丁** + warning（列表原样）；
///   · 挂补丁抛异常 ⇒ warning（列表原样）；
///   · `EventRotatorServer.QuestGateReady == false` ⇒ 直接 return（列表原样）；
///   · `__result` 不是 `IList` / 空 / 不是泛型 List（造不出同型新 list）⇒ return（列表原样）；
///   · sessionId 认不出来 / 读不到该 session 的 profile ⇒ return（列表原样）；
///   · 逐个任务判断时任何异常 ⇒ 整体 return（**一个都不摘**）+ 只告警一次。
///   ⇒ 任何一条降级都不动列表 ⇒ 玩家看到的任务列表与"没装这个补丁"完全一致。
///
/// ─────────────────────────────────────────────────────────────────────────────────────────
/// ⚠ 不拦可重复任务：`/client/quest/list` 这条路只列 `templateTable.Quests`（DB 静态任务），
///   可重复任务在 **profile** 里（`RepeatableQuestController`，QuestController.cs:32-34 的调用方
///   `QuestCallbacks.ListQuests` → `QuestHelper.GetClientQuests`），而且它们的 id 永远不在门控集里
///   ⇒ 结构上就不可能被本补丁碰到（另见 `QuestCallbacks.cs:36-39`：接受可重复任务走的是另一条路）。
/// </summary>
internal static class QuestListFilterPatch
{
    private static ISptLogger<EventRotatorServer> _log;

    /// <summary>SPT 单例，由 Plugin 注入（用来判断"profile 里是否已有该任务条目"）。</summary>
    internal static SaveServer SaveServer;

    /// <summary>补丁是否真的挂上去了。</summary>
    internal static bool Installed;

    /// <summary>
    /// ★★★ v1.0.1【prepatch 兼容·真因】SPT 注入的**运行时那个** QuestHelper 实例。
    ///   <para>为什么必须用它：SPT 的 prepatch（`user/patchers/&lt;mod&gt;`）会额外生成
    ///   `SPTarkov.Server.Core.Patched.dll`，它与原始 `SPTarkov.Server.Core.dll`
    ///   **程序集名与版本完全相同**。进程里于是可能同时存在两份同名同版本程序集，
    ///   而 `AccessTools.TypeByName` 只按"第一个含该类型的程序集"返回 ——
    ///   **命中的那一份不一定是真正在执行的那一份**。</para>
    ///   <para>后果：补丁确实挂上了（Harmony 不报错、日志也打"✔ 已挂"），但挂的是**另一份**类型上的
    ///   同名方法 ⇒ 永远不触发、无异常、无日志。这正是「任务列表过滤不生效」的根因
    ///   （`[EventRotator][诊断] Postfix 第 N 次进入` 一条都没出现）。</para>
    ///   <para>正解：用 DI 实例的 `GetType()` 取**真正在跑的**类型，再以它所在程序集为准按全名解析其它 SPT 类型。</para>
    /// </summary>
    internal static object QuestHelperInstance;

    private static bool _warnedNoProfile, _warnedException;
    private static string _lastLogKey;

    public static void Enable(Harmony harmony, ISptLogger<EventRotatorServer> log)
    {
        _log = log;
        try
        {
            // ★ v1.0.1【取运行时类型】优先 DI 实例 → 它的 GetType() 就是**正在被调用**的那个类型
            var liveType = QuestHelperInstance?.GetType();
            var t = liveType
                    ?? RuntimeTypes.Find("SPTarkov.Server.Core.Helpers.Quest.QuestHelper")
                    ?? FindType("QuestHelper");
            if (t == null)
            {
                _log?.Warning("[EventRotator][任务列表过滤] ⚠ 找不到类型 QuestHelper ⇒ **不挂补丁，任务列表原样返回**");
                return;
            }
            var m = AccessTools.Method(t, "GetClientQuests");
            if (m == null)
            {
                _log?.Warning("[EventRotator][任务列表过滤] ⚠ 找不到 QuestHelper.GetClientQuests ⇒ **不挂补丁，任务列表原样返回**");
                return;
            }
            // 签名自检：1 个 MongoId 参数 + 返回泛型 List<>
            // ★ v1.0.1【prepatch 兼容】改 TypeCompat.Same（全名比较）——
            //   装带 user/patchers 的 mod 后 SPT 会加载 SPTarkov.Server.Core.Patched.dll，
            //   运行时参数类型与 typeof(MongoId) **不是同一个 Type 实例**，原来的 == 恒 false。
            var ps = m.GetParameters();
            bool sigOk = ps.Length == 1 && TypeCompat.Same(ps[0].ParameterType, typeof(MongoId))
                         && m.ReturnType.IsGenericType
                         && TypeCompat.Same(m.ReturnType.GetGenericTypeDefinition(), typeof(List<>));
            if (!sigOk)
            {
                _log?.Warning($"[EventRotator][任务列表过滤] ⚠ GetClientQuests 签名与预期不符"
                            + $"（{string.Join(",", ps.Select(p => p.ParameterType.Name))} → {m.ReturnType.Name}）"
                            + " ⇒ **不挂补丁，任务列表原样返回**");
                return;
            }

            harmony.Patch(m, null, new HarmonyMethod(typeof(QuestListFilterPatch), nameof(Postfix)) { priority = 300 },
                          null, null, null);
            Installed = true;
            // ★ 自检：SPT prepatch 会让进程里同时存在两份**同名同版本**的 `SPTarkov.Server.Core`
            //   （磁盘上那份 + 内存里那份 Patched）。一旦挂到磁盘上那份，补丁会**静默不触发**
            //   （无异常、无日志、任务列表照旧）—— 这里把"挂的那份是不是运行时那份"直接打进日志。
            var sameAsMongo = ReferenceEquals(m.DeclaringType?.Assembly, typeof(MongoId).Assembly);
            var mountMsg = "[EventRotator][任务列表过滤] ✔ 已挂 QuestHelper.GetClientQuests (postfix p300)："
                     + "「已接条目 + 被门控 + 所属活动没在跑」的任务**不再出现在任务列表**（列表实例不就地改，返回新 list）；"
                     + "任何异常/降级 ⇒ 列表原样"
                     + $"（目标类型来源={(liveType != null ? "DI实例 GetType()" : "TypeByName 回退")}，"
                     + $"程序集={m.DeclaringType?.Assembly.GetName().Name}，与MongoId同程序集={sameAsMongo}）";
            if (sameAsMongo) _log?.Info(mountMsg);
            else _log?.Warning(mountMsg + " ⚠ 与 MongoId 不同程序集 ⇒ 极可能挂到了非运行时那份，补丁可能不触发");

            // ★★ 第二道：`QuestController.GetClientQuests` —— `QuestHelper.GetClientQuests` 的直接调用方。
            //   两处都挂 ⇒ 任一层生效即可；postfix 幂等（已过滤过的列表不会再被摘第二次），
            //   同时生效也不会重复摘 / 重复打日志。
            try
            {
                var qc = t.Assembly.GetType("SPTarkov.Server.Core.Controllers.QuestController")
                         ?? RuntimeTypes.Find("SPTarkov.Server.Core.Controllers.QuestController")
                         ?? FindType("QuestController");
                var qcm = qc != null ? AccessTools.Method(qc, "GetClientQuests") : null;
                if (qcm != null && qcm.GetParameters().Length == 1 && qcm.DeclaringType != m.DeclaringType)
                {
                    harmony.Patch(qcm, null, new HarmonyMethod(typeof(QuestListFilterPatch), nameof(Postfix)) { priority = 300 },
                                  null, null, null);
                    _log?.Info("[EventRotator][任务列表过滤] ✔ 第二道已挂 QuestController.GetClientQuests (postfix p300)"
                             + $"（程序集={qcm.DeclaringType?.Assembly.GetName().Name}）");
                }
            }
            catch (Exception ex2)
            {
                _log?.Warning("[EventRotator][任务列表过滤] ⚠ 第二道（QuestController）挂载失败，不影响第一道：" + ex2.Message);
            }
        }
        catch (Exception ex)
        {
            _log?.Warning($"[EventRotator][任务列表过滤] ⚠ 挂补丁失败 ⇒ **任务列表原样返回**：{ex.Message}");
        }
    }

    /// <summary>
    /// Postfix：`GetClientQuests(MongoId sessionId) → List&lt;Quest&gt;`。
    /// 参数用 `object __0` / `ref object __result`（与本 mod `QuestAcceptGatePatch` 同风格，Harness 里已实证可写回）。
    /// </summary>
    private static void Postfix(object __0, ref object __result)
    {
        try
        {
            if (!Installed) return;
            // ① 门控未就绪 ⇒ 一律不改列表（"绝不能把列表弄坏"的第一道闸）
            if (!EventRotatorServer.QuestGateReady) return;
            if (!TypeCompat.IsMongoId(__0)) return;
            var sid = __0;                          // ★ object：运行时可能是 Patched 版的 MongoId
            if (__result is not IList list || list.Count == 0) return;
            var lt = list.GetType();
            if (!lt.IsGenericType || lt.GetGenericTypeDefinition() != typeof(List<>)) return;   // 造不出同型新 list ⇒ 不改

            // ③ "profile 里已有条目"这一条只有本补丁能判（GetClientQuests 内部那个分支走的就是同一份数据）
            var owned = ProfileQuestIds(sid);
            if (owned == null) return;              // 读不到 profile ⇒ 不改列表（降级）
            if (owned.Count == 0) return;

            var keep = new List<object>(list.Count);
            var removed = new List<string>();
            foreach (var o in list)
            {
                var id = Prop(o, "Id")?.ToString();
                // ★ 三条同时满足才摘：① 在门控集里 ② 所属活动没在跑（= IsQuestBlockedByGate） ③ profile 已有条目
                if (!string.IsNullOrEmpty(id) && owned.Contains(id)
                    && EventRotatorServer.IsQuestBlockedByGate(id, out _))
                {
                    removed.Add(id);
                    continue;
                }
                keep.Add(o);
            }
            if (removed.Count == 0) return;         // 没什么可摘 ⇒ **连 list 都不重建**

            // ★ 不改原实例：造一个同型新 list（万一将来 SPT 换成共享/缓存实例也不会被我们污染）
            var fresh = (IList)Activator.CreateInstance(lt);
            if (fresh == null) return;
            foreach (var k in keep) fresh.Add(k);
            __result = fresh;

            LogOnce(sid, removed);
        }
        catch (Exception ex)
        {
            // ★ 关键：异常时**什么都不做** ⇒ __result 保持原样（列表完整）
            if (!_warnedException)
            {
                _warnedException = true;
                _log?.Warning($"[EventRotator][任务列表过滤] ⚠ postfix 异常 ⇒ 本次**任务列表原样返回**"
                            + $"（只会出现一次这条警告）：{ex.Message}");
            }
        }
    }

    /// <summary>该 session 的 PMC profile 里已有的任务 id 集合（读不到 ⇒ null = 降级，不改列表）。</summary>
    private static HashSet<string> ProfileQuestIds(object sid)
    {
        try
        {
            if (SaveServer == null)
            {
                if (!_warnedNoProfile)
                {
                    _warnedNoProfile = true;
                    _log?.Warning("[EventRotator][任务列表过滤] ⚠ 没拿到 SaveServer（DI 未注入）⇒ 无法判断\"profile 已有条目\"，"
                                + "**任务列表原样返回**（只会出现一次这条警告）");
                }
                return null;
            }
            // ★ v1.0.1【prepatch 兼容】改运行时反射：SaveServer.GetProfile 的形参是**运行时那个** MongoId，
            //   我们编译时的 MongoId 传进去会 ArgumentException。
            var prof = TypeCompat.InvokeWithSession(SaveServer, "GetProfile", sid);
            // ★ v1.0.1【prepatch 兼容】prof 现在是 object，用本文件既有的 Prop() 反射读
            var cd = Prop(prof, "CharacterData");
            var pmc = Prop(cd, "PmcData");
            var qs = Prop(pmc, "Quests") as System.Collections.IEnumerable;
            if (qs == null) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var q in qs)
            {
                var id = Prop(q, "QId")?.ToString();   // ★ prepatch 兼容：qs 现在是 IEnumerable（元素是 object）
                if (!string.IsNullOrEmpty(id)) set.Add(id);
            }
            return set;
        }
        catch { return null; }
    }

    /// <summary>限流日志：同一 session 同一结果只打一次；`logLevel=verbose` 时逐个列 id。</summary>
    private static void LogOnce(object sid, List<string> removed)
    {
        try
        {
            var key = sid + "|" + removed.Count + "|" + string.Join(",", removed.OrderBy(x => x, StringComparer.Ordinal));
            if (key == _lastLogKey) return;                 // 同一结果不重复刷屏
            _lastLogKey = key;
            _log?.Info($"[EventRotator][任务列表过滤] 本次移除了 {removed.Count} 个"
                     + "「已接条目 + 被门控 + 所属活动没在跑」的任务"
                     + (EventEngine.VerboseLogging
                        ? "：" + string.Join(",", removed)
                        : " —— 要逐个看 id 请把 config.json 的 logLevel 改成 verbose"));
        }
        catch { }
    }

    private static Type FindType(string simpleName)
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(x => x.Name == simpleName);
        }
        catch { return null; }
    }

    /// <summary>读属性（本 mod 通用反射读法）。</summary>
    // ★ v1.0.1【性能·第五轮】两步属性查找（精确 → 不敏感）改成缓存。
    //   本 patch 可能在"每个任务/每次 UI 刷新"上被调 ⇒ 原来每次 1~2 次裸反射。
    //   语义逐字保留：先查精确、返回 null 再查不敏感；`GetValue` 异常照旧向上抛。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string, bool), PropertyInfo> _piCache = new();

    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        var exact = _piCache.GetOrAdd((t, name, false),
            static k => k.Item1.GetProperty(k.Item2, BindingFlags.Public | BindingFlags.Instance));
        var v = exact?.GetValue(o);
        if (v != null) return v;
        var loose = _piCache.GetOrAdd((t, name, true),
            static k => k.Item1.GetProperty(k.Item2, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));
        return loose?.GetValue(o);
    }
}
