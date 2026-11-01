// task-14 D2 幂等性实证：直接反射调用**已编译产物里的** BossWavePatch.ReconcileActiveEventBosses，
// 模拟"ABPS 每局结束重建列表 → 我们补写"，连跑多局，验证条目数**不累积**。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

class IdemProbe
{
    const BindingFlags ALL = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static int fails = 0;

    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var dir = AppContext.BaseDirectory;

        // 探针环境需要自己解析 SPTarkov.* 依赖（LoadFrom 上下文不会自动去 BaseDirectory 找）
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var simple = new AssemblyName(e.Name).Name;
            var p = System.IO.Path.Combine(dir, simple + ".dll");
            return System.IO.File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        Assembly.LoadFrom(System.IO.Path.Combine(dir, "SPTarkov.Server.Core.dll"));

        // 加载被测产物
        var modPath = Environment.GetEnvironmentVariable("MODDLL");
        var asm = Assembly.LoadFrom(modPath);
        var bp = asm.GetType("EventRotator.Patches.BossWavePatch")
                 ?? asm.GetTypes().First(t => t.Name == "BossWavePatch");
        Console.WriteLine($"被测产物: {modPath}");
        Console.WriteLine($"类型: {bp.FullName}\n");

        // ---- 定位成员 ----
        var mRec = bp.GetMethod("ReconcileActiveEventBosses", ALL);
        var mPure = bp.GetMethod("ReconcileBossListForMap", ALL);   // ★ 纯函数（无日志/无静态状态）——幂等性就靠它
        var mProp = bp.GetMethod("Prop", ALL);
        var mEx = bp.GetMethod("ExistingBossNames", ALL);
        Check("ReconcileActiveEventBosses(string) 存在", mRec != null);
        Check("ReconcileBossListForMap(IList,string,List<string>) 存在（纯函数）", mPure != null);
        Check("Prop / ExistingBossNames 存在", mProp != null && mEx != null);
        if (mPure == null || mProp == null || mEx == null) { Console.WriteLine("定位失败，终止"); Environment.Exit(1); }

