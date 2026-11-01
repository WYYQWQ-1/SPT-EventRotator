// ★★★ 离线探针：**"补丁挂到死副本"根因复现 + 修复自证**（问题 A ①）
//
// 复现机制（与实机 SPT 4.1.6 的 prepatch 双载同构）：
//   · Default ALC 里载入磁盘上的 SPTarkov.Server.Core.dll（= 外层 prepatch pass 那份，**死的**）；
//   · 另开一个隔离 ALC，用 `LoadFromStream` 载入**同一份字节**（= SPT.PrepatchHost 里那份，
//     `Assembly.Location` 为空，与实机 patched copy 同型）；
//   · 把 RuntimeTypes.LiveCoreAssembly 指向隔离 ALC 那份（模拟 DI 实例 GetType().Assembly）；
//   · 断言：
//       ① 旧的 `AccessTools.TypeByName` 命中的是 **Default 那份**（= 实机 `ALC=Default` 的根因）；
//       ② 新的 `RuntimeTypes.Find` 命中的是 **隔离 ALC 那份**（= 运行时那份）；
//       ③ 在"运行时那份"上挂 Harmony postfix，真的会被调用（补丁生效）；
//       ④ `PatchProbe` 的挂载/调用日志逐字含 `同一份=True` / `挂载/调用同一份 ✔`。
//
// 不写实机、不启动服务端；只读 D:\PureEFTv4.1 的 DLL 与工作区 Release 构建。
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using HarmonyLib;
using SPTarkov.Server.Core.Models.Common;   // 强制把磁盘那份 Core 载入 Default ALC（= 死副本）
using IOPath = System.IO.Path;

static class Probe
{
    const string SPT = @"D:\PureEFTv4.1\EFTv4.1\SPT_Runtime";
    const string ModSrc = @"C:\Users\32674\OneDrive\Documents\deepseek-harness\default-workspace\fix\SPT-EventRotator";
    static string ModDll => IOPath.Combine(ModSrc, "bin", "Release", "net10.0", "SPT-EventRotator.dll");

