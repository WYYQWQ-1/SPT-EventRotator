// 只读探针：在独立进程里用**真 SPT 模型 + 真 mod DLL** 跑一遍成就注入 / 文案注入，
// 不启动服务端、不写 SPT_Data（把 modPath 指到一个只含 achievements/ 的临时目录，
// 于是 CopyIcons 那步因为 images/achievement 不存在而直接跳过）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;
using IOPath = System.IO.Path;

internal static class Program
{
    private const string Runtime = @"D:\PureEFTv4.1\EFTv4.1\SPT_Runtime";
    private static readonly string ModDir = FindModRoot();

    private static int Main()
    {
        var jsonUtil = new JsonUtil(new IJsonConverterRegistrator[] { new SptJsonConverterRegistrator() });

        // ── 真 TemplateTable：只填 Quests / Achievements 两个 backing field（其它维度不碰） ──
        var table = (TemplateTable)RuntimeHelpers.GetUninitializedObject(typeof(TemplateTable));
        SetField(table, "<Achievements>k__BackingField",
            jsonUtil.DeserializeFromFile<List<Achievement>>(IOPath.Combine(Runtime, "SPT_Data", "database", "templates", "achievements.json")));
        Console.WriteLine($"① 载入本体成就表 = {table.Achievements.Count} 条");
        SetField(table, "<Quests>k__BackingField",
            jsonUtil.DeserializeFromFile<Dictionary<MongoId, Quest>>(IOPath.Combine(Runtime, "SPT_Data", "database", "templates", "quests.json")));
        Console.WriteLine($"   载入本体任务表 = {table.Quests.Count} 条");

        var modAsm = typeof(Program).Assembly.GetReferencedAssemblies().Any(a => a.Name == "SPT-EventRotator")
            ? Assembly.Load("SPT-EventRotator") : null;
        modAsm ??= Assembly.LoadFrom(IOPath.Combine(ModDir, "bin", "Release", "net10.0", "SPT-EventRotator.dll"));
        var inject = modAsm.GetType("EventRotator.AchievementInject", true);
        var locale = modAsm.GetType("EventRotator.LocaleInject", true);

        Action<string> log = s => Console.WriteLine("   " + s);
        inject.GetField("Info", BindingFlags.Public | BindingFlags.Static).SetValue(null, log);
        inject.GetField("Warn", BindingFlags.Public | BindingFlags.Static).SetValue(null, log);
        locale.GetField("Warn", BindingFlags.Public | BindingFlags.Static).SetValue(null, log);
        locale.GetField("Info", BindingFlags.Public | BindingFlags.Static).SetValue(null, log);

        // ── modPath：临时目录，只放 achievements/ 的副本 ⇒ 不碰 SPT_Data ──
        var tmp = IOPath.Combine(AppContext.BaseDirectory, "tmp_mod_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(IOPath.Combine(tmp, "achievements"));
        foreach (var f in Directory.GetFiles(IOPath.Combine(ModDir, "achievements"), "*.json"))
            File.Copy(f, IOPath.Combine(tmp, "achievements", IOPath.GetFileName(f)));

        // ── 复刻 Preload 的真实顺序：先 QuestLineDimension.Load（自定义任务进表），再成就 ──
        var qld = modAsm.GetType("EventRotator.QuestLineDimension", true);
        qld.GetField("Warn", BindingFlags.Public | BindingFlags.Static).SetValue(null, log);
        qld.GetField("Info", BindingFlags.Public | BindingFlags.Static).SetValue(null, log);
        var localeTable0 = (LocaleTable)RuntimeHelpers.GetUninitializedObject(typeof(LocaleTable));
        var global0 = new Dictionary<string, LazyLoad<GlobalLocaleDictionary>>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang0 in new[] { "en", "ch" })
            global0[lang0] = new LazyLoad<GlobalLocaleDictionary>(() => new GlobalLocaleDictionary(), true);
        SetField(localeTable0, "<Global>k__BackingField", global0);
        qld.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
           .Invoke(null, new object[] { ModDir, table, localeTable0, jsonUtil });
        Console.WriteLine($"   任务线注入后任务表 = {table.Quests.Count} 条");

        var load = inject.GetMethod("Load", BindingFlags.Public | BindingFlags.Static);

        // ── ② 第一次注入 ──
        int before = table.Achievements.Count;
        load.Invoke(null, new object[] { tmp, table, jsonUtil });
        Console.WriteLine($"② 注入前 {before} 条 → 注入后 {table.Achievements.Count} 条（+{table.Achievements.Count - before}）");
        var added = table.Achievements.Skip(before).ToList();
        foreach (var a in added)
        {
            var conds = a.Conditions?.AvailableForFinish ?? new List<QuestCondition>();
            Console.WriteLine($"   {a.Id}  index={a.Index,-6} rarity={a.Rarity,-9} conds={conds.Count,-3} " +
                              $"name键=\"{a.Id} name\"  image={a.ImageUrl}  rewards={a.Rewards?.Count() ?? 0}");
        }
        var missing = (System.Collections.IEnumerable)inject.GetField("MissingQuestTargets", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        Console.WriteLine($"   条件里引用但任务表缺失的任务 id = {missing.Cast<object>().Count()} 个");

        // ── ③ 幂等：再跑一次 ──
        int mid = table.Achievements.Count;
        load.Invoke(null, new object[] { tmp, table, jsonUtil });
        Console.WriteLine($"③ 第二次注入后 = {table.Achievements.Count} 条（{mid} → {table.Achievements.Count}，应不变）");

        // ── ④ 文案：真 LocaleTable.Global（LazyLoad + AddTransformer） ──
        var localeTable = (LocaleTable)RuntimeHelpers.GetUninitializedObject(typeof(LocaleTable));
        var global = new Dictionary<string, LazyLoad<GlobalLocaleDictionary>>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in new[] { "en", "ch" })
            global[lang] = new LazyLoad<GlobalLocaleDictionary>(() => new GlobalLocaleDictionary(), true);
        SetField(localeTable, "<Global>k__BackingField", global);

        locale.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { localeTable });

