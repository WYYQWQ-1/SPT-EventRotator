using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;

namespace EventRotator.Patches;

/// <summary>
/// ★★★ v2.6.14【补洞】`QuestController.AcceptQuest` 的服务端硬门控（Harmony **prefix**）。
///
/// ─────────────────────────────────────────────────────────────────────────────────────────
/// 补的是什么洞（用户追问："要是我活动任务已经接了呢？"）—— 全部有反编译 file:line：
///
///   ① `QuestHelper.cs:882-886`（`GetClientQuests`）：
///      <code>
///      var questStatus = pmcProfile.Quests.FirstOrDefault(x => x.QId == quest.Id);
///      if (questStatus != null) { quest.SptStatus = questStatus.Status; list.Add(quest); }   // ← 无条件加入
///      else { … :889 才走 ShowEventQuestToPlayer 等一堆门控判定 … }
///      </code>
///      ⇒ **profile 里已有条目的任务绕过门控**：`QuestHelper.ShowEventQuestToPlayer`（:280-295）那一层
///        只拦"从没接过"的任务。
///   ② `QuestController.AcceptQuest`（QuestController.cs:47-77）**全程没有任何门控检查**：
///      <code>
///      if (questStatus != null) { questHelper.ResetQuestState(pmcData, QuestStatusEnum.Started, acceptedQuest.QuestId); … }
///      else                     { var q = questHelper.GetQuestReadyForProfile(pmcData, QuestStatusEnum.Started, acceptedQuest); pmcData.Quests.Add(q); }
///      </code>
///      ⇒ 服务端"客户端要什么就给什么"；**两个分支都必须拦**（本 prefix 在方法之前返回 false ⇒ 两个分支一起拦）。
///   ③ 全库 `IsQuestRelatedToEvent` 只被 `QuestHelper.ShowEventQuestToPlayer:284/288/292` 引用
///      ⇒ 门控**只存在于「列任务」这一条路径**。
///   ④ 本 mod 的轮换重置（`EventRotatorServer.ResetQuestProgress`）会把已接任务翻回
///      `QuestStatusEnum.AvailableForStart` ⇒ 客户端显示"接受"按钮 ⇒ **活动关着也能再接一次**。
///
/// ⇒ 本 prefix 在**接任务入口**再硬拦一次：请求的任务若"**被门控 且 拥有它的活动当前都没在跑**" ⇒
///   **干净地拒绝**（返回标准失败响应），活动在跑则放行。
///
/// ─────────────────────────────────────────────────────────────────────────────────────────
/// 拒绝响应的构造依据（**逐字照抄 SPT 现成写法**，不抛异常）：
///   · `QuestController.ShowRepeatableQuestInvalidConditionError`（QuestController.cs:222-228）：
///     <code>
///     string text = serverLocalisationService.GetText("repeatable-quest_handover_failed_condition_invalid", …);
///     logger.Error(text);
///     return httpResponseUtil.AppendErrorToOutput(output, text);
///     </code>
///     另一个同型写法：`QuestController.ShowQuestItemHandoverMatchError`（:237-250）。
///   · 其中的 `output` 来自 `EventOutputHolder.GetOutput(sessionId)`（EventOutputHolder.cs:28-37）；
///   · `HttpResponseUtil.AppendErrorToOutput(output, message, errorCode)`（HttpResponseUtil.cs:127-160）：
///     往 `output.Warnings` 追加一条 `Warning { Index, ErrorMessage = message, Code = errorCode }`
///     （`errorCode` 用默认的 `BackendErrorCodes.None`，与 SPT 自己那两处调用一致 ——
///      **不传特殊错误码**：某些码会让客户端弹专用对话框/断线）。
///   ⇒ 客户端拿到的是**正常的 200 + 标准 warning 通道**，UI 表现是红字错误提示，不是 500。
///
/// ⚠ 错误文案用**纯 ASCII 英文**：这段文字会**显示在游戏客户端 UI 里**，而 EFT 客户端字体对中文
///   渲染成方块（服务端日志里的中文提示不受影响，那是我们自己的 logger）。所以这里不能写中文。
///
/// ─────────────────────────────────────────────────────────────────────────────────────────
/// 降级路径（"绝不能因为补丁失败就让所有任务都接不了"）：
///   · 找不到 `QuestController` / `AcceptQuest` / 签名不符 ⇒ **不挂补丁** + warning ⇒ 一律放行；
///   · 挂补丁抛异常 ⇒ warning ⇒ 一律放行；
///   · `EventRotatorServer.QuestGateReady == false`（门控还没跑过第一轮）⇒ 放行；
///   · 请求参数认不出来（不是 AcceptQuest 请求对象 / 读不到 QuestId）⇒ 放行；
///   · 构造失败响应时 `OutputHolder`/`HttpUtil` 为空或 session 认不出来 ⇒ warning（一次）+ 放行
///     —— **宁可漏拦一个任务，也不给客户端返回 null 响应**；
///   · prefix 内部任何异常 ⇒ warning（一次）+ 放行。
///   ⇒ 判据本身来自 `EventRotatorServer.IsQuestBlockedByGate`（`QuestGateReady` + 反查表 + 在跑集合），
///     而那三样只在 `ApplyQuestGate` **成功算完**之后才会被刷新。
/// </summary>
internal static class QuestAcceptGatePatch
{
    private static ISptLogger<EventRotatorServer> _log;