    static readonly System.Collections.Generic.List<string> Out = new();
    static int pass, fail;
    static void Line(string s) { Out.Add(s); Console.WriteLine(s); }
    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { pass++; Line($"  [PASS] {name}" + (detail.Length > 0 ? "  | " + detail : "")); }
        else { fail++; Line($"  [FAIL] {name}  | {detail}"); }
    }

    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Line("════════ 探针 ①：补丁挂载/调用方法身份（prepatch 双载 → AccessTools 命中死副本）════════");

        if (!File.Exists(ModDll)) { Line($"✗ 找不到 {ModDll}（先 dotnet build -c Release）"); return 1; }

        // ── 0. 确保"死副本"（Default ALC / 磁盘）已装载：探针自己引用了一次 MongoId ──
        var diskMongo = typeof(MongoId).Assembly;
        Line($"  死副本（Default ALC / 磁盘）：{diskMongo.GetName().Name} v{diskMongo.GetName().Version}@{diskMongo.Location}");

        // ── 1. 隔离 ALC 里用流载入同一份 Core = 实机 SPT.PrepatchHost 里那份 ──
        Assembly live = null;
        try
        {
            var alc = new AssemblyLoadContext("probe-live", isCollectible: false);
            alc.Resolving += (ctx, name) =>
            {
                var p = IOPath.Combine(SPT, name.Name + ".dll");
                if (File.Exists(p)) { try { return ctx.LoadFromAssemblyPath(p); } catch { } }
                var hit = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name.Name);
                return hit;
            };
            var bytes = File.ReadAllBytes(IOPath.Combine(SPT, "SPTarkov.Server.Core.dll"));
            live = alc.LoadFromStream(new MemoryStream(bytes));
        }
        catch (Exception ex) { Line("✗ 隔离 ALC 载入 Core 失败：" + ex.GetType().Name + ": " + ex.Message); Flush(); return 1; }
        Line($"  运行时副本（probe-live ALC / 流载入）：{live.GetName().Name} v{live.GetName().Version}@{live.Location}");

        Check("两份 SPTarkov.Server.Core 同名同版本但**不是同一个实例**",
              ReferenceEquals(live, diskMongo) == false
              && live.GetName().Name == diskMongo.GetName().Name
              && live.GetName().Version == diskMongo.GetName().Version,
              $"live={live.GetName()} disk={diskMongo.GetName()}");

        // ── 2. 载入 mod（Default ALC），反射取 RuntimeTypes / PatchProbe ──
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var n = new AssemblyName(e.Name).Name + ".dll";
            foreach (var d in new[] { IOPath.Combine(ModSrc, "bin", "Release", "net10.0"), SPT, AppContext.BaseDirectory })
            {
                var p = IOPath.Combine(d, n);
                if (File.Exists(p)) { try { return Assembly.LoadFrom(p); } catch { } }
            }
            return null;
        };
        var modAsm = Assembly.LoadFrom(ModDll);
        var tRt = modAsm.GetType("EventRotator.RuntimeTypes", true);
        var tPp = modAsm.GetType("EventRotator.PatchProbe", true);
        const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        var msgs = new System.Collections.Generic.List<string>();
        tPp.GetField("Log", AnyStatic)?.SetValue(null, (Action<string>)(s => msgs.Add(s)));

        // 模拟 Plugin.OnLoad 第一行：DI 实例 GetType().Assembly = 运行时那份
        tRt.GetField("LiveCoreAssembly", AnyStatic)?.SetValue(null, live);
        Line($"  RuntimeTypes.LiveCoreAssembly ← {live.GetName().Name}（probe-live ALC）");

        // ── 3. 旧路径 vs 新路径 ──
        const string fullName = "SPTarkov.Server.Core.Generators.Loot.LocationLootGenerator";
        var oldType = AccessTools.TypeByName(fullName);
        var mFind = tRt.GetMethod("Find", AnyStatic);
        var newType = (Type)mFind.Invoke(null, new object[] { fullName });

        Check("旧的 AccessTools.TypeByName 命中**死副本**（Default ALC / 磁盘）",
              oldType != null && ReferenceEquals(oldType.Assembly, diskMongo),
              oldType == null ? "(null)" : $"ALC={AssemblyLoadContext.GetLoadContext(oldType.Assembly)?.Name} Location={oldType.Assembly.Location}");
        Check("新的 RuntimeTypes.Find 命中**运行时副本**（probe-live ALC / 流载入）",
              newType != null && ReferenceEquals(newType.Assembly, live),
              newType == null ? "(null)" : $"ALC={AssemblyLoadContext.GetLoadContext(newType.Assembly)?.Name} Location='{newType.Assembly.Location}'");
        Check("两者是不同的 Type 实例（= 实机「挂上了却永不触发」的机制）",
              oldType != null && newType != null && !ReferenceEquals(oldType, newType));

        // ── 4. 在"运行时那份"上真的挂一个 postfix，并**调用**它（证明补丁会生效） ──
        var harmony = new Harmony("probe.hookidentity");
        var mongoLive = live.GetType("SPTarkov.Server.Core.Models.Common.MongoId", true);
        var isValidLive = mongoLive.GetMethod("IsValidMongoId", BindingFlags.Public | BindingFlags.Static);
        var postfix = new HarmonyMethod(typeof(Probe).GetMethod(nameof(PostfixFired), BindingFlags.Public | BindingFlags.Static));
        harmony.Patch(isValidLive, postfix: postfix);

        var mPpMount = tPp.GetMethod("Mount", AnyStatic);
        var mPpCall = tPp.GetMethod("OnCall", AnyStatic);
        var mPpIsLive = tPp.GetMethod("IsLive", AnyStatic);

        mPpMount.Invoke(null, new object[] { harmony, isValidLive, "probe.MongoId.IsValidMongoId" });

        _fired = 0;
        var ret = isValidLive.Invoke(null, new object[] { "5b432d215acfc4771e1c6624" });
        Check("在运行时副本上挂的 postfix **真的被调用**（返回=True 且停靠计数=1）",
              ret is bool b && b && _fired == 1, $"ret={ret} postfixFired={_fired}");

        mPpCall.Invoke(null, new object[] { isValidLive, "probe.MongoId.IsValidMongoId" });

        // 对照：把同一个方法在死副本上的 MethodInfo 传进去 ⇒ 必须判 False
        var mongoDisk = diskMongo.GetType("SPTarkov.Server.Core.Models.Common.MongoId", true);
        var isValidDisk = mongoDisk.GetMethod("IsValidMongoId", BindingFlags.Public | BindingFlags.Static);
        var liveOk = (bool)mPpIsLive.Invoke(null, new object[] { isValidLive });
        var diskOk = (bool)mPpIsLive.Invoke(null, new object[] { isValidDisk });
        Check("PatchProbe.IsLive：运行时副本=True / 死副本=False", liveOk && !diskOk, $"live={liveOk} disk={diskOk}");

        // ── 5. 日志逐字断言 ──
        Line("  ── PatchProbe 日志逐字 ──");
        foreach (var m in msgs) foreach (var piece in m.Split('\n')) Line("  " + piece);

        Check("挂载日志：含方法签名 + ALC/Location/MVID + 已进补丁表=True + 与运行时同一份=True",
              msgs.Any(m => m.Contains("probe.MongoId.IsValidMongoId") && m.Contains("挂载：")
                            && m.Contains("ALC=probe-live") && m.Contains("MVID=")
                            && m.Contains("已进 Harmony 补丁表=True") && m.Contains("与运行时同一份=True")),
              msgs.FirstOrDefault(m => m.Contains("挂载：")) ?? "(无)");
        Check("调用日志：含方法签名 + 与运行时同一份=True + 挂载/调用同一份 ✔",
              msgs.Any(m => m.Contains("probe.MongoId.IsValidMongoId") && m.Contains("调用：")
                            && m.Contains("与运行时同一份=True") && m.Contains("挂载/调用同一份 ✔")),
              msgs.FirstOrDefault(m => m.Contains("调用：")) ?? "(无)");
        Check("副本清单：打印 2 份 Core（一份死副本 + 一份运行时）",
              msgs.Any(m => m.Contains("进程内 SPTarkov.Server.Core 副本") && m.Contains("←运行时") && m.Contains("←死副本")),
              msgs.FirstOrDefault(m => m.Contains("副本")) ?? "(无)");

        Line($"\n结果：{pass} PASS / {fail} FAIL");
        Flush();
        return fail == 0 ? 0 : 1;
    }

    static int _fired;
    public static void PostfixFired(MethodBase __originalMethod) { _fired++; }

    static void Flush()
    {
        try { File.WriteAllText(IOPath.Combine(ModSrc, "_probe_hookidentity", "probe_out.txt"), string.Join("\n", Out) + "\n", new UTF8Encoding(false)); } catch { }
    }
}
