// 只读 E2E 探针：在**独立进程**里构造真实 ABPS v2.1.0 类型 → 反射调用 Release 版
// SPT-EventRotator.dll 的 AbpsCompat.ReadSpawnChance / SetSpawnChance，验证：
//   ① 新版强类型路径写进 BossConfig.BossX.SpawnChance.<Map> ② 回读自证 ③ 还原闭包
//   ④ 三张"名字对不上"的图（bigmap/rezervbase/sandbox）⑤ 失败原因能区分
//   ⑥ 旧版字典路径的写入/还原仍等价（直接打私有 WriteChanceToDict）
// ⚠ 只在探针进程内改内存对象；不启动服务端、不写任何 ABPS 文件。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

internal static class AbpsE2EProbe
{
    private const string AbpsDll =
        @"D:\PureEFTv4.1\EFTv4.1\SPT_Runtime\user\mods\acidphantasm-botplacementsystem\acidphantasm-botplacementsystem.dll";
    private const string Runtime = @"D:\PureEFTv4.1\EFTv4.1\SPT_Runtime";
    private const string ModDll =
        @"C:\Users\32674\OneDrive\Documents\deepseek-harness\default-workspace\fix\SPT-EventRotator\bin\Release\net10.0\SPT-EventRotator.dll";

    private static int _bad;

