// 探针：列出 SeasonalEventService 的所有成员（服务端 dll，不启动服务）
using System;
using System.Linq;
using System.Reflection;

internal static class Probe
{
    private static int Main()
    {
        var asm = Assembly.LoadFrom(@"D:\PureEFTv4.1\EFTv4.1\SPT_Runtime\SPTarkov.Server.Core.dll");
        var t = asm.GetType("SPTarkov.Server.Core.Services.Server.SeasonalEventService");
        if (t == null) { Console.WriteLine("找不到 SeasonalEventService"); return 2; }
        Console.WriteLine("类型: " + t.FullName);
        foreach (var c in t.GetConstructors())
            Console.WriteLine("ctor(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            Console.WriteLine("field  " + f.FieldType.Name + " " + f.Name);
        foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            Console.WriteLine("prop   " + p.PropertyType.Name + " " + p.Name);
        foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            Console.WriteLine("method " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
        return 0;
    }
}
