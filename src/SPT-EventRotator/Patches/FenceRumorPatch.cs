using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace EventRotator.Patches;

/// <summary>
/// 第 3 项【Fence 传闻】的挂载点：<c>GameCallbacks.GameStart</c> 的 postfix。
///
/// ── 为什么挂这里（Cecil 反编译核对）─────────────────────────────────────────────
///   <c>SPTarkov.Server.Core.Callbacks.GameCallbacks::GameStart(string url, EmptyRequestData _, MongoId sessionID)</c>
///   是客户端「进入游戏」的入口（**不是 async**，IL 里同步调用
///   <c>GameController.GameStart(url, sessionID, timeUtil.GetTimeStamp())</c> 后返回 ValueTask）。
///   ⇒ postfix 一定在控制器做完之后跑，而且参数里就带着 **sessionID**，正好拿去发消息。
///
/// ── 降级 ────────────────────────────────────────────────────────────────────
///   找不到类型/方法 ⇒ 只 warning（Fence 传闻不生效，**绝不影响登录**）；
///   postfix 内任何异常都被 <see cref="FenceRumor"/> 自己吞掉并打 warning。
/// </summary>
internal static class FenceRumorPatch
{
    private static Action<string> _log;

    public static void Enable(Harmony harmony, Action<string> log)
    {
        _log = log;
        try
        {
            var t = RuntimeTypes.Find("SPTarkov.Server.Core.Callbacks.GameCallbacks")
                    ?? RuntimeTypes.FindSimple("GameCallbacks");
            if (t == null)
            {
                Warn("找不到 GameCallbacks ⇒ Fence 传闻未挂载（其余功能不受影响）");
                return;
            }

            var m = AccessTools.Method(t, "GameStart");
            if (m == null)
            {
                Warn("找不到 GameCallbacks.GameStart ⇒ Fence 传闻未挂载");
                return;
            }

            harmony.Patch(m, postfix: new HarmonyMethod(typeof(FenceRumorPatch), nameof(Postfix)));
            _log?.Invoke("[EventRotator][Fence传闻] 已挂载 GameCallbacks.GameStart postfix（活动集合变化 ⇒ 进游戏时发一条黑商消息）");
            try
            {
                var asm = t.Assembly;
                var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(asm)?.Name ?? "(default)";
                _log?.Invoke($"[EventRotator][Fence传闻] 钩子身份：type={t.FullName}｜assembly={asm.GetName().Name} "
                           + $"v{asm.GetName().Version}｜ALC={alc}｜参数=({string.Join(",", m.GetParameters().Select(p => p.Name))})"
                           + $"｜GetPatchedMethods 含它={harmony.GetPatchedMethods().Contains(m)}");
                PatchProbe.Mount(harmony, m, "GameCallbacks.GameStart");
            }
            catch (Exception ex2) { Warn("钩子身份自证失败（不影响补丁）：" + ex2.Message); }
        }
        catch (Exception ex)
        {
            Warn("挂载失败（已降级，不影响登录）：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// postfix 用 <c>object[] __args</c> 取参：SPT 若走 `Core.Patched.dll`，运行时的 `MongoId`
    /// 与本 mod 编译期那份**不是同一个 Type**，所以不按类型声明形参，按 <see cref="TypeCompat.IsMongoId"/> 认。
    /// </summary>
    public static void Postfix(object[] __args)
    {
        try
        {
            if (__args == null || __args.Length == 0) return;
            object session = null;
            for (int i = __args.Length - 1; i >= 0; i--)   // sessionID 在最后
                if (TypeCompat.IsMongoId(__args[i])) { session = __args[i]; break; }
            if (session == null) return;
            FenceRumor.OnPlayerEnterGame(session);
        }
        catch (Exception ex)
        {
            try { _log?.Invoke("[EventRotator][Fence传闻] ⚠ postfix 异常（不影响登录）：" + ex.Message); } catch { }
        }
    }

    private static void Warn(string m)
    {
        try { _log?.Invoke("[EventRotator][Fence传闻] ⚠ " + m); } catch { }
    }
}
