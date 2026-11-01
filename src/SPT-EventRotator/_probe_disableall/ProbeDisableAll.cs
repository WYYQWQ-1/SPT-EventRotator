// ═══════════════════════════════════════════════════════════════════════════════════════
// v2.7.0 硬开关「不启用任何活动」(config.json → disableAllEvents) 的**离线实证探针**。
//
// 只在探针进程里跑：不启动服务端、不动实机 mod 目录（全部在 %TEMP% 的临时目录里重建）。
// 被测对象 = 刚编译出来的 Release 产物 SPT-EventRotator.dll（不是另写一份模拟逻辑）。
//
// 覆盖用户要求的第三部分：
//   ① 复现根因：清空 force.txt（= 面板"关掉所有活动"）后，**活动依旧被自动选上**
//   ② 模拟"上次套用了 halloween"，逐项 before/after：
//        · DB 首领表（真实 LocationTable + ApplyToDb → 加首领/改概率）
//        · 天气季节（真实 WeatherConfig.OverrideSeason）
//        · 外部联动 Blackout（真实配置文件 100 → 用户原值）
//        · 原生季节镜像（seasonalevents 的 zombieSettings.enabled）
//   然后 disableAllEvents=true ⇒ 调真实 `HardRestoreForDisableAllEvents()` ⇒ 逐项断言还原
//   ③ disableAllEvents=true 时 force.txt 里写 halloween ⇒ 断言仍然不套用
//   ④ disableAllEvents=false ⇒ 恢复正常（force 重新被采用）+ 贴“恢复正常轮换逻辑”那行日志
//   ⑤ 幂等：连跑两次硬还原，值不变、⛔ 日志只打一次
// ═══════════════════════════════════════════════════════════════════════════════════════
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

class ProbeDisableAll
{
    const BindingFlags ALL = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags ANY = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    const string SrcDir = @"C:\Users\32674\OneDrive\Documents\deepseek-harness\default-workspace\fix\SPT-EventRotator";
    const string ModDll = SrcDir + @"\bin\Release\net10.0\SPT-EventRotator.dll";

    static int _fails;
    static readonly List<string> _log = new();

    static void Check(string name, bool ok, string extra = "")
    {
        Console.WriteLine((ok ? "  ✔ " : "  ✘ ") + name + (string.IsNullOrEmpty(extra) ? "" : "   " + extra));
        if (!ok) _fails++;
    }

    public class LogSink : DispatchProxy
    {
        protected override object Invoke(MethodInfo m, object[] args)
        {
            var txt = (args == null || args.Length == 0) ? "" : string.Join(" ~ ", args.Select(a => a?.ToString() ?? "null"));
            lock (_log) _log.Add(m.Name + " | " + txt);
            if (m.ReturnType == typeof(bool)) return true;
            if (m.ReturnType == typeof(int)) return 0;
            if (m.ReturnType == typeof(void)) return null;
            if (m.ReturnType == typeof(IDisposable)) return null;
            return m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null;
        }
    }

    static bool SawLog(string needle) { lock (_log) return _log.Any(x => x.Contains(needle)); }
    static int CountLog(string needle) { lock (_log) return _log.Count(x => x.Contains(needle)); }
    static void DumpLog(string needle, int max = 12)
    {
        List<string> hits; lock (_log) hits = _log.Where(x => x.Contains(needle)).ToList();
        foreach (var h in hits.Take(max)) Console.WriteLine("      LOG " + h);
        if (hits.Count > max) Console.WriteLine($"      … 共 {hits.Count} 条");
    }

    static Assembly _spt;
    static Assembly _mod;
    static object _logProxy;
    static object _locationTable;
    static object _weatherConfig;
    static IList _bossList;
    static string _tmpRoot;
    static string _modDir;
    static string _blackoutCfg, _blackDivCfg;

    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var dir = AppContext.BaseDirectory;
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            try
            {
                var simple = new AssemblyName(e.Name).Name;
                var p = Path.Combine(dir, simple + ".dll");
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            catch { }
            return null;
        };

