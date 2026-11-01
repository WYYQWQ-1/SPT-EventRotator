using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using IOPath = System.IO.Path;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;

internal static class Program
{
    private static readonly string SptRoot = @"D:\PureEFTv4.1\EFTv4.1\SPT_Runtime";
    private static readonly string SrcMod = @"C:\Users\32674\OneDrive\Documents\deepseek-harness\default-workspace\fix\SPT-EventRotator";

    private static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var jsonUtil = new JsonUtil(new IJsonConverterRegistrator[] { new SptJsonConverterRegistrator() });

        // ── 真表：本机 achievements.json（数组，53 条） ──
        var achPath = IOPath.Combine(SptRoot, @"SPT_Data\database\templates\achievements.json");
        var baseList = jsonUtil.Deserialize<List<Achievement>>(File.ReadAllText(achPath));
        Console.WriteLine($"① 注入前：成就表 {baseList.Count} 条（{achPath}）");

        // ── 真表：本机 quests.json + 本 mod questlines 里注入的自定义任务 ──
        var questsPath = IOPath.Combine(SptRoot, @"SPT_Data\database\templates\quests.json");
        var quests = jsonUtil.Deserialize<Dictionary<MongoId, Quest>>(File.ReadAllText(questsPath));
        int custom = 0;
        foreach (var f in Directory.GetFiles(IOPath.Combine(SrcMod, "questlines"), "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            if (!doc.RootElement.TryGetProperty("quests", out var qs)) continue;
            foreach (var p in qs.EnumerateObject())
            {
                var id = new MongoId(p.Name);
                if (!quests.ContainsKey(id)) { quests[id] = null!; custom++; }
            }
        }
        Console.WriteLine($"   任务表 {quests.Count} 条（其中本 mod questlines 自定义任务 {custom} 条）");

        // TemplateTable / LocaleTable 都是带 required 成员的 record ⇒ 用 Activator 绕开编译期检查
        var tt = (TemplateTable)Activator.CreateInstance(typeof(TemplateTable))!;
        SetProp(tt, "Achievements", baseList);      // init-only ⇒ 只能反射赋值
        SetProp(tt, "Quests", quests);

        // ── 模拟一个「标准安装结构」，让 LocateSptData 找到 SPT_Data 并真的铺图 ──
        var sim = IOPath.Combine(AppContext.BaseDirectory, "_sim");
        try { if (Directory.Exists(sim)) Directory.Delete(sim, true); }
        catch { sim = IOPath.Combine(AppContext.BaseDirectory, "_sim_" + DateTime.Now.Ticks); }
        var simMod = IOPath.Combine(sim, @"SPT_Runtime\user\mods\SPT-EventRotator");
        CopyDir(IOPath.Combine(SrcMod, "achievements"), IOPath.Combine(simMod, "achievements"));
        CopyDir(IOPath.Combine(SrcMod, "images", "achievement"), IOPath.Combine(simMod, "images", "achievement"));
        var simImg = IOPath.Combine(sim, @"SPT_Runtime\SPT_Data\images\achievement");
        Directory.CreateDirectory(simImg);
        Console.WriteLine($"   （图标铺设走模拟安装结构：{simMod}）");

        // ── 加载真 DLL，走真入口 ──
        var dll = IOPath.Combine(SrcMod, @"bin\Release\net10.0\SPT-EventRotator.dll");
        var modAsm = Assembly.LoadFrom(dll);
        var inj = modAsm.GetType("EventRotator.AchievementInject", true)!;
        inj.GetField("Warn")!.SetValue(null, (Action<string>)(s => Console.WriteLine("   [W] " + s)));
        inj.GetField("Info")!.SetValue(null, (Action<string>)(s => Console.WriteLine("   [I] " + s)));
        var load = inj.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!;

        Console.WriteLine();
        Console.WriteLine("== 第一次 Load() ==");
        load.Invoke(null, new object[] { simMod, tt, jsonUtil });
        int after1 = baseList.Count;
        var ids = (List<string>)inj.GetField("InjectedIds")!.GetValue(null)!;
        Console.WriteLine($"① 注入后：成就表 {after1} 条（53 + {after1 - 53}）  注入 id {ids.Count} 条");

        // ── 本地化：真 LocaleInject.Apply（en/ch 真词典） ──
        var ch = LoadLocale("ch");
        var en = LoadLocale("en");
        var localeTable = (LocaleTable)Activator.CreateInstance(typeof(LocaleTable))!;
        SetProp(localeTable, "Global", new Dictionary<string, LazyLoad<GlobalLocaleDictionary>>(StringComparer.Ordinal)
        {
            ["ch"] = new LazyLoad<GlobalLocaleDictionary>(() => ch, false),
            ["en"] = new LazyLoad<GlobalLocaleDictionary>(() => en, false),
        });
        var li = modAsm.GetType("EventRotator.LocaleInject", true)!;
        li.GetField("Warn")!.SetValue(null, (Action<string>)(s => Console.WriteLine("   [W] " + s)));
        li.GetField("Info")!.SetValue(null, (Action<string>)(s => Console.WriteLine("   [I] " + s)));
        li.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { localeTable });
        // LazyLoad 的 transformer 只在物化时生效 ⇒ 先碰一次 .Value（真服务端由 LocaleService 读它）
        _ = localeTable.Global["ch"].Value;
        _ = localeTable.Global["en"].Value;

