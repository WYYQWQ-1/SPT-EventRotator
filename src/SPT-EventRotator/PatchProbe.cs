using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace EventRotator;

/// <summary>
/// ★★★ v1.0.2【补丁自证】**挂载期 + 调用期**各打一条身份日志，专治
/// 「日志说挂上了、实机一次都没触发、也没有任何异常」。
///
/// <para>为什么需要它：Harmony 挂补丁失败会抛异常（我们全程 try/catch + 打 warning），
/// 但**挂错程序集**是完全静默的 —— 补丁表里有、<c>GetPatchedMethods()</c> 也包含它，
/// 只是永远不会被调用。唯一能一眼判死的证据是：</para>
/// <list type="number">
/// <item>挂载期：目标的 <c>DeclaringType.Assembly</c> 的 <b>ALC / Location / MVID</b>；</item>
/// <item>调用期：Harmony 注入的 <c>__originalMethod</c> 的同一组身份；</item>
/// <item>两者与 <see cref="RuntimeTypes.LiveCoreAssembly"/> 是否 <c>ReferenceEquals</c>。</item>
/// </list>
///
/// <para>实机日志逐字格式（上线后搜 <c>[钩子自证]</c> 即可）：</para>
/// <code>
/// [EventRotator][钩子自证] BotGenerator.PrepareAndGenerateBot｜挂载：…｜同一份=True｜已进补丁表=True
/// [EventRotator][钩子自证] BotGenerator.PrepareAndGenerateBot｜调用：…｜同一份=True
/// </code>
/// </summary>
internal static class PatchProbe
{
    public static Action<string> Log;

    private static readonly HashSet<string> _mountSeen = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _callSeen = new(StringComparer.Ordinal);
    private static readonly object _lock = new();
    private static bool _copiesDumped;

    private static void Emit(string m) { try { Log?.Invoke(m); } catch { } }

    /// <summary>方法签名（逐字）：<c>返回值 类型.方法(参数1,参数2)</c>。用于日志里核对"挂的是不是那个重载"。</summary>
    internal static string Signature(MethodBase m)
    {
        if (m == null) return "(null)";
        try
        {
            var ps = string.Join(", ", m.GetParameters().Select(p => (p.ParameterType?.Name ?? "?") + " " + p.Name));
            var rt = (m as MethodInfo)?.ReturnType?.Name ?? "void";
            return $"{rt} {m.DeclaringType?.FullName}.{m.Name}({ps})";
        }
        catch (Exception ex) { return "(sig failed: " + ex.GetType().Name + ")"; }
    }

    /// <summary>挂载成功后立刻调用：打印目标身份 + 是否运行时那份 + 进程内 Core 副本清单。</summary>
    internal static void Mount(Harmony harmony, MethodBase m, string tag)
    {
        if (m == null) return;
        try
        {
            lock (_lock) { if (!_mountSeen.Add(tag)) return; }

            var asm = m.DeclaringType?.Assembly;
            var live = RuntimeTypes.LiveCoreAssembly;
            var same = ReferenceEquals(asm, live);
            var inTable = harmony != null && harmony.GetPatchedMethods().Contains(m);

            Emit($"[EventRotator][钩子自证] {tag}｜挂载：{Signature(m)}"
               + $"｜目标 {RuntimeTypes.Describe(asm)}｜已进 Harmony 补丁表={inTable}｜与运行时同一份={same}");

            if (!same)
            {
                Emit($"[EventRotator][钩子自证] ⚠ {tag}：**挂载目标不是运行时那份程序集** ⇒ 补丁永远不会触发（无异常、无日志）"
                   + $"｜运行时 {RuntimeTypes.Describe(live)}");
            }

            lock (_lock)
            {
                if (!_copiesDumped)
                {
                    _copiesDumped = true;
                    var copies = RuntimeTypes.CoreCopies();
                    Emit($"[EventRotator][钩子自证] 进程内 SPTarkov.Server.Core 副本 {copies.Count} 份："
                       + (copies.Count == 0 ? "(无)"
                          : string.Join(" ｜ ", copies.Select((c, i) =>
                              $"#{i + 1} {RuntimeTypes.Describe(c)}{(ReferenceEquals(c, live) ? " ←运行时" : " ←死副本")}"))));
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// 在 postfix/prefix 里调用（参数由 Harmony 注入的 <c>__originalMethod</c> 提供）：
    /// **每个方法只打一次**，证明"被调用的就是挂载的那个方法"，并把调用期身份与运行时对比。
    /// </summary>
    internal static void OnCall(MethodBase original, string tag)
    {
        if (original == null) return;
        try
        {
            lock (_lock) { if (!_callSeen.Add(tag)) return; }

            var asm = original.DeclaringType?.Assembly;
            var live = RuntimeTypes.LiveCoreAssembly;
            var same = ReferenceEquals(asm, live);
            Emit($"[EventRotator][钩子自证] {tag}｜调用：{Signature(original)}"
               + $"｜实参程序集 {RuntimeTypes.Describe(asm)}｜与运行时同一份={same}"
               + (same ? " ⇒ 挂载/调用同一份 ✔" : " ⇒ **不是同一份（补丁挂错）** ✘"));
        }
        catch { }
    }

    /// <summary>给探针用：不依赖 Harmony，直接把"是否同一份"算出来。</summary>
    internal static bool IsLive(MethodBase m)
    {
        try { return ReferenceEquals(m?.DeclaringType?.Assembly, RuntimeTypes.LiveCoreAssembly); }
        catch { return false; }
    }
}