        _spt = Assembly.LoadFrom(Path.Combine(dir, "SPTarkov.Server.Core.dll"));
        var modPath = Environment.GetEnvironmentVariable("MODDLL") ?? ModDll;
        _mod = Assembly.LoadFrom(modPath);
        Console.WriteLine($"被测产物: {modPath}");
        Console.WriteLine($"MOD = {_mod.GetName().Name} {_mod.GetName().Version}\n");

        var engine = T("EventRotator.EventEngine");
        var bossPatch = T("EventRotator.Patches.BossWavePatch");
        var weatherDim = T("EventRotator.WeatherSeasonDimension");
        var extBridge = T("EventRotator.ExternalBridges");
        var srvType = T("EventRotator.EventRotatorServer");
        var defType = T("EventRotator.EventDef");

        // ── logger（DispatchProxy 实现 ISptLogger<EventRotatorServer>）
        var logFieldType = srvType.GetConstructors().First().GetParameters()[0].ParameterType;   // ISptLogger<EventRotatorServer>
        _logProxy = DispatchProxy.Create(logFieldType, typeof(LogSink));
        engine.GetField("Log", ALL)?.SetValue(null, _logProxy);
        bossPatch.GetField("_log", ALL)?.SetValue(null, _logProxy);

        // ── 临时 mod 目录（不动实机）
        _tmpRoot = Path.Combine(SrcDir, "_probe_disableall", "_tmp", "run_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        _modDir = Path.Combine(_tmpRoot, "user", "mods", "SPT-EventRotator");
        Directory.CreateDirectory(_modDir);
        File.Copy(Path.Combine(SrcDir, "events.json"), Path.Combine(_modDir, "events.json"));
        WriteConfig(disableAll: false);
        File.WriteAllText(Path.Combine(_modDir, "force.txt"), "halloween");
        engine.GetField("ModPath", ALL)?.SetValue(null, _modDir);
        Console.WriteLine($"临时 mod 目录: {_modDir}\n");

        // ── 外部联动目标（Blackout / BlackDivServer 的真实配置文件形状）
        var modsRoot = Path.Combine(_tmpRoot, "user", "mods");
        Directory.CreateDirectory(Path.Combine(modsRoot, "Blackout"));
        Directory.CreateDirectory(Path.Combine(modsRoot, "BlackDivServer"));
        _blackoutCfg = Path.Combine(modsRoot, "Blackout", "config.json");
        _blackDivCfg = Path.Combine(modsRoot, "BlackDivServer", "config.jsonc");
        // 真实键：Blackout.config.json → blackoutChance（小数）/ BlackDivServer.config.jsonc → labsStartChance
        File.WriteAllText(_blackoutCfg, "{\n  // 用户原值\n  \"blackoutChance\": 25\n}\n");
        File.WriteAllText(_blackDivCfg, "{\n  // 用户原值\n  \"labsStartChance\": 15\n}\n");

        // ═══════════════ ① 复现根因：清空 force.txt 后活动依旧被自动选上
        Console.WriteLine("═══ ① 复现根因：force.txt=halloween → 清空 → 活动是否还在？ ═══");
        engine.GetMethod("Load", ALL).Invoke(null, new object[] { _modDir, _logProxy });
        var actives = (IList)engine.GetField("Actives", ALL).GetValue(null);
        Check("force.txt=halloween ⇒ Actives = halloween+night_of_the_cult",
              Ids(actives) == "halloween,night_of_the_cult", "实际=" + Ids(actives));
        DumpLog("FORCED event(s)", 2);

        File.WriteAllText(Path.Combine(_modDir, "force.txt"), "");          // = 面板「清空 / 关掉所有活动」
        engine.GetMethod("ReloadSettingsFromDisk", ALL).Invoke(null, new object[] { _logProxy });
        var afterClear = Ids(actives);
        Console.WriteLine($"      「关掉所有活动」后 Actives = [{afterClear}]");
        Check("★根因①复现：清空 force.txt 后**仍有活动生效**（自动层把活动补回来）", afterClear.Length > 0, "实际=" + afterClear);
        DumpLog("季节活动阶段已到", 1);
        DumpLog("奖励轮换] 今日奖励活动", 1);

        // ═══════════════ ② 模拟「上次套用了 halloween」的 before 快照
        Console.WriteLine("\n═══ ② 模拟上次已套用 halloween，逐项 before 快照 ═══");
        File.WriteAllText(Path.Combine(_modDir, "force.txt"), "halloween");
        engine.GetMethod("Load", ALL).Invoke(null, new object[] { _modDir, _logProxy });
        Console.WriteLine($"      当前 Actives = [{Ids(actives)}]");

        BuildLocationTable();
        bossPatch.GetMethod("ApplyToDb", ALL).Invoke(null, new object[] { _locationTable });
        var bossAfterApply = _bossList.Count;
        var chanceAfterApply = Chance(_bossList[0]);
        Check("DB：halloween 套用后 bigmap 首领表被改（加条目/提概率）",
              bossAfterApply != 1 || Math.Abs(chanceAfterApply - 30) > 1e-6,
              $"条目 {bossAfterApply} / 第1条概率 {chanceAfterApply}");
        DumpLog("DB boss apply on LocationTable", 1);

        _weatherConfig = Activator.CreateInstance(Spt("WeatherConfig"));
        weatherDim.GetField("WeatherConfig", ALL).SetValue(null, _weatherConfig);
        weatherDim.GetField("Log", ALL).SetValue(null, new Action<string>(m => { lock (_log) _log.Add("weather | " + m); }));
        weatherDim.GetField("AddRestore", ALL).SetValue(null,
            new Action<Action>(a => _restores.Add(a)));
        weatherDim.GetMethod("Apply", ALL).Invoke(null, new object[] { "WINTER", "probe before", _logProxy });
        var seasonBefore = SeasonOf(_weatherConfig);
        Check("天气：OverrideSeason 被套成 WINTER", seasonBefore == "WINTER", "实际=" + seasonBefore);

        extBridge.GetField("RootOverride", ALL)?.SetValue(null, modsRoot);
        extBridge.GetMethod("ApplyBlackout", ALL).Invoke(null, new object[] { true });
        Check("外部联动：Blackout.blackoutChance 被拉到 100（before）",
              ReadInt(_blackoutCfg, "blackoutChance") == 100 && ReadInt(_blackDivCfg, "labsStartChance") == 100,
              $"Blackout={ReadInt(_blackoutCfg, "blackoutChance")} BlackDiv={ReadInt(_blackDivCfg, "labsStartChance")}");

        var secCfg = BuildSeasonalEventConfig();
        if (secCfg != null)
        {
            var zsEnabled = ZombieEnabled(secCfg);
            Check("原生季节镜像：zombieSettings.enabled 被开成 true（before）", zsEnabled == true, "实际=" + zsEnabled);
        }
        else Console.WriteLine("      （seasonalevents 模型构造失败 ⇒ 该步只验证日志）");

        // 战利品倍率（`Restore()` 里用 `_baseLoose` 复位的那一层）+ 一条"任意维度"的还原闭包
        var locCfg = Activator.CreateInstance(Spt("LocationConfig"));
        var looseProp = locCfg.GetType().GetProperties(ANY).First(x => x.Name.Equals("LooseLootMultiplier", StringComparison.OrdinalIgnoreCase));
        var looseDict = (IDictionary)looseProp.GetValue(locCfg);
        if (looseDict == null) { looseDict = new Dictionary<string, double>(); looseProp.SetValue(locCfg, looseDict); }
        looseDict["bigmap"] = 3.5;                      // 活动把松散战利品倍率抬到 3.5（before）
        _fake = "dirty";
        _restores.Add(() => _fake = "restored");        // = 战利品 transformer / globals 倍率等维度的还原闭包
        var baseLoose = new Dictionary<string, double> { ["bigmap"] = 1.0 };
        Console.WriteLine($"      松散战利品倍率(bigmap):  before {looseDict["bigmap"]}（基准 1.0）");

        // ═══════════════ ③ disableAllEvents=true ⇒ 完整还原（逐项 after）
        Console.WriteLine("\n═══ ③ disableAllEvents=true ⇒ 硬还原，逐项 after ═══");
        WriteConfig(disableAll: true);
        var reload = (string)engine.GetMethod("ReloadSettingsFromDisk", ALL).Invoke(null, new object[] { _logProxy });
        Check("闸门：Actives 被清空（不采用 force.txt）", actives.Count == 0, "实际=" + Ids(actives) + " / 返回值=" + reload);
        Check("force.txt 文件**没被删改**（不采用 ≠ 删文件）",
              File.ReadAllText(Path.Combine(_modDir, "force.txt")).Trim() == "halloween",
              "内容=" + File.ReadAllText(Path.Combine(_modDir, "force.txt")).Trim());

        var srv = RuntimeHelpers.GetUninitializedObject(srvType);
        SetField(srv, "logger", _logProxy);
        SetField(srv, "_restoreActions", _restores);
        SetField(srv, "_baseLoose", baseLoose);
        SetField(srv, "_baseStatic", new Dictionary<string, double>());
        SetField(srv, "locationTable", _locationTable);
        SetField(srv, "weatherConfig", _weatherConfig);
        SetField(srv, "locationConfig", locCfg);
        if (secCfg != null) SetField(srv, "seasonalEventConfig", secCfg);

        var hardRestore = srvType.GetMethod("HardRestoreForDisableAllEvents", ANY);
        Check("方法存在：HardRestoreForDisableAllEvents()", hardRestore != null);
        // ★ 走真实的入口 `RestoreThenApply`：它先 `Restore()`（回放还原闭包 + 复位倍率），
        //   再进硬开关分支做 DB/原生季节/外部联动/天气还原。
        var rta = srvType.GetMethod("RestoreThenApply", ANY);
        Check("方法存在：RestoreThenApply()（硬开关分支入口）", rta != null);
        rta.Invoke(srv, null);

        Console.WriteLine("\n  ── 逐项 before / after ──");
        var bossAfter = _bossList.Count;
        var chanceAfter = Chance(_bossList[0]);
        Console.WriteLine($"      DB 首领表(bigmap):   before 条目 {bossAfterApply} / 概率 {chanceAfterApply}   →   after 条目 {bossAfter} / 概率 {chanceAfter}");
        Check("DB：追加条目被删掉、原概率回放", bossAfter == 1 && Math.Abs(chanceAfter - 30) < 1e-6);
        var seasonAfter = SeasonOf(_weatherConfig) ?? "null";
        Console.WriteLine($"      天气 OverrideSeason: before WINTER   →   after {seasonAfter}");
        Check("天气：OverrideSeason 还原成 null（不覆盖）", seasonAfter == "null");
        var bo = ReadInt(_blackoutCfg, "blackoutChance"); var bd = ReadInt(_blackDivCfg, "labsStartChance");
        Console.WriteLine($"      Blackout 概率:       before 100/100   →   after {bo}/{bd}");
        Check("外部联动：回用户原值 25/15", bo == 25 && bd == 15);
        if (secCfg != null)
        {
            var zsAfter = ZombieEnabled(secCfg);
            Console.WriteLine($"      zombieSettings.enabled: before true   →   after {zsAfter}");
            Check("原生季节镜像：僵尸开关被关回 false", zsAfter == false);
        }
        Check("Actives 仍为空", actives.Count == 0, "实际=" + Ids(actives));
        Console.WriteLine($"      松散战利品倍率(bigmap): after {looseDict["bigmap"]}（基准 1.0）");
        Check("战利品倍率：Restore() 按 _baseLoose 复位成 1.0", Math.Abs(Convert.ToDouble(looseDict["bigmap"]) - 1.0) < 1e-9);
        Console.WriteLine($"      任意维度还原闭包:      before dirty   →   after {_fake}");
        Check("还原闭包：_restoreActions 被 LIFO 回放（transformer/globals/倍率… 全走这条）", _fake == "restored");
        Console.WriteLine($"      ⛔ 日志：{CountLog("⛔ disableAllEvents=true")} 条");
        DumpLog("⛔ disableAllEvents", 1);
        DumpLog("[disableAllEvents]", 8);

        // ═══════════════ ④ 幂等
        Console.WriteLine("\n═══ ④ 幂等：再跑一次硬还原 ═══");
        hardRestore.Invoke(srv, null);
        Check("幂等：值不变", _bossList.Count == 1 && Math.Abs(Chance(_bossList[0]) - 30) < 1e-6
              && SeasonOf(_weatherConfig) == null && ReadInt(_blackoutCfg, "blackoutChance") == 25);
        Check("幂等：⛔ 日志仍只有 1 条（SigChanged 降噪）", CountLog("⛔ disableAllEvents=true") == 1,
              "实际=" + CountLog("⛔ disableAllEvents=true"));

        // ═══════════════ ⑤ disableAllEvents=true + force.txt=halloween ⇒ 仍然不套用
        Console.WriteLine("\n═══ ⑤ disableAllEvents=true 时 force.txt 写 halloween ═══");
        File.WriteAllText(Path.Combine(_modDir, "force.txt"), "halloween");
        engine.GetMethod("Load", ALL).Invoke(null, new object[] { _modDir, _logProxy });
        Check("★仍然不套用（Actives 为空）", actives.Count == 0, "实际=" + Ids(actives));
        Check("也没有出现 FORCED 行", !SawLog("FORCED event(s)") || CountLog("FORCED event(s)") == 2,
              "FORCED 行数=" + CountLog("FORCED event(s)"));

        // ═══════════════ ⑥ disableAllEvents=false ⇒ 恢复正常
        Console.WriteLine("\n═══ ⑥ disableAllEvents=false ⇒ 恢复正常轮换逻辑 ═══");
        WriteConfig(disableAll: false);
        engine.GetMethod("Load", ALL).Invoke(null, new object[] { _modDir, _logProxy });
        var restored = Ids(actives);
        Check("force.txt=halloween 重新被采用", restored == "halloween,night_of_the_cult", "实际=" + restored);
        // 触发"恢复正常"那行（真跑 RestoreThenApply 的正常分支）
        srvType.GetField("_disableAllEventsWasOn", ANY)?.SetValue(srv, true);
        try { srvType.GetMethod("RestoreThenApply", ANY).Invoke(srv, null); }
        catch (Exception ex) { Console.WriteLine("      （RestoreThenApply 正常分支在探针里部分步骤缺 DI 实例，已忽略："
                                                + ex.GetType().Name + "）"); }
        Console.WriteLine($"      日志条数：{CountLog("disableAllEvents=false ⇒ 恢复正常轮换逻辑")}");
        DumpLog("disableAllEvents=false ⇒ 恢复正常轮换逻辑", 1);
        Check("贴出『恢复正常轮换逻辑』日志",
              CountLog("disableAllEvents=false ⇒ 恢复正常轮换逻辑") >= 1);

        // ═══════════════ ⑦ 网页面板的保存通道（白名单 + 结构校验 + 回读）
        Console.WriteLine("\n═══ ⑦ 网页面板 ConfigWebService.Save 能否写这个键 ═══");
        var webType = T("ConfigWebService");
        var web = Activator.CreateInstance(webType);
        var mSave = webType.GetMethod("Save");
        ValueTuple<bool, string> SavePatch(params (string k, object v)[] kvs)
        {
            var p = new JsonObject();
            foreach (var (k, v) in kvs) p[k] = v is bool b ? JsonValue.Create(b) : JsonValue.Create(v?.ToString());
            return (ValueTuple<bool, string>)mSave.Invoke(web, new object[] { p });
        }
        var r1 = SavePatch(("disableAllEvents", (object)true));
        Check("Save({disableAllEvents:true}) 返回 ok", r1.Item1, r1.Item2);
        Check("写盘回读 = true（ConfigWeb.BoolKeys 白名单生效）", ReadBool("disableAllEvents") == true,
              "文件值=" + ReadBool("disableAllEvents"));
        var r2 = SavePatch(("disableAllEvents", (object)false));
        Check("Save({disableAllEvents:false}) 返回 ok", r2.Item1, r2.Item2);
        Check("写盘回读 = false（可逆）", ReadBool("disableAllEvents") == false,
              "文件值=" + ReadBool("disableAllEvents"));
        Check("默认配置模板里也有这个键（老配置会被 MergeMissingKeys 自动补上）",
              File.ReadAllText(Path.Combine(SrcDir, "config.json")).Contains("\"disableAllEvents\""));

        Console.WriteLine("\n════════════════════════════════");
        Console.WriteLine(_fails == 0 ? "全部通过 ✔（0 失败）" : $"✘ 失败 {_fails} 项");
        try { Directory.Delete(_tmpRoot, true); } catch { }
        Environment.Exit(_fails == 0 ? 0 : 1);
    }