        // ── ② 逐条：aid / 中文名 / 条件条数 ──
        var byLine = (Dictionary<string, int>)inj.GetField("ByLine")!.GetValue(null)!;
        var missing = (List<string>)inj.GetField("MissingQuestTargets")!.GetValue(null)!;
        Console.WriteLine();
        Console.WriteLine("② 逐条（任务线 / aid / 条件条数 / 中文名 / 英文名）");
        foreach (var kv in byLine.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var file = IOPath.Combine(SrcMod, "achievements", kv.Key + ".json");
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var aid = doc.RootElement[0].GetProperty("id").GetString();
            var a = baseList.First(x => (string)x.Id == aid);
            ch.TryGetValue(aid + " name", out var cn);
            en.TryGetValue(aid + " name", out var enn);
            Console.WriteLine($"   {kv.Key,-22} {aid}  Q条件={kv.Value,-3} index={a.Index,-6} rarity={a.Rarity,-9} 中文名={cn}  英文名={enn}  {a.ImageUrl}");
        }
        Console.WriteLine($"   条件引用的任务 id 在任务表里：{(missing.Count == 0 ? "全部命中（0 条缺失）" : "缺失 " + missing.Count + " 条：" + string.Join(", ", missing))}");

        // ── ③ 幂等：连跑第二次 ──
        Console.WriteLine();
        Console.WriteLine("== 第二次 Load()（幂等检查） ==");
        load.Invoke(null, new object[] { simMod, tt, jsonUtil });
        Console.WriteLine($"③ 注入后：成就表 {baseList.Count} 条（第一次 {after1}）⇒ "
                        + (baseList.Count == after1 ? "幂等 OK" : "！不幂等"));
        Console.WriteLine($"   注入 id 累计 {((List<string>)inj.GetField("InjectedIds")!.GetValue(null)!).Count} 条（应仍为 {ids.Count}）");

