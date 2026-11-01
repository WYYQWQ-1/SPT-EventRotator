// 探针：验证「显式互斥」生效（组合展开不带出 / Rotation.Conflicts 判定为真）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

internal static class Probe
{
    private static readonly string ModDir =
        @"C:\Users\32674\OneDrive\Documents\deepseek-harness\default-workspace\fix\SPT-EventRotator";

    private static int Main()
    {
        // 让 SPT_Data 之外的依赖（SPTarkov.Common / Server.Core …）从探针输出目录解析
        var probeDir = AppContext.BaseDirectory;
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            try
            {
                var simple = new AssemblyName(e.Name).Name;
                var cand = Path.Combine(probeDir, simple + ".dll");
                return File.Exists(cand) ? Assembly.LoadFrom(cand) : null;
            }
            catch { return null; }
        };

        var dll = Path.Combine(ModDir, "bin", "Release", "net10.0", "SPT-EventRotator.dll");
        var asm = Assembly.LoadFrom(dll);
        var tEvent = asm.GetType("EventRotator.EventDef");
        var tEngine = asm.GetType("EventRotator.EventEngine");
        var tRot = asm.GetType("EventRotator.Rotation");
        if (tEvent == null || tEngine == null || tRot == null)
        {
            Console.WriteLine("!! 找不到 EventDef / EventEngine / Rotation");
            return 2;
        }

        // 直接用 JSON 反序列化 events.json（字段名与 EventDef 一致）
        var json = File.ReadAllText(Path.Combine(ModDir, "events.json"));
        var listType = typeof(List<>).MakeGenericType(tEvent);
        var all = (System.Collections.IList)JsonSerializer.Deserialize(json, listType,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var evs = all.Cast<object>().ToList();

        object ById(string id) => evs.First(e => string.Equals(
            tEvent.GetProperty("Id")!.GetValue(e)?.ToString(), id, StringComparison.OrdinalIgnoreCase));

        var halloween = ById("halloween");
        var cult = ById("night_of_the_cult");

        // ① MutuallyExcluded
        var miMutex = tEngine.GetMethod("MutuallyExcluded", BindingFlags.Public | BindingFlags.Static)!;
        bool mutex = (bool)miMutex.Invoke(null, new[] { halloween, cult })!;
        Console.WriteLine($"① MutuallyExcluded(halloween, night_of_the_cult) = {mutex}");

        // ② Rotation.Conflicts
        var miConf = tRot.GetMethod("Conflicts", BindingFlags.Public | BindingFlags.Static)!;
        bool conf = (bool)miConf.Invoke(null, new[] { halloween, cult })!;
        Console.WriteLine($"② Rotation.Conflicts(halloween, night_of_the_cult) = {conf}");

        // ③ 组合展开：只喂 halloween，看会不会带出 cult
        var miExpand = tEngine.GetMethod("ExpandFamilyCombosCore", BindingFlags.Public | BindingFlags.Static)!;
        var seedList = (System.Collections.IList)Activator.CreateInstance(listType)!;
        seedList.Add(halloween);
        var expanded = (System.Collections.IList)miExpand.Invoke(null, new object[]
        {
            seedList, all, DateTime.Today, true, false
        })!;
        var ids = expanded.Cast<object>()
            .Select(e => tEvent.GetProperty("Id")!.GetValue(e)?.ToString()).ToList();
        Console.WriteLine($"③ ExpandFamilyCombosCore([halloween]) → {expanded.Count} 个: {string.Join(",", ids)}");
        bool cultIn = ids.Any(x => string.Equals(x, "night_of_the_cult", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"   night_of_the_cult 是否被带出 = {cultIn}（期望 False）");

        // ④ 反向：只喂 cult，看会不会带出 halloween
        var seed2 = (System.Collections.IList)Activator.CreateInstance(listType)!;
        seed2.Add(cult);
        var exp2 = (System.Collections.IList)miExpand.Invoke(null, new object[] { seed2, all, DateTime.Today, true, false })!;
        var ids2 = exp2.Cast<object>().Select(e => tEvent.GetProperty("Id")!.GetValue(e)?.ToString()).ToList();
        bool hallIn = ids2.Any(x => string.Equals(x, "halloween", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"④ ExpandFamilyCombosCore([night_of_the_cult]) → {exp2.Count} 个；带出 halloween = {hallIn}（期望 False）");

        // ⑤ 邪教之夜 是否仍可被轮换选中（不是"过时年份变体"）
        var miOutdated = tEngine.GetMethod("IsOutdatedYearVariant", BindingFlags.Public | BindingFlags.Static)!;
        bool outdated = (bool)miOutdated.Invoke(null, new object[] { cult, all })!;
        Console.WriteLine($"⑤ IsOutdatedYearVariant(night_of_the_cult) = {outdated}（期望 False ⇒ 仍可进轮换池）");

        // ⑥ 邪教之夜 单独是否存在互斥阻挡（没有 halloween 时不该被自己挡住）
        bool selfBlocked = (bool)miConf.Invoke(null, new[] { cult, cult })!;
        Console.WriteLine($"⑥ Conflicts(邪教之夜, 它自己) = {selfBlocked}（仅用于确认无异常）");

        bool ok = mutex && conf && !cultIn && !hallIn && !outdated;
        Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
        return ok ? 0 : 1;
    }
}
