// task-14 挂钩挂载实证：加载**真实**的 mod 产物 + ABPS + Harmony，
// 反射调用 MountAbpsRebuildHook，验证它真的在两个目标方法上打上了 postfix，并复现挂载日志原文。
using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

class MountProbe
{
    static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var dir = AppContext.BaseDirectory;
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var simple = new AssemblyName(e.Name).Name;
            var p = System.IO.Path.Combine(dir, simple + ".dll");
            return System.IO.File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        foreach (var f in new[] { "SPTarkov.Server.Core.dll", "acidphantasm-botplacementsystem.dll", "0Harmony.dll" })
            Assembly.LoadFrom(System.IO.Path.Combine(dir, f));

        var modPath = Environment.GetEnvironmentVariable("MODDLL");
        var asm = Assembly.LoadFrom(modPath);
        var bp = asm.GetType("EventRotator.Patches.BossWavePatch") ?? asm.GetTypes().First(t => t.Name == "BossWavePatch");
        var loggerType = asm.GetTypes().FirstOrDefault(t => t.Name == "EventRotatorServer");
        Console.WriteLine($"mod 产物: {modPath}");
        Console.WriteLine($"BossWavePatch: {bp.FullName}  EventRotatorServer: {loggerType?.FullName}\n");

        // 目标方法
        var ms = AccessTools.TypeByName("BotPlacementSystemServer.Controllers.MapSpawns");
        var perMap = AccessTools.Method(ms, "ReplaceOriginalLocationForMap", new[] { typeof(string) });
        var allMaps = AccessTools.Method(ms, "ReplaceOriginalLocations", Type.EmptyTypes);
        Console.WriteLine($"目标①: {perMap?.DeclaringType?.Name}.{perMap?.Name}(string)");
        Console.WriteLine($"目标②: {allMaps?.DeclaringType?.Name}.{allMaps?.Name}()\n");

        // 挂前：读 patch 信息
        void Show(string tag)
        {
            foreach (var (n, m) in new[] { ("①", perMap), ("②", allMaps) })
            {
                if (m == null) continue;
                var info = Harmony.GetPatchInfo(m);
                var post = info?.Postfixes?.Select(p => $"{p.owner}/{p.PatchMethod.Name}") ?? Enumerable.Empty<string>();
                Console.WriteLine($"  {tag} 目标{n} postfix = [{string.Join(", ", post)}]");
            }
        }
        Console.WriteLine("=== 挂载前 ==="); Show("前");

        // 造一个 logger：ISptLogger<T> 是接口 ⇒ 用 DispatchProxy 捕获日志原文（不需要真实实现）
        var captured = new System.Collections.Generic.List<string>();
        object logger = null;
        try
        {
            var loggerInterface = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(t => t.IsInterface && t.Name == "ISptLogger`1" && t.IsGenericTypeDefinition);
            if (loggerInterface != null)
            {
                var closed = loggerInterface.MakeGenericType(loggerType);
                var proxyType = typeof(LogCapture<>).MakeGenericType(closed);
                logger = proxyType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
                                 ?.Invoke(null, new object[] { captured });
            }
            Console.WriteLine($"\nlogger 代理: {(logger == null ? "✗ 建不出来（不影响挂钩判定，仅日志原文抓不到）" : "✓ 已注入")}");
        }
        catch (Exception ex) { Console.WriteLine($"\n⚠ 建 logger 代理失败: {ex.GetType().Name}: {ex.Message}"); }

        var fLog = bp.GetField("_log", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        try { if (logger != null) fLog.SetValue(null, logger); } catch (Exception ex) { Console.WriteLine($"⚠ 写 _log 失败: {ex.Message}"); }

        // 真正调用被验证的方法
        var harmony = new Harmony("probe.task14");
        var mnt = bp.GetMethod("MountAbpsRebuildHook", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Console.WriteLine($"\n=== 调用 {mnt.Name}(Harmony) ===");
        try { mnt.Invoke(null, new object[] { harmony }); }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            Console.WriteLine($"  ✗ 抛出 {inner.GetType().Name}: {inner.Message}");
        }

        Console.WriteLine("\n=== 挂载后 ==="); Show("后");

        Console.WriteLine($"\n=== 捕获到的日志原文（共 {captured.Count} 条）===");
        foreach (var line in captured) Console.WriteLine($"  {line}");

        var i1 = perMap != null ? Harmony.GetPatchInfo(perMap) : null;
        var i2 = allMaps != null ? Harmony.GetPatchInfo(allMaps) : null;
        var ok1 = i1?.Postfixes?.Any(p => p.PatchMethod.Name == "PostfixAbpsReplaceForMap") == true;
        var ok2 = i2?.Postfixes?.Any(p => p.PatchMethod.Name == "PostfixAbpsReplaceAll") == true;
        Console.WriteLine($"\n  目标① 打上了 PostfixAbpsReplaceForMap = {ok1}");
        Console.WriteLine($"  目标② 打上了 PostfixAbpsReplaceAll    = {ok2}");

        // 降级路径：ABPS 不在场时（模拟：直接调 ReconcileActiveEventBosses，_dbBaseByMap 为空 ⇒ 立即 return，不抛）
        var rec = bp.GetMethod("ReconcileActiveEventBosses", BindingFlags.NonPublic | BindingFlags.Static);
        var threw = false;
        try { rec.Invoke(null, new object[] { "laboratory" }); } catch { threw = true; }
        Console.WriteLine($"  降级路径：_dbBaseByMap 为空时 ReconcileActiveEventBosses 静默返回（未抛） = {!threw}");

        var pass = ok1 && ok2 && !threw;
        Console.WriteLine($"\n════ 判定 PASS = {pass} ════");
        return pass ? 0 : 3;
    }
}

/// <summary>ISptLogger&lt;T&gt; 的 DispatchProxy：把日志原文抓进一个 List，便于离线比对。</summary>
class LogCapture<T> : DispatchProxy
{
    System.Collections.Generic.List<string> _sink;

    public static T Create(System.Collections.Generic.List<string> sink)
    {
        var p = Create<T, LogCapture<T>>();
        ((LogCapture<T>)(object)p)._sink = sink;
        return p;
    }

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        try
        {
            var msg = args != null && args.Length > 0 ? args[0] as string : null;
            if (msg != null) _sink?.Add($"[{targetMethod.Name}] {msg}");
        }
        catch { }
        return targetMethod.ReturnType == typeof(bool) ? (object)true : null;
    }
}
