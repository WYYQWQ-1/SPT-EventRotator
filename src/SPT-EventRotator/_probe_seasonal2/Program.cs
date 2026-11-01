// 探针：找出哪些类型有 AddEventBossesToMaps / 与季节首领注入相关的方法
using System;
using System.Linq;
using System.Reflection;

internal static class Probe
{
    private static int Main()
    {
        var asm = Assembly.LoadFrom(@"D:\PureEFTv4.1\EFTv4.1\SPT_Runtime\SPTarkov.Server.Core.dll");
        foreach (var t in asm.GetTypes())
        {
            MethodInfo[] ms;
            try { ms = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
            catch { continue; }
            foreach (var m in ms)
            {
                if (m.Name.IndexOf("AddEventBossesToMaps", StringComparison.OrdinalIgnoreCase) >= 0
                    || m.Name.IndexOf("EnableSeasonalEvents", StringComparison.OrdinalIgnoreCase) >= 0
                    || m.Name.IndexOf("AddEventBossToMap", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Console.WriteLine(t.FullName + " :: " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
                }
            }
        }
        return 0;
    }
}
