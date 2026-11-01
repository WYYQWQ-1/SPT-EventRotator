// 只读探针：把 AbpsCompat.cs 的映射表对到实机 ABPS v2.1.0 DLL 的真实类型上。
// 不启动服务端、不写任何东西。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

internal static class AbpsV21Probe
{
    private const string Dll = @"D:\PureEFTv4.1\EFTv4.1\SPT_Runtime\user\mods\acidphantasm-botplacementsystem\acidphantasm-botplacementsystem.dll";
    private const string Runtime = @"D:\PureEFTv4.1\EFTv4.1\SPT_Runtime";

    private static int _bad;

    private static void Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            try
            {
                var n = new AssemblyName(e.Name).Name + ".dll";
                foreach (var d in new[] { System.IO.Path.GetDirectoryName(Dll), Runtime })
                {
                    var p = System.IO.Path.Combine(d, n);
                    if (System.IO.File.Exists(p)) return Assembly.LoadFrom(p);
                }
            }
            catch { }
            return null;
        };

        var asm = Assembly.LoadFrom(Dll);
        Console.WriteLine("ABPS DLL: " + asm.GetName().Name + " v" + asm.GetName().Version + " path=" + Dll);

        var modConfig = asm.GetType("BotPlacementSystemServer.Globals.ModConfig", false);
        Check("ModConfig 类型存在", modConfig != null);
        var cfgProp = modConfig?.GetProperty("Config", BindingFlags.Public | BindingFlags.Static);
        Check("ModConfig.Config(static) 存在", cfgProp != null);
        var cfgType = cfgProp?.PropertyType;
        Console.WriteLine("  Config 类型 = " + cfgType?.FullName);

        var bcProp = cfgType?.GetProperty("BossConfig");
        Check("Config.BossConfig 存在", bcProp != null);
        var bcType = bcProp?.PropertyType;
        Console.WriteLine("  BossConfig 类型 = " + bcType?.FullName + " isDict=" + typeof(System.Collections.IDictionary).IsAssignableFrom(bcType));

        var bossProps = bcType?.GetProperties(BindingFlags.Public | BindingFlags.Instance) ?? Array.Empty<PropertyInfo>();
        Console.WriteLine("  BossConfig 属性数 = " + bossProps.Length + " : " + string.Join(",", bossProps.Select(p => p.Name)));

        // SpawnChance 的类型
        var firstBossType = bossProps.FirstOrDefault(p => p.Name == "BossKilla")?.PropertyType;
        Console.WriteLine("  BossKilla 类型 = " + firstBossType?.FullName + " isClass=" + (firstBossType?.IsClass) + " isStruct=" + (firstBossType?.IsValueType));
        var scProp = firstBossType?.GetProperty("SpawnChance");
        Check("BossLocationInfo.SpawnChance 存在", scProp != null);
        var scType = scProp?.PropertyType;
        Console.WriteLine("  SpawnChance 类型 = " + scType?.FullName + " isClass=" + (scType?.IsClass) + " isStruct=" + (scType?.IsValueType)
                          + " CanWrite=" + scProp?.CanWrite);

        var locProps = scType?.GetProperties(BindingFlags.Public | BindingFlags.Instance) ?? Array.Empty<PropertyInfo>();
        Console.WriteLine("  ValidLocationInt 属性(" + locProps.Length + ") = " +
                          string.Join(",", locProps.Select(p => $"{p.Name}:{p.PropertyType.Name}{(p.CanWrite ? "" : "(只读)")}")));

        // ── 映射表校验：13 张图 ──
        var mapTable = new (string Spt, string Abps)[]
        {
            ("bigmap", "Customs"), ("factory4_day", "Factory4Day"), ("factory4_night", "Factory4Night"),
            ("interchange", "Interchange"), ("laboratory", "Laboratory"), ("lighthouse", "Lighthouse"),
            ("rezervbase", "Reserve"), ("sandbox", "GroundZero"), ("sandbox_high", "GroundZeroHigh"),
            ("shoreline", "Shoreline"), ("tarkovstreets", "TarkovStreets"), ("woods", "Woods"),
            ("labyrinth", "Labyrinth"),
        };
        Console.WriteLine("\n== map 映射表（13）==");
        foreach (var (spt, abps) in mapTable)
        {
            var p = locProps.FirstOrDefault(x => x.Name == abps);
            Check($"map {spt} → {abps}", p != null && p.PropertyType == typeof(int) && p.CanWrite);
        }

        // ── 映射表校验：12 个 boss ──
        var bossTable = new (string Ours, string Abps)[]
        {
            ("bossKilla", "BossKilla"), ("bossBoar", "BossBoar"), ("bossKolontay", "BossKolontay"),
            ("bossGluhar", "BossGluhar"), ("bossSanitar", "BossSanitar"), ("bossKojaniy", "BossKojaniy"),
            ("bossTagilla", "BossTagilla"), ("bossZryachiy", "BossZryachiy"), ("bossKnight", "BossKnight"),
            ("bossBully", "BossBully"), ("bossPartisan", "BossPartisan"), ("sectantPriest", "SectantPriest"),
        };
        Console.WriteLine("\n== boss 映射表（12）==");
        foreach (var (ours, abps) in bossTable)
        {
            var p = bossProps.FirstOrDefault(x => x.Name == abps);
            Check($"boss {ours} → {abps}", p != null && p.PropertyType == firstBossType);
        }

        // ── 真机写读测试：在新建的 ValidLocationInt 实例上 SetValue 再读回 ──
        Console.WriteLine("\n== 写入/回读实测（新建实例）==");
        try
        {
            var inst = Activator.CreateInstance(scType, nonPublic: true);
            foreach (var name in new[] { "Customs", "TarkovStreets", "Reserve", "GroundZero", "Woods", "Labyrinth" })
            {
                var p = scType.GetProperty(name);
                p.SetValue(inst, 87);
                var back = p.GetValue(inst);
                Check($"SetValue/GetValue {name} = {back}", Convert.ToInt32(back) == 87);
            }
        }
        catch (Exception ex) { _bad++; Console.WriteLine("  [FAIL] 实例写入测试抛异常: " + ex.GetType().Name + ": " + ex.Message); }

        // ── 大小写敏感检查（验证"必须显式映射"而不是猜）──
        Console.WriteLine("\n== 反例：只用大小写转换会怎样 ==");
        foreach (var bad in new[] { "Bigmap", "Rezervbase", "Sandbox" })
            Check($"不存在属性 {bad}（证明必须靠显式表 + IgnoreCase 兜底）", locProps.All(p => p.Name != bad));

        Console.WriteLine(_bad == 0 ? "\n== 全部通过 ==" : $"\n== 失败 {_bad} 项 ==");
        Environment.Exit(_bad == 0 ? 0 : 1);
    }

    private static void Check(string what, bool ok)
    {
        if (!ok) _bad++;
        Console.WriteLine((ok ? "  [OK]   " : "  [FAIL] ") + what);
    }
}