    static readonly List<Action> _restores = new();
    static string _fake = "dirty";   // 模拟"某个维度改过的字段"，由还原闭包写回

    // ── helpers ─────────────────────────────────────────────────────────────
    static Type T(string full) => _mod.GetType(full) ?? _mod.GetTypes().First(t => t.Name == full.Split('.').Last());
    static Type Spt(string name)
    {
        var full = "SPTarkov.Server.Core.Models.Spt.Config." + name;
        return _spt.GetType(full) ?? SptAny(name);
    }
    static string Ids(IList actives)
        => string.Join(",", actives.Cast<object>().Select(e => e.GetType().GetProperty("Id").GetValue(e)?.ToString() ?? ""));

    static void SetField(object obj, string name, object val)
    {
        var f = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
             ?? obj.GetType().GetField("<" + name + ">P", BindingFlags.NonPublic | BindingFlags.Instance)
             ?? obj.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(x => x.Name.Contains(name));
        if (f == null) { Console.WriteLine($"      ⚠ 找不到字段 {name}"); return; }
        try { f.SetValue(obj, val); } catch (Exception ex) { Console.WriteLine($"      ⚠ 设置字段 {name} 失败: {ex.Message}"); }
    }

    static void WriteConfig(bool disableAll)
    {
        var src = File.ReadAllText(Path.Combine(SrcDir, "config.json"));
        var o = JsonNode.Parse(src).AsObject();
        o["disableAllEvents"] = disableAll;
        File.WriteAllText(Path.Combine(_modDir, "config.json"), o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>跨 SPTarkov.* 程序集找类型（BossLocationSpawn 等不一定在 Core 里）。</summary>
    static Type SptAny(string name)
    {
        var t = _spt.GetTypes().FirstOrDefault(x => x.Name == name);
        if (t != null) return t;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!a.GetName().Name.StartsWith("SPTarkov")) continue;
            try { t = a.GetTypes().FirstOrDefault(x => x.Name == name); } catch { }
            if (t != null) return t;
        }
        Console.WriteLine("      ⚠ 找不到类型 " + name);
        return null;
    }