        // ── ④ 图标：复制后的清单 + 路由可解析性（复刻 ImageRouteImporter/ImageRouter 的规则） ──
        Console.WriteLine();
        Console.WriteLine("④ 图标（模拟 SPT_Data/images/achievement）");
        var files = Directory.GetFiles(simImg, "*.png").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"   文件清单（{files.Count} 张）：");
        foreach (var f in files) Console.WriteLine("     " + IOPath.GetFileName(f) + $"  {new FileInfo(f).Length} B");
        var routes = BuildRoutes(IOPath.Combine(sim, @"SPT_Runtime\SPT_Data\images"));
        int ok = 0, miss = 0;
        foreach (var kv in byLine)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(IOPath.Combine(SrcMod, "achievements", kv.Key + ".json")));
            var url = doc.RootElement[0].GetProperty("imageUrl").GetString()!;
            var key = IOPath.ChangeExtension(url, null)!.ToLowerInvariant();   // ImageRouter.CanHandle：去扩展名 + 转小写
            if (routes.ContainsKey(key)) { ok++; Console.WriteLine($"     OK   {url} → {IOPath.GetFileName(routes[key])}"); }
            else { miss++; Console.WriteLine($"     MISS {url}（键 {key}）"); }
        }
        Console.WriteLine($"   路径可解析：{ok} 条 OK / {miss} 条 MISS");

        // ── 回读：反序列化后的关键字段（证明 SPT 的 JsonUtil 认得这些 JSON） ──
        Console.WriteLine();
        Console.WriteLine("⑤ 回读一条注入结果（真 Achievement 模型）");
        var sample = baseList.First(x => (string)x.Id == ids[0]);
        var sc = sample.Conditions.AvailableForFinish;
        Console.WriteLine($"   id={sample.Id} imageUrl={sample.ImageUrl} rarity={sample.Rarity} side={sample.Side} index={sample.Index}");
        Console.WriteLine($"   instantComplete={sample.InstantComplete} showNotificationsInGame={sample.ShowNotificationsInGame} showProgress={sample.ShowProgress} prefab='{sample.prefab}'");
        Console.WriteLine($"   rewards={sample.Rewards?.Count() ?? 0} 条  fail={sample.Conditions.Fail.Count} 条  availableForFinish={sc.Count} 条");
        var c0 = sc[0];
        Console.WriteLine($"   条件[0]: type={c0.ConditionType} target={c0.Target?.Item} status=[{string.Join(",", c0.Status!)}] index={c0.Index} availableAfter={c0.AvailableAfter} dispersion={c0.Dispersion} parentId='{c0.ParentId}'");
        Console.WriteLine($"   条件 index 序列：{string.Join(",", sc.Select(x => x.Index))}");
        Console.WriteLine($"   扩展字段里保留的 name/description："
                        + (sample.ExtensionData != null && sample.ExtensionData.ContainsKey("name") ? sample.ExtensionData["name"] : "（无）"));

        bool all4 = baseList.Where(x => ids.Contains((string)x.Id))
                            .All(x => x.Conditions.AvailableForFinish.All(c => c.Status != null && c.Status.Count == 1
                                    && c.Status.Any(s => Convert.ToInt32(s) == 4)));
        var targets = baseList.Where(x => ids.Contains((string)x.Id))
                              .SelectMany(x => x.Conditions.AvailableForFinish.Select(c => (string)c.Target!.Item))
                              .ToList();
        Console.WriteLine($"   全部条件 status==[4]：{all4}；条件总数 {targets.Count}，去重后 {targets.Distinct().Count()}");

        // 本地化键齐不齐（9 条 × name/description × en/ch）
        int locOk = 0, locBad = 0;
        foreach (var id in ids)
        {
            foreach (var suffix in new[] { " name", " description" })
            {
                foreach (var d in new[] { ch, en })
                {
                    if (d.TryGetValue(id + suffix, out var v) && !string.IsNullOrWhiteSpace(v)) locOk++;
                    else { locBad++; Console.WriteLine($"   ！缺本地化键 {id}{suffix}"); }
                }
            }
        }
        Console.WriteLine($"   本地化键：{locOk} 条命中 / {locBad} 条缺失（应为 9×2×2=36）");
    }

    private static void SetProp(object o, string name, object value)
        => o.GetType().GetProperty(name)!.SetValue(o, value);

    private static GlobalLocaleDictionary LoadLocale(string lang)
    {
        var p = IOPath.Combine(SptRoot, @"SPT_Data\database\locales\global\" + lang + ".json");
        var plain = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p))!;
        var d = new GlobalLocaleDictionary();
        foreach (var kv in plain) d[kv.Key] = kv.Value;
        return d;
    }

    /// <summary>复刻 ImageRouteImporter.CreateRouteMapping 的键规则：`/files` + 相对路径（去扩展名、去首斜杠、反斜杠→斜杠）</summary>
    private static Dictionary<string, string> BuildRoutes(string imagesDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(imagesDir, "*", SearchOption.AllDirectories))
        {
            var rel = IOPath.GetRelativePath(imagesDir, f);
            var key = ("/files/" + IOPath.ChangeExtension(rel, null)!.Replace('\\', '/').TrimStart('/')).ToLowerInvariant();
            map[key] = f;
        }
        return map;
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src, "*.png").Concat(Directory.GetFiles(src, "*.json")))
            File.Copy(f, IOPath.Combine(dst, IOPath.GetFileName(f)), true);
    }
}