    /// <summary>SPT 单例，由 Plugin 注入（构造失败响应用）。</summary>
    internal static EventOutputHolder OutputHolder;
    /// <summary>SPT 单例，由 Plugin 注入（往响应里追加 warning 用）。</summary>
    internal static HttpResponseUtil HttpUtil;

    /// <summary>补丁是否**真的挂上去了**（挂失败 ⇒ prefix 自己也不会被调用；这里再兜一层）。</summary>
    internal static bool Installed;

    private static bool _warnedNoResponse, _warnedException;

    /// <summary>挂 prefix（失败只 warning，不抛）。</summary>
    public static void Enable(Harmony harmony, ISptLogger<EventRotatorServer> log)
    {
        _log = log;
        try
        {
            var t = RuntimeTypes.Find("SPTarkov.Server.Core.Controllers.QuestController")
                    ?? FindType("QuestController");
            if (t == null)
            {
                _log?.Warning("[EventRotator][接任务门控] ⚠ 找不到类型 QuestController ⇒ **不挂补丁，接任务一律放行**"
                            + "（活动门控只剩「列任务」那一层）");
                return;
            }
            var m = AccessTools.Method(t, "AcceptQuest");
            if (m == null)
            {
                _log?.Warning("[EventRotator][接任务门控] ⚠ 找不到 QuestController.AcceptQuest ⇒ **不挂补丁，接任务一律放行**");
                return;
            }
            // 签名自检（防 SPT 改签名后我们挂在错的重载上）：必须是 3 参，且其中一个参数类型名含 AcceptQuest
            var ps = m.GetParameters();
            bool sigOk = ps.Length == 3
                         && ps.Any(p => p.ParameterType.Name.IndexOf("AcceptQuest", StringComparison.OrdinalIgnoreCase) >= 0)
                         && ps.Any(p => TypeCompat.Same(p.ParameterType, typeof(MongoId)));   // ★ prepatch 兼容
            if (!sigOk)
            {
                _log?.Warning($"[EventRotator][接任务门控] ⚠ AcceptQuest 签名与预期不符"
                            + $"（{string.Join(", ", ps.Select(p => p.ParameterType.Name))}）⇒ **不挂补丁，接任务一律放行**");
                return;
            }

            // priority 300：与本 mod 其它补丁同档（早于默认 400），保证能在别人之前短路
            harmony.Patch(m, new HarmonyMethod(typeof(QuestAcceptGatePatch), nameof(Prefix)) { priority = 300 },
                          null, null, null, null);
            Installed = true;
            _log?.Info("[EventRotator][接任务门控] ✔ 已挂 QuestController.AcceptQuest (prefix p300)："
                     + "活动没在跑时**拒绝接受**它的任务（含「已接过的」—— 那正是列任务门控拦不住的），"
                     + "活动在跑时放行；失败响应走 SPT 标准 warning 通道，不抛异常");
            _log?.Info($"[EventRotator][接任务门控] 　响应构造器：OutputHolder={(OutputHolder != null ? "已注入" : "⚠ 空（拒绝路径会降级放行）")}、"
                     + $"HttpUtil={(HttpUtil != null ? "已注入" : "⚠ 空（拒绝路径会降级放行）")}");
        }
        catch (Exception ex)
        {
            _log?.Warning($"[EventRotator][接任务门控] ⚠ 挂补丁失败 ⇒ **接任务一律放行**：{ex.Message}");
        }
    }