    static void BuildLocationTable()
    {
        if (Environment.GetEnvironmentVariable("DUMP") == "1")
        {
            foreach (var nm in new[] { "LocationTable", "LocationBase", "Location", "LootLocation" })
            {
                var t = SptAny(nm);
                if (t == null) continue;
                Console.WriteLine($"  [{t.FullName}] props: " + string.Join(", ",
                    t.GetProperties().Select(p => p.PropertyType.Name + " " + p.Name).Take(30)));
            }
            var withBoss = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.StartsWith("SPTarkov"))
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                .Where(t => t.GetProperty("BossLocationSpawn") != null).ToList();
            foreach (var t in withBoss) Console.WriteLine("  有 BossLocationSpawn 的类型: " + t.FullName);
            return;
        }
        var ltType = SptAny("LocationTable");
        _locationTable = Activator.CreateInstance(ltType);
        // SPT 4.1.6 的真实形状：LocationTable.Bigmap : Location（Eft.Common）→ .Base : LocationBase
        // ⇒ BossLocationSpawn 在 **LocationBase** 上（与 BossWavePatch 里 loc.Base 的读法一致）
        var locProp = ltType.GetProperties().First(x => x.Name.Equals("Bigmap", StringComparison.OrdinalIgnoreCase));
        var locType = locProp.PropertyType;
        var locObj = Activator.CreateInstance(locType);
        var lbType = SptAny("LocationBase");
        var inner = Activator.CreateInstance(lbType);          // LocationBase
        var blsType = SptAny("BossLocationSpawn");
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(blsType));
        var one = Activator.CreateInstance(blsType);
        Set(one, "BossName", "bossKilla");
        Set(one, "BossChance", 30d);
        Set(one, "BossZone", "ZoneGate");
        Set(one, "BossEscortAmount", "0");
        Set(one, "ShowOnTarkovMap", false);
        list.Add(one);
        Set(inner, "BossLocationSpawn", list);
        Set(inner, "Id", "bigmap");
        Set(locObj, "Base", inner);          // Location.Base = LocationBase
        locProp.SetValue(_locationTable, locObj);
        _bossList = list;
    }

    static void Set(object obj, string name, object val)
    {
        if (obj == null) return;
        var t = obj.GetType();
        var p = t.GetProperties(ANY).FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (p != null && p.CanWrite) { p.SetValue(obj, val); return; }
        var f = t.GetFields(ANY).FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (f != null) f.SetValue(obj, val);
    }

    static double Chance(object bossEntry)
    {
        var p = bossEntry.GetType().GetProperties(ANY).FirstOrDefault(x => x.Name.Equals("BossChance", StringComparison.OrdinalIgnoreCase));
        var v = p?.GetValue(bossEntry);
        return v == null ? -1 : Convert.ToDouble(v);
    }

    static string SeasonOf(object weatherConfig)
        => weatherConfig?.GetType().GetProperties(ANY)
               .FirstOrDefault(x => x.Name.Equals("OverrideSeason", StringComparison.OrdinalIgnoreCase))?.GetValue(weatherConfig)?.ToString();

    static int ReadInt(string path, string key)
    {
        try
        {
            var txt = File.ReadAllText(path);
            var o = JsonNode.Parse(txt, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })?.AsObject();
            return o?[key]?.GetValue<int>() ?? -1;
        }
        catch { return -1; }
    }

    /// <summary>读临时 mod 目录里 config.json 的布尔键。</summary>
    static bool? ReadBool(string key)
    {
        try
        {
            var o = JsonNode.Parse(File.ReadAllText(Path.Combine(_modDir, "config.json")))?.AsObject();
            var n = o?[key];
            return n == null ? (bool?)null : n.GetValue<bool>();
        }
        catch { return null; }
    }

    /// <summary>构造一棵"能跑 PrepareHalloweenNative"的 seasonalevents 模型（失败返回 null，该步只验证日志）。</summary>
    static object BuildSeasonalEventConfig()
    {
        try
        {
            var cfgType = Spt("SeasonalEventConfig");
            var cfg = Activator.CreateInstance(cfgType);
            var evType = cfgType.GetProperties(ANY).First(x => x.Name == "Events").PropertyType
                         .GetGenericArguments()[0];
            var ev = Activator.CreateInstance(evType);
            Set(ev, "Type", Enum.Parse(Spt("SeasonalEventType"), "Halloween"));
            var setType = evType.GetProperties(ANY).First(x => x.Name == "Settings").PropertyType;
            var st = Activator.CreateInstance(setType);
            var zsType = st.GetType().GetProperties(ANY).First(x => x.Name == "ZombieSettings").PropertyType;
            var zs = Activator.CreateInstance(zsType);
            Set(zs, "Enabled", true);
            Set(st, "ZombieSettings", zs);
            Set(ev, "Settings", st);
            var listType = typeof(List<>).MakeGenericType(evType);
            var list = (IList)Activator.CreateInstance(listType);
            list.Add(ev);
            Set(cfg, "Events", list);
            return cfg;
        }
        catch (Exception ex)
        {
            Console.WriteLine("      构造 seasonalevents 模型失败：" + ex.Message);
            return null;
        }
    }

    static bool? ZombieEnabled(object secCfg)
    {
        try
        {
            var events = (IEnumerable)secCfg.GetType().GetProperties(ANY).First(x => x.Name == "Events").GetValue(secCfg);
            foreach (var ev in events)
            {
                var settings = ev.GetType().GetProperties(ANY).First(x => x.Name == "Settings").GetValue(ev);
                if (settings == null) continue;
                var zs = settings.GetType().GetProperties(ANY).First(x => x.Name == "ZombieSettings").GetValue(settings);
                if (zs == null) continue;
                var en = zs.GetType().GetProperties(ANY).First(x => x.Name == "Enabled").GetValue(zs);
                return en == null ? (bool?)null : Convert.ToBoolean(en);
            }
        }
        catch { }
        return null;
    }
}