        // 纯函数签名核对
        var pps = mPure.GetParameters();
        Console.WriteLine($"  纯函数签名: ReconcileBossListForMap({string.Join(", ", pps.Select(x => x.ParameterType.Name + " " + x.Name))})"
                          + $" -> {mPure.ReturnType.Name}");
        // ---- 构造真实 BossLocationSpawn 列表 ----
        var spt = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "SPTarkov.Server.Core");
        var blsType = spt.GetType("SPTarkov.Server.Core.Models.Eft.Common.BossLocationSpawn")
                      ?? spt.GetTypes().First(t => t.Name == "BossLocationSpawn");
        Console.WriteLine($"BossLocationSpawn = {blsType.FullName}");
        var listType = typeof(List<>).MakeGenericType(blsType);

        object MakeEntry(string name)
        {
            var e = Activator.CreateInstance(blsType);
            Set(e, "BossName", name);
            Set(e, "BossChance", 30d);
            return e;
        }
        void Set(object o, string p, object v) => o.GetType().GetProperty(p)?.SetValue(o, v);
        object Get(object o, string p) => o.GetType().GetProperty(p)?.GetValue(o);
        IList NewList(params string[] names)
        {
            var l = (IList)Activator.CreateInstance(listType);
            foreach (var n in names) l.Add(MakeEntry(n));
            return l;
        }

        // ---- 构造 want / 调用纯函数（不碰 _dbBaseByMap / EventEngine / 日志）----
        var wantList = (List<string>)Activator.CreateInstance(typeof(List<string>));
        wantList.Add("bossTagilla");

        string Names(IList l) => string.Join(",", l.Cast<object>().Select(x => Get(x, "BossName")));
        bool Skull(IList l, string n) => l.Cast<object>().Any(x => (Get(x, "BossName") as string) == n
                                        && Get(x, "ShowOnTarkovMap") is bool a && a
                                        && Get(x, "ShowOnTarkovMapPvE") is bool b && b);
        int CountTagilla(IList l) => l.Cast<object>().Count(x => (Get(x, "BossName") as string) == "bossTagilla");
        object Invoke(IList l) => mPure.Invoke(null, new object[] { l, "laboratory", wantList });

        // ══════ 测试 1：已存在场景（只补显示字段 + 幂等守卫）——不依赖 BuildEntry ══════
        Console.WriteLine("════════ 测试 1：条目已存在（ABPS 表里已有 bossTagilla）→ 只补显示字段 ════════\n");
        var t1 = NewList("bossTagilla", "pmcBot", "pmcUSEC");
        Set(t1[0], "BossChance", 5d);              // 故意非 100
        Set(t1[0], "BossZone", "BotZoneKeepMe");   // 故意设一个刷点
        Set(t1[0], "ShowOnTarkovMap", false);
        var before = t1.Count;
        for (int i = 1; i <= 5; i++)
        {
            Invoke(t1);
            Console.WriteLine($"  第 {i} 次调用：表内 = [{Names(t1)}] (共 {t1.Count} 条)  bossTagilla 条数 = {CountTagilla(t1)}");
        }
        var chanceKept = Convert.ToDouble(Get(t1[0], "BossChance")) == 5d;
        var zoneKept = (Get(t1[0], "BossZone") as string) == "BotZoneKeepMe";
        var skullSet = Get(t1[0], "ShowOnTarkovMap") is bool tt && tt && Get(t1[0], "ShowOnTarkovMapPvE") is bool pp && pp;
        var noAccum = t1.Count == before && CountTagilla(t1) == 1;
        Console.WriteLine($"\n  BossChance 未被改(5 保持)  = {chanceKept}   ← D2「不改概率」");
        Console.WriteLine($"  BossZone   未被改          = {zoneKept}   ← D2「不改刷点」");
        Console.WriteLine($"  两个显示字段被补齐         = {skullSet}   ← 骷髅");
        Console.WriteLine($"  条目数恒定({before})、bossTagilla 恒为 1 条 = {noAccum}   ← 幂等，不累积");

        // ══════ 测试 2：缺失场景（走 BuildEntry 追加）══════
        Console.WriteLine("\n════════ 测试 2：条目缺失（模拟 ABPS 每局重建出无 bossTagilla 的表）→ 追加 ════════\n");
        var counts = new List<int>();
        var appends = new List<string>();
        for (int raid = 1; raid <= 5; raid++)
        {
            var fresh = NewList("pmcBot", "pmcBot", "pmcUSEC");   // ABPS 重建出的新表
            string res;
            try
            {
                var r = Invoke(fresh);
                res = $"返回 {FormatRet(r)}";
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                res = $"异常 {inner.GetType().Name}: {inner.Message}";
            }
            appends.Add(res);
            var n = CountTagilla(fresh);
            counts.Add(n);
            Console.WriteLine($"  第 {raid} 局：表内 = [{Names(fresh)}] (共 {fresh.Count} 条)  bossTagilla = {n}  骷髅 = {Skull(fresh, "bossTagilla")}  [{res}]");
        }

        Console.WriteLine("\n════════ 判定 ════════");
        Console.WriteLine($"  测试1（已存在/幂等守卫）：{(chanceKept && zoneKept && skullSet && noAccum ? "✓ PASS" : "✗ FAIL")}");
        Console.WriteLine($"  测试2 各局 bossTagilla 条数 = [{string.Join(",", counts)}]");
        var t2ok = counts.Count > 0 && counts.All(c => c == 1);
        Console.WriteLine($"  测试2（缺失→追加，每局恰好 1 条、不线性累积）：{(t2ok ? "✓ PASS" : "✗ 见上各局明细（若全为 0 说明探针环境跑不了 BuildEntry）")}");

        var pass = chanceKept && zoneKept && skullSet && noAccum && t2ok;
        Console.WriteLine($"\n════ 总判定 PASS = {pass} ════");
        Environment.Exit(pass ? 0 : 3);
    }

    static string FormatRet(object tuple)
    {
        if (tuple == null) return "null";
        var t = tuple.GetType();
        var a = t.GetField("Item1")?.GetValue(tuple);
        var b = t.GetField("Item2")?.GetValue(tuple);
        return $"(Added={a}, Marked={b})";
    }

    static void Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "✓" : "✗")} {what}");
        if (!ok) fails++;
    }
}