    /// <summary>
    /// Prefix：`AcceptQuest(PmcData pmcData, AcceptQuestRequestData acceptedQuest, MongoId sessionID)`。
    /// 参数用 <c>object __0/__1/__2</c>（与本 mod `BossWavePatch` 同风格）⇒ 对 SPT 改参数顺序/调整类型更耐受；
    /// 具体谁是"请求"、谁是"session"按**类型**识别（不靠位置）。
    /// 返回 <c>false</c> = 不再执行原方法（**"有条目 / 没条目"两个分支都会被拦掉**）。
    /// </summary>
    private static bool Prefix(object __0, object __1, object __2, ref object __result)
    {
        try
        {
            if (!Installed) return true;

            object req = null, session = null;
            foreach (var a in new[] { __0, __1, __2 })
            {
                if (a == null) continue;
                if (req == null && a.GetType().Name.IndexOf("AcceptQuest", StringComparison.OrdinalIgnoreCase) >= 0)
                    req = a;
                else if (session == null && TypeCompat.IsMongoId(a))                       // ★ prepatch 兼容
                    session = a;
            }
            if (req == null) return true;                       // 认不出请求对象 ⇒ 放行（降级）

            var qid = Prop(req, "QuestId")?.ToString();
            if (string.IsNullOrEmpty(qid)) return true;         // 读不到任务 id ⇒ 放行（降级）

            // ★ 判据全部来自 `EventRotatorServer` 里 ApplyQuestGate 刷新的那套状态（不重算）
            if (!EventRotatorServer.IsQuestBlockedByGate(qid, out var why)) return true;

            // ── 干净拒绝：照 SPT `ShowRepeatableQuestInvalidConditionError` 的写法构造失败响应 ──
            var resp = BuildErrorResponse(session, why);
            if (resp == null)
            {
                // 构造不出标准响应 ⇒ **降级放行**（宁可漏拦，也不给客户端 null 响应）
                if (!_warnedNoResponse)
                {
                    _warnedNoResponse = true;
                    _log?.Warning("[EventRotator][接任务门控] ⚠ 构造失败响应失败（OutputHolder/HttpUtil/session 不可用）"
                                + " ⇒ 这条**降级放行**（只会出现一次这条警告）；若要彻底禁用请删掉本补丁的 Enable 调用");
                }
                return true;
            }

            __result = resp;
            _log?.Info($"[EventRotator][接任务门控] ⛔ 已拒绝接受任务 {qid}：{why}"
                     + " —— 返回标准失败响应（Warnings[].ErrorMessage），未抛异常、未改动 profile");
            return false;                                       // ★ 两个分支（有条目/没条目）一起被拦掉
        }
        catch (Exception ex)
        {
            if (!_warnedException)
            {
                _warnedException = true;
                _log?.Warning($"[EventRotator][接任务门控] ⚠ prefix 异常 ⇒ 本次放行（只会出现一次这条警告）：{ex.Message}");
            }
            return true;                                        // ★ 任何意外都放行
        }
    }

    /// <summary>
    /// 构造"标准失败响应"：`EventOutputHolder.GetOutput(sessionId)` → `HttpResponseUtil.AppendErrorToOutput`。
    /// 依据：`QuestController.ShowRepeatableQuestInvalidConditionError`（QuestController.cs:222-228）、
    /// `EventOutputHolder.cs:28-37`、`HttpResponseUtil.cs:127-160`。
    /// 返回 null = 构造不出来（调用方降级放行）。
    /// </summary>
    private static object BuildErrorResponse(object session, string reason)
    {
        try
        {
            if (OutputHolder == null || HttpUtil == null) return null;
            if (!TypeCompat.IsMongoId(session)) return null;                          // ★ prepatch 兼容
            var sid = session;                          // ★ object：运行时可能是 Patched 版的 MongoId

            // ★ v1.0.1【prepatch 兼容】改运行时反射（同上：形参是运行时那个 MongoId）
            var output = TypeCompat.InvokeWithSession(OutputHolder, "GetOutput", sid);
            if (output == null) return null;

            // ⚠ 这段文字会显示在**客户端 UI**里 ⇒ 必须 ASCII（EFT 客户端字体渲染不了中文，会变方块）
            var msg = "[EventRotator] This quest is only available while its event is active. "
                    + "Please wait until the next rotation brings the event back.";
            // ★ v1.0.1【prepatch 兼容】output 现在是 object（来自运行时反射）⇒ 同样用反射调
            var m = HttpUtil.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                              .FirstOrDefault(x => x.Name == "AppendErrorToOutput"
                                                && x.GetParameters().Length >= 1
                                                && TypeCompat.Same(x.GetParameters()[0].ParameterType, output.GetType()));
            if (m == null) return null;
            var args = m.GetParameters().Length == 1 ? new[] { output } : new[] { output, (object)msg };
            return m.Invoke(HttpUtil, args);   // BackendErrorCodes 用默认 None（同 SPT）
        }
        catch { return null; }
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

    /// <summary>读属性（本 mod 通用反射读法；大小写不敏感兜底）。</summary>
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
