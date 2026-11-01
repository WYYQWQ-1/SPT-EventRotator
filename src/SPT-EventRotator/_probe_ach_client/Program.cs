using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using IOPath = System.IO.Path;

internal static class Program
{
    private static readonly string Managed = @"D:\PureEFTv4.1\EFTv4.1\EscapeFromTarkov_Data\Managed";
    private static readonly string AchDir = IOPath.Combine(FindModRoot(), "achievements");

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

    private static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var p = Path.Combine(Managed, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        var asm = Assembly.LoadFrom(Path.Combine(Managed, "Assembly-CSharp.dll"));
        var converters = (JsonConverter[])asm.GetType("EFT.EftJsonConverters")
            .GetField("Converters", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        Console.WriteLine($"客户端 JsonConverter 表 = {converters.Length} 个（含 For Condition 的 PolymorphicConverter / ConditionsDict 适配器）");

        var tplType = asm.GetType("EFT.Achievements.AchievementTemplate");
        int files = 0, totalConds = 0;
        foreach (var file in Directory.GetFiles(AchDir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            files++;
            var arr = (Array)JsonConvert.DeserializeObject(File.ReadAllText(file), tplType.MakeArrayType(), converters);
            foreach (var tpl in arr)
            {
                var id = (string)tplType.GetProperty("Id").GetValue(tpl);
                var hidden = tplType.GetProperty("Hidden").GetValue(tpl);
                var showProgress = tplType.GetProperty("ShowProgress").GetValue(tpl);
                var bar = tplType.GetProperty("ShowProgressBar").GetValue(tpl);
                var rarity = tplType.GetProperty("Rarity").GetValue(tpl);
                var side = tplType.GetProperty("PlayerSide").GetValue(tpl);
                var conds = (IDictionary)tplType.GetProperty("Conditions").GetValue(tpl);

                int n = 0;
                string sample = "";
                var kinds = new System.Collections.Generic.List<string>();
                foreach (DictionaryEntry e in conds)
                {
                    foreach (var c in (IEnumerable)e.Value)
                    {
                        n++; totalConds++;
                        kinds.Add(c.GetType().Name);
                        if (n == 1)
                            sample = $"{c.GetType().Name} target={c.GetType().GetField("target")?.GetValue(c)} status=[{string.Join("|", ((IEnumerable)c.GetType().GetField("statuses").GetValue(c)).Cast<object>())}]";
                    }
                }
                Console.WriteLine($"{Path.GetFileName(file),-26} {id}  conds={n,-3} hidden={hidden,-5} showProgress={showProgress,-5} bar={bar,-5} rarity={rarity,-9} side={side}");
                Console.WriteLine($"   └ 全部条件类型 = {string.Join(",", kinds.Distinct())} ｜ 首条: {sample}");
            }
        }
        Console.WriteLine($"\n合计：{files} 个文件 / {totalConds} 条条件，全部由客户端 ConditionSerializer 实例化为 ConditionQuest（无 abstract Condition 异常）");
        return totalConds == 128 ? 0 : 1;
    }
}