        int total = 0;
        foreach (var lang in new[] { "en", "ch" })
        {
            var dict = localeTable.Global[lang].Value;
            var achKeys = dict.Keys.Where(k => added.Any(a => k.StartsWith((string)a.Id))).OrderBy(k => k).ToList();
            total += achKeys.Count;
            Console.WriteLine($"④ locale[{lang}]：成就相关键 {achKeys.Count} 个（全表 {dict.Count} 键）");
            foreach (var k in achKeys.Take(4))
                Console.WriteLine($"     {k} = {Trim(dict[k])}");
        }
        Console.WriteLine($"   成就文案键合计 = {total}（9 条成就 × (name+description) × 2 语种 = 36）");

        // ── 图标：文件名与 imageUrl 对得上（不复制、只核对） ──
        var imgDir = IOPath.Combine(ModDir, "images", "achievement");
        int iconOk = 0;
        foreach (var a in added)
        {
            var file = IOPath.GetFileName(a.ImageUrl);
            var p = IOPath.Combine(imgDir, file);
            if (!File.Exists(p)) { Console.WriteLine($"    ✘ 图标缺失：{file}"); continue; }
            var head = File.ReadAllBytes(p);
            var isPng = head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47;
            if (isPng) iconOk++;
            else Console.WriteLine($"    ✘ 不是 PNG：{file}");
        }
        Console.WriteLine($"⑤ 图标：{iconOk}/{added.Count} 张存在且是 PNG（路由键 = imageUrl 去扩展名转小写 = /files/achievement/<name>）");

        try { Directory.Delete(tmp, true); } catch { }
        var pass = table.Achievements.Count == before + 9 && table.Achievements.Count == mid && total == 36 && iconOk == added.Count;
        Console.WriteLine(pass ? "\n== 全部通过 ==" : "\n== 有项目未通过 ==");
        return pass ? 0 : 1;
    }

    /// <summary>从输出目录上溯找 mod 根（含 achievements/ 的那级）。</summary>
    private static string FindModRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (Directory.Exists(IOPath.Combine(d.FullName, "achievements"))) return d.FullName;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("找不到 mod 根（含 achievements/ 的目录）");
    }

    private static string Trim(string s) { s = (s ?? "").Replace("\n", " "); return s.Length > 60 ? s.Substring(0, 60) + "…" : s; }

    private static void SetField(object obj, string name, object value)
    {
        var f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new MissingFieldException(obj.GetType().FullName, name);
        f.SetValue(obj, value);
    }
}