    private static void Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            try
            {
                var n = new AssemblyName(e.Name).Name + ".dll";
                var dirs = new List<string>
                {
                    System.IO.Path.GetDirectoryName(AbpsDll), Runtime,
                };
                // ★ 探针进程缺少服务端自带的 Microsoft.Extensions.Logging.Abstractions（SPT 的 ILogger 基接口）
                //   —— 从本机 ASP.NET 共享框架借一份（只用于让接口类型可解析，探针不写日志）。
                var aspNet = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.AspNetCore.App");
                if (System.IO.Directory.Exists(aspNet))
                    dirs.Add(System.IO.Directory.GetDirectories(aspNet).OrderByDescending(d => d).First());

                foreach (var d in dirs)
                {
                    if (string.IsNullOrEmpty(d)) continue;
                    var p = System.IO.Path.Combine(d, n);
                    if (System.IO.File.Exists(p)) return Assembly.LoadFrom(p);
                }
            }
            catch { }
            return null;
        };

        var abps = Assembly.LoadFrom(AbpsDll);
        var mod = Assembly.LoadFrom(ModDll);
        Console.WriteLine("ABPS = " + abps.GetName().Version + "   MOD = " + mod.GetName().Name + " " + mod.GetName().Version);

        var modConfig = abps.GetType("BotPlacementSystemServer.Globals.ModConfig", true);
        var cfgType = abps.GetType("BotPlacementSystemServer.Models.AbpsConfig", true);
        var bcType = abps.GetType("BotPlacementSystemServer.Models.BossConfig", true);
        var bliType = abps.GetType("BotPlacementSystemServer.Models.BossLocationInfo", true);
        var vliType = abps.GetType("BotPlacementSystemServer.Models.ValidLocationInt", true);

        // ── 构造一份最小配置：BossConfig 的 20 个 BossLocationInfo 全部 new 出来 ──
        var bc = New(bcType);
        int made = 0;
        foreach (var bp in bcType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (bp.PropertyType != bliType) continue;
            if (bp.GetIndexParameters().Length > 0) continue;   // ★ `Item` 是索引器（idx=1），不是 boss 属性
            var bli = New(bliType);
            bliType.GetProperty("SpawnChance").SetValue(bli, New(vliType));
            bp.SetValue(bc, bli);
            made++;
        }
        Console.WriteLine("构造 BossLocationInfo × " + made);

        var cfg = New(cfgType);
        cfgType.GetProperty("BossConfig").SetValue(cfg, bc);
        SetStatic(modConfig, "Config", cfg);

        var compat = mod.GetType("EventRotator.AbpsCompat", true);
        var mRead = compat.GetMethod("ReadSpawnChance", BindingFlags.Public | BindingFlags.Static);
        var mSet = compat.GetMethod("SetSpawnChance", BindingFlags.Public | BindingFlags.Static);
        var pLastMode = compat.GetProperty("LastMode", BindingFlags.Public | BindingFlags.Static);
        var pLastFail = compat.GetProperty("LastFailure", BindingFlags.Public | BindingFlags.Static);

        // 跳过 Probe()（它走 Harmony AccessTools，探针进程缺少 Microsoft.Extensions.Logging），
        // 直接注入探测缓存 —— 本探针要验的是**读写双路径**，Probe 本身未改动。
        compat.GetField("_modConfigType", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, modConfig);
        compat.GetField("_probed", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, true);
        compat.GetField("<Detected>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, true);
        Console.WriteLine("探测缓存已注入（_modConfigType / _probed=true / Detected=true）");

        int Read(string boss, string map) => Convert.ToInt32(mRead.Invoke(null, new object[] { boss, map }));
        string LastFail() => pLastFail.GetValue(null) as string;
        bool Set(string boss, string map, int v, out Action restore)
        {
            Action captured = null;
            Action<Action> hook = a => captured = a;
            var ok = Convert.ToBoolean(mSet.Invoke(null, new object[] { boss, map, v, hook }));
            restore = captured;
            return ok;
        }

        Console.WriteLine("\n== ① 新版路径：写 / 回读 / 还原 ==");
        Check("初始 Read(bossKilla,bigmap) == 0", Read("bossKilla", "bigmap") == 0);
        var ok1 = Set("bossKilla", "bigmap", 100, out var r1);
        Check("Set(bossKilla,bigmap,100) == true", ok1);
        Check("回读 Read == 100", Read("bossKilla", "bigmap") == 100);
        Check("LastMode == 新版ABPS", (pLastMode.GetValue(null) as string) == "新版ABPS");
        Check("直接查真实属性 Customs == 100",
            Convert.ToInt32(bliType.GetProperty("SpawnChance").GetValue(bcType.GetProperty("BossKilla").GetValue(bc))
                .GetType().GetProperty("Customs").GetValue(
                    bliType.GetProperty("SpawnChance").GetValue(bcType.GetProperty("BossKilla").GetValue(bc)))) == 100);
        Check("还原闭包已登记", r1 != null);
        r1();
        Check("还原后回读 == 0", Read("bossKilla", "bigmap") == 0);

        Console.WriteLine("\n== ② 三张必须靠映射表的地图 + 其它图 ==");
        foreach (var (map, prop) in new[]
                 {
                     ("bigmap", "Customs"), ("rezervbase", "Reserve"), ("sandbox", "GroundZero"),
                     ("sandbox_high", "GroundZeroHigh"), ("labyrinth", "Labyrinth"),
                     ("factory4_night", "Factory4Night"), ("tarkovstreets", "TarkovStreets"),
                 })
        {
            var done = Set("bossKilla", map, 55, out var rr);
            var vli = bliType.GetProperty("SpawnChance").GetValue(bcType.GetProperty("BossKilla").GetValue(bc));
            var actual = vli.GetType().GetProperty(prop).GetValue(vli);
            Check($"{map} → {prop} = {actual}", done && Convert.ToInt32(actual) == 55 && Read("bossKilla", map) == 55);
            rr?.Invoke();
            Check($"  {prop} 还原 = 0", Convert.ToInt32(vli.GetType().GetProperty(prop).GetValue(vli)) == 0);
        }

        Console.WriteLine("\n== ③ 失败原因可区分（应为 false，且不动任何值）==");
        var okMap = Set("bossKilla", "skier", 100, out _);
        Check($"未知 map 'skier' ⇒ false，原因：{LastFail()}", !okMap && (LastFail() ?? "").Contains("没有到 ABPS"));
        var okBoss = Set("peacemaker", "woods", 100, out _);
        // ★ 2026-10-24 缺陷2 修复后：`peacemaker` 也不在 ABPS 的 19 个强类型属性里 ⇒ 走「静默降级」分支
        //   （不再逐局 warning），`LastFailure` 的动作说明变成"没有可写位置 ⇒ 按 SPT 原生 bossChance 生效"。
        //   这里两种措辞都接受，保证探针不被日志文案卡住。
        var lfBoss = LastFail() ?? "";
        Check($"未知 boss 'peacemaker' ⇒ false，原因：{lfBoss}",
            !okBoss && (lfBoss.Contains("BossConfig 是强类型但找不到属性 'Peacemaker'")
                        || (lfBoss.Contains("没有") && lfBoss.Contains("可写位置"))));

        Console.WriteLine("\n== ④ 旧版字典路径（私有 WriteChanceToDict：写 / 还原 等价）==");
        var mWrite = compat.GetMethod("WriteChanceToDict", BindingFlags.NonPublic | BindingFlags.Static);
        var dict = new Dictionary<string, int> { ["woods"] = 7 };
        Action restoreOld = null;
        var oldOk = Convert.ToBoolean(mWrite.Invoke(null, new object[]
            { dict, "bossKojaniy", "woods", 100, (Action<Action>)(a => restoreOld = a) }));
        Check("旧版字典写入 == 100", oldOk && dict["woods"] == 100);
        restoreOld?.Invoke();
        Check("旧版字典还原 == 7", dict["woods"] == 7);

        Console.WriteLine(_bad == 0 ? "\n== 全部通过 ==" : $"\n== 失败 {_bad} 项 ==");
        Environment.Exit(_bad == 0 ? 0 : 1);
    }

    private static object New(Type t)
    {
        try { return Activator.CreateInstance(t, nonPublic: true); }
        catch { return RuntimeHelpers.GetUninitializedObject(t); }
    }

    private static void SetStatic(Type t, string name, object value)
    {
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
        if (p != null)
        {
            if (p.CanWrite) { p.SetValue(null, value); return; }
            var bf = t.GetField("<" + p.Name + ">k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
            if (bf != null) { bf.SetValue(null, value); return; }
            throw new InvalidOperationException(name + " 不可写");
        }
        var f = t.GetField(name, BindingFlags.Public | BindingFlags.Static);
        if (f != null) { f.SetValue(null, value); return; }
        throw new InvalidOperationException("找不到 " + name);
    }

    private static void Check(string what, bool ok)
    {
        if (!ok) _bad++;
        Console.WriteLine((ok ? "  [OK]   " : "  [FAIL] ") + what);
    }
}
