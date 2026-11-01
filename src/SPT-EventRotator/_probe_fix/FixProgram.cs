// 缺陷1/缺陷2 修复的**离线实证**探针（只在探针进程里跑，不启动服务端、不改实机任何文件）。
//
// 缺陷1：直接反射调用 Release 版 SPT-EventRotator.dll 的 `BossWavePatch.BuildEntry(type, boss, map, ev)`，
//        用 events.json 里 `scourge_incursion` 的真实字段（escortType/escortCount/bossChance）构造 EventDef，
//        验证条目**在建出来的那一刻**就是 followerScourge / 4 / 100（而不是先 sectantWarrior/0 再改）。
// 缺陷2：加载实机 ABPS dll，构造它真实的强类型 BossConfig（只给 BossKilla 赋值），然后验证
//        ① BossSlotInAbps(bossKilla)=true / (bossScourge)=false
//        ② 支持的角色照旧写入成功并回读自证（没把正常路径改坏）
//        ③ 自定义角色：写入被**静默跳过**，只打**一条 info**（每进程一次），**0 条 warning**
//        ④ 连续模拟两局 `ReplayAbpsBossChances` ⇒ 仍然 0 条 warning（这正是"日志不再每局刷 ⚠"的验收点）
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

class FixProbe
{
    const BindingFlags ALL = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags ANY = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    const string Runtime = @"D:\PureEFTv4.1\EFTv4.1\SPT_Runtime";
    const string AbpsDll = @"D:\PureEFTv4.1\EFTv4.1\SPT_Runtime\user\mods\acidphantasm-botplacementsystem\acidphantasm-botplacementsystem.dll";
    const string ModDll = @"C:\Users\32674\OneDrive\Documents\deepseek-harness\default-workspace\fix\SPT-EventRotator\bin\Release\net10.0\SPT-EventRotator.dll";

    static int _fails;
    static readonly List<string> _logCalls = new();

    static void Check(string name, bool ok, string extra = "")
    {
        Console.WriteLine((ok ? "  ✔ " : "  ✘ ") + name + (string.IsNullOrEmpty(extra) ? "" : "   " + extra));
        if (!ok) _fails++;
    }

    // 动态实现 SPT 的 ISptLogger<T>（接口 ⇒ 可用 DispatchProxy），把每一条日志记下来
    public class LogSink : DispatchProxy
    {
        protected override object Invoke(MethodInfo m, object[] args)
        {
            var txt = m.Name + " | " + string.Join(" ~ ", (args ?? Array.Empty<object>()).Select(a => a?.ToString() ?? "null"));
            lock (_logCalls) _logCalls.Add(txt);
            if (m.ReturnType == typeof(bool)) return false;
            if (m.ReturnType == typeof(int)) return 0;
            if (m.ReturnType == typeof(void)) return null;
            if (m.ReturnType == typeof(IDisposable)) return null;
            return m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null;
        }
    }

    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var dir = AppContext.BaseDirectory;
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            try
            {
                var simple = new AssemblyName(e.Name).Name;
                var p = System.IO.Path.Combine(dir, simple + ".dll");
                if (System.IO.File.Exists(p)) return Assembly.LoadFrom(p);
                p = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(AbpsDll), simple + ".dll");
                if (System.IO.File.Exists(p)) return Assembly.LoadFrom(p);
            }
            catch { }
            return null;
        };

        var spt = Assembly.LoadFrom(System.IO.Path.Combine(dir, "SPTarkov.Server.Core.dll"));
        var modPath = Environment.GetEnvironmentVariable("MODDLL") ?? ModDll;
        var mod = Assembly.LoadFrom(modPath);
        Console.WriteLine($"被测产物: {modPath}");
        Console.WriteLine($"MOD = {mod.GetName().Name} {mod.GetName().Version}");

        var bp = mod.GetType("EventRotator.Patches.BossWavePatch") ?? mod.GetTypes().First(t => t.Name == "BossWavePatch");
        var defType = mod.GetType("EventRotator.EventDef") ?? mod.GetTypes().First(t => t.Name == "EventDef");
        var engineType = mod.GetType("EventRotator.EventEngine") ?? mod.GetTypes().First(t => t.Name == "EventEngine");
        var compatType = mod.GetType("EventRotator.AbpsCompat") ?? mod.GetTypes().First(t => t.Name == "AbpsCompat");
        var srvType = mod.GetType("EventRotator.EventRotatorServer") ?? mod.GetTypes().First(t => t.Name == "EventRotatorServer");

        // ── 日志捕获：把 BossWavePatch._log / AbpsCompat._log 换成探针 sink ──
        object sinkProxy = null;
        var logFieldBp = bp.GetField("_log", ALL);
        var logFieldCompat = compatType.GetField("_log", ALL);
        if (logFieldBp != null)
        {
            var loggerType = logFieldBp.FieldType;
            sinkProxy = DispatchProxy.Create(loggerType, typeof(LogSink));
            logFieldBp.SetValue(null, sinkProxy);
            if (logFieldCompat != null) logFieldCompat.SetValue(null, sinkProxy);
        }
        Check("已捕获日志（_log 注入 DispatchProxy）", sinkProxy != null, logFieldBp == null ? "找不到 _log 字段" : "");

        // ── 构造真实 EventDef：scourge_incursion / laboratory / bossScourge ──
        var ev = Activator.CreateInstance(defType);
        defType.GetProperty("Id")?.SetValue(ev, "scourge_incursion");

        var escortTypeProp = defType.GetProperty("EscortType");
        var d1 = new Dictionary<string, Dictionary<string, string>>
        {
            ["laboratory"] = new Dictionary<string, string> { ["bossScourge"] = "followerScourge" }
        };
        escortTypeProp.SetValue(ev, d1);

        var ecvType = mod.GetTypes().First(t => t.Name == "EscortCountValue");
        var ecv = Activator.CreateInstance(ecvType);
        ecvType.GetProperty("Min").SetValue(ecv, 4);
        ecvType.GetProperty("Max").SetValue(ecv, 4);
        var innerEc = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), ecvType));
        innerEc["bossScourge"] = ecv;
        var outerEc = (IDictionary)Activator.CreateInstance(
            typeof(Dictionary<,>).MakeGenericType(typeof(string), typeof(Dictionary<,>).MakeGenericType(typeof(string), ecvType)));
        outerEc["laboratory"] = innerEc;
        defType.GetProperty("EscortCount").SetValue(ev, outerEc);

        var d3 = new Dictionary<string, Dictionary<string, int>>
        {
            ["laboratory"] = new Dictionary<string, int> { ["bossScourge"] = 100 }
        };
        defType.GetProperty("BossChance").SetValue(ev, d3);

        var activesField = engineType.GetField("Actives", ALL);
        var actives = (IList)activesField.GetValue(null);
        actives.Clear();
        actives.Add(ev);
        Check("EventEngine.Actives = [scourge_incursion]", actives.Count == 1);

        // ── 构造真实 BossLocationSpawn 列表类型 ──
        var blsType = spt.GetType("SPTarkov.Server.Core.Models.Eft.Common.BossLocationSpawn")
                      ?? spt.GetTypes().First(t => t.Name == "BossLocationSpawn");
        Console.WriteLine($"BossLocationSpawn = {blsType.FullName}");
        var mBuild = bp.GetMethod("BuildEntry", ALL);
        Check("BuildEntry(Type,string,string,EventDef) 存在（缺陷1 的新签名）",
              mBuild != null && mBuild.GetParameters().Length == 4,
              mBuild == null ? "找不到" : string.Join(", ", mBuild.GetParameters().Select(p => p.ParameterType.Name)));

        object Prop(object o, string n) => o.GetType().GetProperty(n, ANY)?.GetValue(o);

        Console.WriteLine();
        Console.WriteLine("═══ 缺陷1：BuildEntry 的取值顺序（活动覆盖优先 / 内置默认兜底）═══");
        var e1 = mBuild.Invoke(null, new object[] { blsType, "bossScourge", "laboratory", ev });
        Check("条目 build 成功", e1 != null);
        if (e1 != null)
        {
            Console.WriteLine($"     BossName={Prop(e1, "BossName")} BossEscortType={Prop(e1, "BossEscortType")} "
                            + $"BossEscortAmount='{Prop(e1, "BossEscortAmount")}' BossChance={Prop(e1, "BossChance")}");
            Check("escortType = followerScourge（活动覆盖，不是内置默认 sectantWarrior）",
                  string.Equals(Prop(e1, "BossEscortType") as string, "followerScourge", StringComparison.OrdinalIgnoreCase),
                  "实得 " + Prop(e1, "BossEscortType"));
            Check("escortCount = 4（活动覆盖，不是内置默认 0）",
                  (Prop(e1, "BossEscortAmount") as string) == "4", "实得 '" + Prop(e1, "BossEscortAmount") + "'");
            Check("BossChance = 100（活动覆盖）", Math.Abs(Convert.ToDouble(Prop(e1, "BossChance")) - 100) < 0.001);
        }

        // 反例：同一活动里没声明的角色 ⇒ 仍然走内置默认（证明没把兜底改坏）
        var e2 = mBuild.Invoke(null, new object[] { blsType, "bossGluhar", "laboratory", ev });
        Check("未声明的 bossGluhar 仍走内置默认 escort=followerGluharAssault amount='0'",
              e2 != null
              && string.Equals(Prop(e2, "BossEscortType") as string, "followerGluharAssault", StringComparison.OrdinalIgnoreCase)
              && (Prop(e2, "BossEscortAmount") as string) == "0",
              $"实得 escort={Prop(e2, "BossEscortType")} amount='{Prop(e2, "BossEscortAmount")}'");

        // 再验证：prefer=null（D2 补写那一路没有 ev 变量）也能从 Actives 里取到活动覆盖
        var e3 = mBuild.Invoke(null, new object[] { blsType, "bossScourge", "laboratory", null });
        Check("prefer=null（D2 路径）同样取到活动覆盖 followerScourge/4",
              e3 != null
              && string.Equals(Prop(e3, "BossEscortType") as string, "followerScourge", StringComparison.OrdinalIgnoreCase)
              && (Prop(e3, "BossEscortAmount") as string) == "4",
              $"实得 escort={Prop(e3, "BossEscortType")} amount='{Prop(e3, "BossEscortAmount")}'");

        // 构建日志要能一眼看出"建的时候就是对的"
        bool buildLogOk = (_logCalls.Any(l => l.Contains("构建 'bossScourge' 条目") && l.Contains("escort='followerScourge'") && l.Contains("amount='4'")));
        Check("构建日志逐字含 escort='followerScourge' amount='4'", buildLogOk,
              buildLogOk ? "" : "未见该行");
        Check("构建 bossScourge 期间 0 条 warning（不再「先默认后覆盖」）",
              _logCalls.All(l => !l.StartsWith("Warning")), string.Join(" / ", _logCalls.Where(l => l.StartsWith("Warning")).Take(3)));

        // ── 缺陷2：加载实机 ABPS，构造它的强类型 BossConfig ──
        Console.WriteLine();
        Console.WriteLine("═══ 缺陷2：ABPS 强类型 BossConfig 里没有自定义角色 ⇒ 静默降级 ═══");
        Assembly abps = null;
        try { abps = Assembly.LoadFrom(AbpsDll); } catch (Exception ex) { Console.WriteLine("  加载 ABPS 失败: " + ex.Message); }
        if (abps == null) { Console.WriteLine("（ABPS 加载失败，跳过缺陷2 的实证）"); }
        else
        {
            var mcType = abps.GetType("BotPlacementSystemServer.Globals.ModConfig", true);
            var cfgType = abps.GetType("BotPlacementSystemServer.Models.AbpsConfig", true);
            var bcType = abps.GetType("BotPlacementSystemServer.Models.BossConfig", true);
            var bliType = abps.GetType("BotPlacementSystemServer.Models.BossLocationInfo", true);
            var vliType = abps.GetType("BotPlacementSystemServer.Models.ValidLocationInt", true);

            // 打印 BossConfig 的真实属性清单（逐字）
            var bossProps = bcType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                  .Where(p => p.GetIndexParameters().Length == 0)
                                  .Select(p => p.Name).ToArray();
            Console.WriteLine("     BossConfig 真实属性(" + bossProps.Length + "): " + string.Join(", ", bossProps));
            Check("BossConfig 无 bossScourge 对应属性（自定义角色没有写入口）",
                  !bossProps.Any(n => string.Equals(n, "BossScourge", StringComparison.OrdinalIgnoreCase)));
            Check("BossConfig 不是字典（没有按字符串键的 map）", !typeof(IDictionary).IsAssignableFrom(bcType));

            object NewObj(Type t) => Activator.CreateInstance(t);
            var killa = NewObj(bliType);
            bliType.GetProperty("SpawnChance").SetValue(killa, NewObj(vliType));
            var bcObj = NewObj(bcType);
            bcType.GetProperty("BossKilla").SetValue(bcObj, killa);

            var cfgObj = NewObj(cfgType);
            cfgType.GetProperty("BossConfig").SetValue(cfgObj, bcObj);

            var setCfg = mcType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static);
            setCfg.GetSetMethod(true).Invoke(null, new[] { cfgObj });
            Check("已把构造的 AbpsConfig 装进 ModConfig.Config（probe 进程内，仅内存）",
                  setCfg.GetValue(null) != null);

            var mSlot = compatType.GetMethod("BossSlotInAbps", ALL);
            var mSet = compatType.GetMethod("SetSpawnChance", ALL);
            var mRead = compatType.GetMethod("ReadSpawnChance", ALL);
            var pFail = compatType.GetProperty("LastFailure", ALL);
            Check("BossSlotInAbps / SetSpawnChance / ReadSpawnChance 存在", mSlot != null && mSet != null && mRead != null);

            _logCalls.Clear();
            var slotKilla = mSlot.Invoke(null, new object[] { "bossKilla" });
            var slotScourge = mSlot.Invoke(null, new object[] { "bossScourge" });
            Check("BossSlotInAbps(bossKilla) = true（ABPS 认这个角色）", (bool?)slotKilla == true, "实得 " + slotKilla);
            Check("BossSlotInAbps(bossScourge) = false（自定义角色，ABPS 不认）", (bool?)slotScourge == false, "实得 " + slotScourge);

            // ② 正常角色照旧可写（没把既有能力改坏）
            bool okKilla = (bool)mSet.Invoke(null, new object[] { "bossKilla", "laboratory", 42, null });
            int afterKilla = (int)mRead.Invoke(null, new object[] { "bossKilla", "laboratory" });
            Check("支持的角色照旧写入成功：bossKilla laboratory 42 → 回读 42", okKilla && afterKilla == 42, $"ok={okKilla} 回读={afterKilla}");

            // ③ 自定义角色：写入被跳过，只一条 info、0 条 warning
            _logCalls.Clear();
            bool okScourge = (bool)mSet.Invoke(null, new object[] { "bossScourge", "laboratory", 100, null });
            bool okScourge2 = (bool)mSet.Invoke(null, new object[] { "bossScourge", "laboratory", 100, null });   // 再来一次：仍不该再打
            var failure = pFail?.GetValue(null) as string;
            int warns = _logCalls.Count(l => l.StartsWith("Warning"));
            int infos = _logCalls.Count(l => l.StartsWith("Info") && l.Contains("自定义角色"));
            Check("自定义角色写入被静默跳过（返回 false，但不再打 warning）", okScourge == false && warns == 0,
                  $"warns={warns}");
            Check("说明只打**一条 info**（第二次调用不重复）", infos == 1, $"info={infos}");
            Check("失败原因写明「按 SPT 原生 bossChance 生效」",
                  failure != null && failure.Contains("原生 bossChance"), failure ?? "(null)");
            Check("失败原因不再出现「找不到属性 'BossScourge'」",
                  failure == null || !failure.Contains("找不到属性"), failure ?? "(null)");
            foreach (var l in _logCalls) Console.WriteLine("       log> " + l);

            // ④ 模拟两局：ABPS 每局重建 ⇒ ReplayAbpsBossChances 连跑两次，必须 0 条 warning
            var mReplay = bp.GetMethod("ReplayAbpsBossChances", ALL);
            Check("ReplayAbpsBossChances(string) 存在", mReplay != null);
            if (mReplay != null)
            {
                _logCalls.Clear();
                mReplay.Invoke(null, new object[] { null });
                mReplay.Invoke(null, new object[] { null });
                int rw = _logCalls.Count(l => l.StartsWith("Warning"));
                int ri = _logCalls.Count(l => l.StartsWith("Info"));
                Check("连续模拟 2 局 bossChance 回放：0 条 warning（原实机是每局一条 ⚠）", rw == 0,
                      rw == 0 ? "" : string.Join(" / ", _logCalls.Where(l => l.StartsWith("Warning")).Take(3)));
                Check("回放期间也没有任何 ⚠/未生效 文本", _logCalls.All(l => !l.Contains("⚠") && !l.Contains("未生效")));
                Console.WriteLine($"       两局回放：warning={rw} info={ri}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('═', 70));
        Console.WriteLine(_fails == 0 ? "探针结论：全部通过 ✔" : $"探针结论：{_fails} 项失败 ✘");
        Environment.Exit(_fails == 0 ? 0 : 1);
    }
}
