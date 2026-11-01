// ─────────────────────────────────────────────────────────────────────────────
//  ProbeContainerLoot —— `containerLootInject` 离线探针
//
//  目标：在**实机 woods/staticContainers.json 的临时副本**上跑**真身**代码
//        （直接 <Compile Include="../ContainerLootInjectDimension.cs" /> 链接，不是重写一份），断言：
//    ① 注入量 ≈ 容器数 × chance（并单独验证 chance=1 / chance=0 的边界）
//    ② 每个容器最多 1 件注入
//    ③ 注入物尺寸全部适配容器格子（读 items.json 的 _props.Width/Height 与容器 _props.Grids[].cellsH/cellsV）
//    ④ 所有容器 tpl / 注入物 tpl 都能在实机 items.json 里找到
//    ⑤ 还原后重新读 .Value 的序列化结果与注入前**逐字节相同**（transformer 转直通）
//    ⑥ 注入件在数据层结构正确：新 _id 唯一、parentId == Template.Root、slotId=真实grid名、upd.StackObjectsCount=1
//    ⑦ 生成期父节点修复（FixUpGeneratedLoot）：模拟 SPT 把根 _id 换成新 MongoId 后，
//       注入件 parentId 被改指新 Root、Location 置空（交给客户端自动选格）
//    ⑦b ★v2 核心回归：**复现 v1 实机事故** —— 预注入件在生成结果里「消失了」，
//       postfix 必须当场补齐 1 件（parentId=运行期 Root、slotId=真实 grid 名、尺寸适配），
//       且序列化后确实出现在返回给客户端的 JSON 里；预注入件「还在」时只修不补（绝不翻倍）
//    ⑦c slotId 不是常量：把某容器格子改名成 `grid_custom` ⇒ 注入件 slotId 必须跟着变
//    ⑧ 日志逐字格式：`[容器注入] woods 医疗箱：命中 N/M 个容器，注入 K 件`
//    ⑨ 尺寸不符跳过：故意把 4x2 军工电池塞 2x2 夹克 ⇒ 注入 0、日志报"跳过 N 个尺寸不符"
//    ⑩ 非法 tpl：容器/物品 tpl 查不到 ⇒ 整类丢弃 + warning，不抛、不注入
//    ⑪ 连续两次 .Value 不累积（每局新鲜对象）
//
//  ★ 关键建模：实机 `Location.StaticContainers` 是 `LazyLoad<StaticContainerDetails>`，
//    而 `StaticContainerDetails` **没有** `[CacheLazyLoad]` ⇒ `cacheValue = false`
//    （与 LooseLoot 同型）。探针直接用 SPT 真身 `LazyLoad<T>`（构造函数 `cacheValue=false`）。
//
//  只读实机文件；临时副本写到系统 temp / PROBE_TMP，退出时删掉。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EventRotator;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
// ★ 不能整体 using ...Eft.Common.Tables：那里也有一个叫 `Path` 的类型，会和 System.IO.Path 撞车
using ItemLocation = SPTarkov.Server.Core.Models.Eft.Common.Tables.ItemLocation;
using ItemRotation = SPTarkov.Server.Core.Models.Eft.Common.Tables.ItemRotation;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Utils.Json;

internal static class Program
{
    private const string DefaultSpt = @"D:\PureEFTv4.1\EFTv4.1\SPT_Runtime";
    private const string MapKey = "woods";

    private static string ModDir;
    private static int Fail, Pass;

    private static void Check(bool ok, string what, string detail = null)
    {
        if (ok) { Pass++; Console.WriteLine($"  ✅ {what}" + (detail == null ? "" : $"  [{detail}]")); }
        else { Fail++; Console.WriteLine($"  ❌ {what}" + (detail == null ? "" : $"  [{detail}]")); }
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            try
            {
                var name = new AssemblyName(e.Name).Name + ".dll";
                var p = Path.Combine(AppContext.BaseDirectory, name);
                return File.Exists(p) ? System.Reflection.Assembly.LoadFrom(p) : null;
            }
            catch { return null; }
        };

        ModDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var spt = args.Length > 0 ? args[0] : DefaultSpt;
        var srcStatic = Path.Combine(spt, "SPT_Data", "database", "locations", MapKey, "staticContainers.json");
        var itemsPath = Path.Combine(spt, "SPT_Data", "database", "templates", "items.json");
        var eventsPath = Path.Combine(ModDir, "events.json");

        Console.WriteLine("=".PadRight(104, '='));
        Console.WriteLine("containerLootInject 离线探针（真身代码 + 实机 staticContainers.json 副本）");
        Console.WriteLine("  mod 目录   : " + ModDir);
        Console.WriteLine("  实机数据   : " + srcStatic);
        foreach (var p in new[] { srcStatic, itemsPath, eventsPath })
            if (!File.Exists(p)) { Console.WriteLine("[FATAL] 缺文件: " + p); return 2; }

        var tmpRoot = Environment.GetEnvironmentVariable("PROBE_TMP");
        if (string.IsNullOrWhiteSpace(tmpRoot)) tmpRoot = Path.GetTempPath();
        var tmpDir = Path.Combine(tmpRoot, "containerloot_probe_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmpDir);
        var tmpStatic = Path.Combine(tmpDir, "staticContainers.json");
        File.Copy(srcStatic, tmpStatic, true);
        var srcHash = Sha256(srcStatic);
        Console.WriteLine($"  临时副本   : {tmpStatic}  ({new FileInfo(tmpStatic).Length:N0} bytes)");
        Console.WriteLine($"  实机文件只读：探针全程不碰 {srcStatic}（副本退出即删）");

        try
        {
            var jsonText = File.ReadAllText(tmpStatic);
            var itemTpls = LoadItemTpls(itemsPath);
            ItemNames.LocaleRoot = spt;                    // ★ v3：日志中文名（探针里显式指向实机 SPT_Runtime）
            Console.WriteLine($"\n[0] items.json 载入：{itemTpls.Count} 个 tpl");

            // 复刻 LazyLoad<StaticContainerDetails>（cacheValue=false）：每次 .Value 都重新反序列化 + 过 transformer
            var lazy = new LazyLoad<StaticContainerDetails>(() => (StaticContainerDetails)R.FromJson(
                typeof(StaticContainerDetails), jsonText, m => Console.WriteLine("  [json] " + m)), false);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var baselineObj = lazy.Value;
            var baseList = baselineObj.StaticContainers.ToList();
            Console.WriteLine($"[0] 首次读 .Value（反序列化）：{sw.ElapsedMilliseconds} ms，容器 {baseList.Count} 个");

            var ev = LoadEvent(eventsPath);
            if (ev == null) { Console.WriteLine("[FATAL] events.json 里没有带 containerLootInject 的活动"); return 2; }
            var opt = ev.ContainerLootInject[MapKey];
            Console.WriteLine($"[0] events.json → '{ev.Id}'，onlyOnMap='{ev.OnlyOnMap}'，chance={opt.Chance}，"
                            + $"categories={string.Join(",", opt.Categories.Keys)}");

            var baselineJson = R.ToJson(baselineObj);
            Console.WriteLine($"[0] 基线序列化：{baselineJson?.Length ?? 0} chars");
            Check(baselineJson != null && baselineJson.Length > 1000, "基线序列化成功");

            // 每个容器 tpl 在 woods 里的数量（实机副本）
            var countByTpl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in baseList)
            {
                var t = RootTplOf(c.Template);
                if (t == null) continue;
                countByTpl[t] = (countByTpl.TryGetValue(t, out var n) ? n : 0) + 1;
            }

            // 载体：多张图共用同一个 LocationTable（不同 mapKey → 不同 LazyLoad，避免 transformer 互相污染）
            // ★ 主图必须登记**同一个** lazy 实例（否则 Apply 给 A 挂 transformer、探针读的是 B ⇒ 永远读不到注入）
            var locs = new Dictionary<string, Location>(StringComparer.OrdinalIgnoreCase)
            {
                [MapKey] = new Location { StaticContainers = lazy },
            };

            var restores = new List<Action>();
            var logLines = new List<string>();
            ContainerLootInjectDimension.LocationTable = new FakeLocationTable(locs);
            ContainerLootInjectDimension.TemplateTable = new FakeTemplateTable { Items = BuildFakeItemTable(itemsPath) };
            ContainerLootInjectDimension.Log = m => { logLines.Add(m); Console.WriteLine("  " + m); };
            ContainerLootInjectDimension.AddRestore = a => restores.Add(a);
            ContainerLootInjectDimension.SeedOverride = 20261024;
            ContainerLootInjectDimension.ResetApplied();

            sw.Restart();
            ContainerLootInjectDimension.Apply(ev);
            Console.WriteLine($"\n[1] Apply 完成：{sw.ElapsedMilliseconds} ms（含自证读取一次 .Value；登记还原闭包 {restores.Count} 个）");

            var injectedObj = lazy.Value;                  // 注入了的那一份（本局真正会用的那种）
            var injList = injectedObj.StaticContainers.ToList();
            Console.WriteLine($"[1] 读 .Value（注入后）：容器 {injList.Count} 个，"
                            + $"Items 总数 {injList.Sum(c => c.Template?.Items?.Count() ?? 0)}，"
                            + $"多物品容器 {injList.Count(c => (c.Template?.Items?.Count() ?? 0) > 1)} 个");

            // 配置规划：容器 tpl → 类别；类别 → 物品池
            var catOfContainer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var itemsOfCat = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var labelOfCat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["jacket"] = "夹克", ["medbox"] = "医疗箱", ["toolbox"] = "工具箱",
                ["safe"] = "保险箱", ["weaponbox"] = "武器箱", ["duffle"] = "旅行包",
                // ★ v3 新增 6 类
                ["crate"] = "木箱/板条箱", ["drawer"] = "抽屉/柜子", ["groundcache"] = "地面藏匿/埋藏桶",
                ["ammobox"] = "弹药箱", ["grenadebox"] = "手雷箱", ["corpse"] = "尸体",
            };
            var stacksOfCat = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in opt.Categories)
                if (kv.Value.Stacks != null) stacksOfCat[kv.Key] = kv.Value.Stacks;
            foreach (var kv in opt.Categories)
            {
                itemsOfCat[kv.Key] = new HashSet<string>(kv.Value.Items.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (var ct in kv.Value.Containers) catOfContainer[ct] = kv.Key;
            }
            var totalByCat = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in catOfContainer)
                if (countByTpl.TryGetValue(kv.Key, out var n)) totalByCat[kv.Value] = (totalByCat.TryGetValue(kv.Value, out var s) ? s : 0) + n;
            Console.WriteLine("[1] 森林各类容器数：" + string.Join("、",
                totalByCat.OrderByDescending(x => x.Value).Select(x => $"{labelOfCat.GetValueOrDefault(x.Key, x.Key)} {x.Value}")));
            Console.WriteLine($"    配置覆盖容器合计：{totalByCat.Values.Sum()} 个（占全图 {baseList.Count} 个 = "
                              + $"{100.0 * totalByCat.Values.Sum() / baseList.Count:0.0}%）");
            var notCovered = countByTpl.Where(kv => !catOfContainer.ContainsKey(kv.Key))
                                       .OrderByDescending(kv => kv.Value).ToList();
            Console.WriteLine("    未归类容器 tpl：" + (notCovered.Count == 0 ? "（无）"
                : string.Join("、", notCovered.Select(kv => $"{kv.Key}×{kv.Value}"))));

            // ── ① / ② 注入量 & 每容器最多 1 件 ─────────────────────────────
            Console.WriteLine("\n[断言 ①②] 注入量 ≈ 容器数 × chance；每容器最多 1 件");
            var injByCat = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var allInjected = new List<(object Data, SptLootItem Item)>();
            int badMulti = 0, unmatched = 0;
            foreach (var c in injList)
            {
                var items = c.Template?.Items?.ToList() ?? new List<SptLootItem>();
                var extra = items.Skip(1).ToList();     // 本体每个静态容器只预置 1 件（容器自身）
                if (extra.Count > 1) badMulti++;
                if (extra.Count == 0) continue;
                var ctpl = RootTplOf(c.Template);
                if (ctpl == null || !catOfContainer.TryGetValue(ctpl, out var cat)) { unmatched++; continue; }
                foreach (var it in extra) allInjected.Add((c, it));
                injByCat[cat] = (injByCat.TryGetValue(cat, out var n) ? n : 0) + extra.Count;
            }
            int injTotal = allInjected.Count;
            Check(badMulti == 0, "没有任何容器被塞超过 1 件", $"违规 {badMulti} 个");
            Check(unmatched == 0, "所有注入都落在配置的容器类别里", $"未归类 {unmatched} 个");
            Check(injTotal > 0, "确实注入了物品", $"{injTotal} 件");

            double chance = opt.Chance ?? 0.25;
            int totalCfg = totalByCat.Values.Sum();
            double expected = totalCfg * chance;
            double sigma = Math.Sqrt(totalCfg * chance * (1 - chance));
            Check(Math.Abs(injTotal - expected) <= 4 * sigma + 2,
                  $"注入量 {injTotal} ≈ 容器 {totalCfg} × chance {chance:0.##} = {expected:0.0}",
                  $"4σ 容差 ±{(4 * sigma + 2):0.0}");

            foreach (var kv in totalByCat.OrderByDescending(x => x.Value))
            {
                int got = injByCat.TryGetValue(kv.Key, out var n) ? n : 0;
                string label = labelOfCat.GetValueOrDefault(kv.Key, kv.Key);
                double exp = kv.Value * chance;
                bool ok = kv.Value >= 10
                    ? Math.Abs(got - exp) <= 4 * Math.Sqrt(kv.Value * chance * (1 - chance)) + 2
                    : got <= kv.Value;
                Check(ok, $"{label}：{got} / {kv.Value} 个容器命中（期望 {exp:0.0}）");
            }

            // ── ③ / ④ / ⑥ 每件的结构 / 尺寸 / tpl ─────────────────────────────
            Console.WriteLine("\n[断言 ③④⑥] 注入件结构 / 尺寸 / tpl 存在性");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int badId = 0, dupId = 0, badParent = 0, badSlot = 0, badStack = 0, badSize = 0, badTpl = 0, badCat = 0;
            var sizeDetail = new List<string>();
            var stackDetail = new List<string>();
            foreach (var (data, it) in allInjected)
            {
                var tmpl = (StaticContainerData)data;
                var id = it.Id.ToString();
                if (!IsHex24(id)) badId++;
                if (!ids.Add(id)) dupId++;
                if (!string.Equals(it.ParentId, tmpl.Template.Root, StringComparison.OrdinalIgnoreCase)) badParent++;
                var ctpl0 = RootTplOf(tmpl.Template);
                var tpl0 = it.Template.ToString();
                var expectGrid = ctpl0 != null && itemTpls.ContainsKey(tpl0)
                    ? ExpectedGrid(ctpl0, itemTpls[tpl0].W, itemTpls[tpl0].H, itemTpls)
                    : null;
                if (expectGrid == null || !string.Equals(it.SlotId, expectGrid, StringComparison.OrdinalIgnoreCase)) badSlot++;

                var tpl = it.Template.ToString();
                if (!itemTpls.ContainsKey(tpl)) { badTpl++; continue; }
                var ctpl = RootTplOf(tmpl.Template);
                if (ctpl == null || !catOfContainer.TryGetValue(ctpl, out var cat)) { badCat++; continue; }
                if (!itemsOfCat[cat].Contains(tpl)) badCat++;
                // ★ v3：可堆叠物（弹药/卢布）按 `categories.*.stacks` 校验，其余必须为 1
                double expectStack = 1.0;
                if (stacksOfCat.TryGetValue(cat, out var st) && st.TryGetValue(tpl, out var sv)) expectStack = sv;
                if (it.Upd?.StackObjectsCount != expectStack)
                {
                    badStack++;
                    if (stackDetail.Count < 5) stackDetail.Add($"{tpl}: 实际 {it.Upd?.StackObjectsCount} 期望 {expectStack}");
                }
                var info = itemTpls[tpl];
                int w = info.W, h = info.H;
                var grids = GridsOf(ctpl, itemTpls);
                bool fit = grids.Any(g => (w <= g.H && h <= g.V) || (w <= g.V && h <= g.H));
                if (!fit) { badSize++; if (sizeDetail.Count < 5) sizeDetail.Add($"{tpl} {w}x{h} → {ctpl} {string.Join("/", grids)}"); }
            }
            Check(badId == 0, "注入件 _id 全为 24 位 hex", $"违规 {badId}");
            Check(dupId == 0, "注入件 _id 全表唯一", $"重复 {dupId}");
            Check(badParent == 0, "注入件 parentId == 容器 Template.Root（数据层不悬空）", $"违规 {badParent}");
            Check(badSlot == 0, "注入件 slotId == 该容器真实 grid 名（不是硬编码 main）", $"违规 {badSlot}");
            Check(badStack == 0, "注入件 upd.StackObjectsCount 正确（普通=1；弹药/卢布=配置的 stacks）",
                  $"违规 {badStack} {string.Join("; ", stackDetail)}");
            Check(badTpl == 0, "所有注入物 tpl 存在于实机 items.json", $"缺失 {badTpl}");
            Check(badCat == 0, "注入物都来自该容器所属类别的物品池（不是所有容器都出钥匙卡）", $"串类 {badCat}");
            Check(badSize == 0, "所有注入物尺寸适配容器格子（允许旋转）", $"越界 {badSize} {string.Join("; ", sizeDetail)}");

            // 打印注入分布
            Console.WriteLine("\n[实测] 注入 tpl 分布");
            foreach (var g in allInjected.GroupBy(x => x.Item.Template.ToString())
                                        .OrderByDescending(g => g.Count()))
                Console.WriteLine($"      {g.Key}  ×{g.Count()}");

            // ── ⑦ 生成期父节点修复（模拟 SPT 重写 Root）──────────────────────
            Console.WriteLine("\n[断言 ⑦] 生成期父节点修复：把根 _id 换成新 MongoId 后，注入件 parentId 改指新 Root");
            try
            {
                var sample = allInjected.FirstOrDefault(x => x.Data is StaticContainerData);
                if (sample.Data == null) { Check(false, "没有可用的注入样本"); }
                else
                {
                    var srcData = (StaticContainerData)sample.Data;
                    var clone = (SpawnpointTemplate)R.FromJson(typeof(SpawnpointTemplate), R.ToJson(srcData.Template));
                    var oldRoot = clone.Root;
                    var newRoot = Guid.NewGuid().ToString("N").Substring(0, 24);
                    clone.Root = newRoot;
                    clone.Items.First().Id = new MongoId(newRoot);      // SPT 只改根物品 _id
                    var mine = sample.Item.Id.ToString();
                    ContainerLootInjectDimension.FixUpGeneratedLoot(MapKey, new List<SpawnpointTemplate> { clone });
                    var fixedItem = clone.Items.FirstOrDefault(x => string.Equals(x.Id.ToString(), mine, StringComparison.OrdinalIgnoreCase));
                    Check(fixedItem != null && string.Equals(fixedItem.ParentId, newRoot, StringComparison.OrdinalIgnoreCase),
                          "注入件 parentId 已改指运行期新 Root", $"旧 {oldRoot} → 新 {newRoot}");
                    Check(fixedItem != null && LocOk(fixedItem, RootTplOf(clone), clone.Items, itemTpls, out _),
                          "注入件 location 合法（v3：算得出就给显式 {x,y,r}，算不出才 null）",
                          fixedItem == null ? "找不到注入件" : (fixedItem.Location == null ? "null" : "显式坐标"));
                }
            }
            catch (Exception ex) { Check(false, "生成期修复测试异常：" + ex.GetType().Name + ": " + ex.Message); }

            // ══════════════════════════════════════════════════════════════════
            //  ⑦b ★v2 核心回归：复现 v1 实机事故（预注入件在生成结果里消失）
            // ══════════════════════════════════════════════════════════════════
            Console.WriteLine("\n[断言 ⑦b] v1 事故复现：预注入件从生成结果里消失 ⇒ postfix 必须当场补齐");
            Console.WriteLine("          （实机 spt20261024.log：chance=0.25 与 chance=1.0 两局都是 `A total of: 1822`");
            Console.WriteLine("            ⇒ 注入件根本没进 __result；v1 的 postfix 当时 fixedN=0/notSpawned=0 全程静默）");
            try
            {
                // 每个类别取 1 个容器做样本
                var samples = new List<(string Cat, StaticContainerData Data, SptLootItem Item)>();
                foreach (var (data, it) in allInjected)
                {
                    var dd = (StaticContainerData)data;
                    var ct = RootTplOf(dd.Template);
                    if (ct == null || !catOfContainer.TryGetValue(ct, out var cat)) continue;
                    if (samples.Any(s => s.Cat == cat)) continue;
                    samples.Add((cat, dd, it));
                }
                Check(samples.Count == catOfContainer.Values.Distinct().Count(),
                      $"每个类别各取到 1 个样本容器", $"{samples.Count}/{catOfContainer.Values.Distinct().Count()}");
                Console.WriteLine("      样本：" + string.Join("、", samples.Select(s =>
                    $"{labelOfCat.GetValueOrDefault(s.Cat, s.Cat)}/{RootTplOf(s.Data.Template)}")));

                int lostRepaired = 0, keptNotDoubled = 0, lostNoReg = 0, badAfter = 0;
                var detail = new List<string>();

                foreach (var s in samples)
                {
                    var ctpl = RootTplOf(s.Data.Template);
                    var myTpl = s.Item.Template.ToString();
                    var myId = s.Item.Id.ToString();
                    var catPool = itemsOfCat[s.Cat];

                    // ── 场景 A：v1 事故现场（生成结果里没有我们的预注入件）
                    var genA = MakeGenerated(s.Data, dropIds: new[] { myId }, out var newRootA);
                    var baseCountA = genA.Items.Count();
                    ContainerLootInjectDimension.FixUpGeneratedLoot(MapKey, new List<SpawnpointTemplate> { genA });
                    var itemsA = genA.Items.ToList();
                    var addedA = itemsA.Skip(baseCountA).ToList();
                    // ★ 补齐是「重新按类别池掷一次」，所以 tpl 可以与原预注入件不同 —— 只要求在新池里
                    string addedTpl = addedA.Count == 1 ? addedA[0].Template.ToString() : null;
                    string expectGridA = addedTpl != null && itemTpls.ContainsKey(addedTpl)
                        ? ExpectedGrid(ctpl, itemTpls[addedTpl].W, itemTpls[addedTpl].H, itemTpls)
                        : null;
                    bool okA = addedA.Count == 1
                               && addedTpl != null && catPool.Contains(addedTpl)
                               && string.Equals(addedA[0].ParentId, newRootA, StringComparison.OrdinalIgnoreCase)
                               && expectGridA != null
                               && string.Equals(addedA[0].SlotId, expectGridA, StringComparison.OrdinalIgnoreCase)
                               && LocOk(addedA[0], ctpl, genA.Items, itemTpls, out _)
                               && addedA[0].Upd?.StackObjectsCount == ((stacksOfCat.TryGetValue(s.Cat, out var stA)
                                       && stA.TryGetValue(addedTpl, out var svA)) ? svA : 1.0)
                               && genA.Items.First().Id.ToString() == newRootA;      // 根物品没被动
                    if (!okA)
                    {
                        badAfter++;
                        detail.Add($"{ctpl}: 补齐失败 added={addedA.Count} tpl={addedTpl} grid={addedA.FirstOrDefault()?.SlotId}/{expectGridA} "
                                 + $"parent={(addedA.FirstOrDefault()?.ParentId == newRootA)} root={genA.Items.First().Id}");
                    }
                    else
                    {
                        lostRepaired++;
                        // 尺寸必须塞得进真实格子
                        var gi = itemTpls[addedTpl];
                        var gs = GridsOf(ctpl, itemTpls);
                        bool fit = gs.Any(g => (gi.W <= g.H && gi.H <= g.V) || (gi.W <= g.V && gi.H <= g.H));
                        if (!fit) { badAfter++; detail.Add($"{ctpl}: 补齐件 {addedTpl} {gi.W}x{gi.H} 塞不进"); }
                        // ★ 关键：序列化后必须真的出现在「返回给客户端的那份 JSON」里
                        var jsonA = R.ToJson(genA) ?? "";
                        var okJson = jsonA.Contains("\"parentId\":\"" + newRootA + "\"", StringComparison.OrdinalIgnoreCase)
                                     && jsonA.Contains("\"slotId\":\"" + expectGridA + "\"", StringComparison.OrdinalIgnoreCase)
                                     && (jsonA.Contains("\"location\":null", StringComparison.OrdinalIgnoreCase)
                                         || Regex.IsMatch(jsonA, "\"location\":\\{[^}]*\"x\""));
                        if (!okJson) { badAfter++; detail.Add($"{ctpl}: 序列化后找不到 parentId/slotId/location"); }
                    }

                    // ── 场景 B：预注入件还在 ⇒ 只修不补（绝不翻倍）
                    var expectGrid = ExpectedGrid(ctpl, itemTpls[myTpl].W, itemTpls[myTpl].H, itemTpls);
                    if (expectGrid == null) { badAfter++; detail.Add($"{ctpl}: 取不到期望 grid"); continue; }
                    var genB = MakeGenerated(s.Data, dropIds: Array.Empty<string>(), out var newRootB);
                    var baseCountB = genB.Items.Count();
                    ContainerLootInjectDimension.FixUpGeneratedLoot(MapKey, new List<SpawnpointTemplate> { genB });
                    var itemsB = genB.Items.ToList();
                    var mineB = itemsB.FirstOrDefault(x => string.Equals(x.Id.ToString(), myId, StringComparison.OrdinalIgnoreCase));
                    bool okB = itemsB.Count == baseCountB                       // 没有多出第二件
                               && mineB != null
                               && string.Equals(mineB.ParentId, newRootB, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(mineB.SlotId, expectGrid, StringComparison.OrdinalIgnoreCase)
                               && LocOk(mineB, ctpl, itemsB, itemTpls, out _);
                    if (!okB) { badAfter++; detail.Add($"{ctpl}: 只修不补失败 count {baseCountB}→{itemsB.Count}"); }
                    else keptNotDoubled++;

                    // ── 场景 C：transformer 压根没跑（登记表里没有这个容器实例 Id）⇒ 也必须补注入
                    var genC = MakeGenerated(s.Data, dropIds: new[] { myId }, out var newRootC);
                    genC.Id = "container_never_registered_" + s.Cat;      // 让认领一定失败
                    var baseCountC = genC.Items.Count();
                    ContainerLootInjectDimension.FixUpGeneratedLoot(MapKey, new List<SpawnpointTemplate> { genC });
                    var itemsC = genC.Items.ToList();
                    var addedC = itemsC.Skip(baseCountC).ToList();
                    string addedTplC = addedC.Count == 1 ? addedC[0].Template.ToString() : null;
                    string expectGridC = addedTplC != null && itemTpls.ContainsKey(addedTplC)
                        ? ExpectedGrid(ctpl, itemTpls[addedTplC].W, itemTpls[addedTplC].H, itemTpls) : null;
                    bool okC = addedC.Count == 1
                               && addedTplC != null && catPool.Contains(addedTplC)
                               && string.Equals(addedC[0].ParentId, newRootC, StringComparison.OrdinalIgnoreCase)
                               && expectGridC != null
                               && string.Equals(addedC[0].SlotId, expectGridC, StringComparison.OrdinalIgnoreCase)
                               && LocOk(addedC[0], ctpl, genC.Items, itemTpls, out _);
                    if (!okC) { badAfter++; detail.Add($"{ctpl}: 无登记补注入失败 added={addedC.Count}"); }
                    else lostNoReg++;
                }

                int catN = catOfContainer.Values.Distinct().Count();
                Check(lostRepaired == catN, "A) 预注入件丢失 ⇒ 每个容器都被补注入 1 件（v1 事故已修）", $"{lostRepaired}/{catN}");
                Check(keptNotDoubled == catN, "B) 预注入件仍在 ⇒ 只修不补，每容器仍只有 1 件（不翻倍）", $"{keptNotDoubled}/{catN}");
                Check(lostNoReg == catN, "C) transformer 压根没跑（登记表无此容器）⇒ 仍然补注入 1 件", $"{lostNoReg}/{catN}");
                Check(badAfter == 0, "补齐件结构全部合格（parentId=运行期Root / slotId=真实grid / location 合法 / 尺寸适配 / 已在JSON里）",
                      string.Join("; ", detail.Take(6)));

                // ── 场景 D：本图没有注入计划 ⇒ 一件不动；只打一次「钩子已到达」，之后完全静默
                var beforeD = logLines.Count;
                var genD = MakeGenerated(samples[0].Data, dropIds: new[] { samples[0].Item.Id.ToString() }, out var _);
                var countD = genD.Items.Count();
                ContainerLootInjectDimension.FixUpGeneratedLoot("bigmap", new List<SpawnpointTemplate> { genD });
                Check(genD.Items.Count() == countD, "D) 没有注入计划的地图（bigmap）⇒ 一件不动", $"{countD} → {genD.Items.Count()}");
                var dLines = logLines.Skip(beforeD).ToList();
                Check(dLines.Count == 1 && dLines[0].Contains("生成钩子已到达") && dLines[0].Contains("map=bigmap"),
                      "D) 只打一次「生成钩子已到达」（证明每张图 postfix 都真跑了）", dLines.FirstOrDefault());

                var beforeD2 = logLines.Count;
                var genD2 = MakeGenerated(samples[0].Data, dropIds: new[] { samples[0].Item.Id.ToString() }, out var _);
                ContainerLootInjectDimension.FixUpGeneratedLoot("bigmap", new List<SpawnpointTemplate> { genD2 });
                Check(logLines.Count == beforeD2, "D) 第二次生成完全静默（不刷屏）", $"{logLines.Count - beforeD2} 行");
            }
            catch (Exception ex) { Check(false, "⑦b 测试异常：" + ex.GetType().Name + ": " + ex.Message); }

            // ── ⑦c slotId 不是常量 ──────────────────────────────────────────
            Console.WriteLine("\n[断言 ⑦c] slotId 跟真实 grid 名走：把容器格子改名 grid_custom ⇒ 注入件 slotId 也变");
            try
            {
                const string key = "woods_probe_gridname";
                var renamedTpl = "578f8778245977358849a9b5";        // Jacket
                var savedTable = ContainerLootInjectDimension.TemplateTable;
                var lz = new LazyLoad<StaticContainerDetails>(() => (StaticContainerDetails)R.FromJson(
                    typeof(StaticContainerDetails), jsonText, null), false);
                locs[key] = new Location { StaticContainers = lz };

                var table = BuildFakeItemTable(itemsPath);
                RenameGrids(table, renamedTpl, "grid_custom");
                // ★ 必须包一层 FakeTemplateTable：维度是读 `TemplateTable.Items` 这个属性
                ContainerLootInjectDimension.TemplateTable = new FakeTemplateTable { Items = table };

                ContainerLootInjectDimension.Apply(NewEvent(key, 1.0, "jacket",
                    new List<string> { renamedTpl }, new Dictionary<string, int> { ["5d235b4d86f7742e017bc88a"] = 1 }));

                var list = lz.Value.StaticContainers.ToList();
                int jackets = 0; string gotSlot = null; int bad = 0;
                foreach (var c in list)
                {
                    if (!string.Equals(RootTplOf(c.Template), renamedTpl, StringComparison.OrdinalIgnoreCase)) continue;
                    jackets++;
                    foreach (var it in c.Template.Items.Skip(1))
                    {
                        gotSlot = it.SlotId;
                        if (!string.Equals(it.SlotId, "grid_custom", StringComparison.OrdinalIgnoreCase)) bad++;
                    }
                }
                Check(jackets > 0 && bad == 0 && string.Equals(gotSlot, "grid_custom", StringComparison.OrdinalIgnoreCase),
                      $"格子改名后 slotId 跟着改成 grid_custom（夹克 {jackets} 个，违规 {bad}）", gotSlot);

                // 顺带把 12 个配置容器 tpl 的真实 grid 名当证据打印出来
                Console.WriteLine("      [证据] 本活动 12 个容器 tpl 的真实 `_props.Grids[]._name`：");
                foreach (var t in catOfContainer.Keys.OrderBy(x => x))
                {
                    var info = itemTpls.TryGetValue(t, out var ii) ? ii : null;
                    var names = info == null ? "(缺失)" : string.Join("/", info.GridNames.Select(n => n ?? "(null)"));
                    var sizes = info == null ? "-" : string.Join("/", info.Grids.Select(g => $"{g.H}x{g.V}"));
                    Console.WriteLine($"        {t}  {(info == null ? "?" : info.Name),-22} grid={names,-12} cells={sizes}");
                }

                ContainerLootInjectDimension.TemplateTable = savedTable;   // ★ 复原，别污染后面的用例
            }
            catch (Exception ex) { Check(false, "⑦c 测试异常：" + ex.GetType().Name + ": " + ex.Message); }

            // ── ⑦d 真实 `cloner.Clone`（FastCloner）会不会把预注入件搞丢 ─────────
            Console.WriteLine("\n[断言 ⑦d] 用**实机 SPT 自己的** FastCloner 克隆预注入容器：注入件必须原样活着");
            Console.WriteLine("          （`LocationLootGenerator.AddLootToContainer` 第一句就是 `cloner.Clone(staticContainer)`）");
            try
            {
                var cloner = new SPTarkov.Server.Core.Utils.Cloners.FastCloner();
                int ok = 0, bad = 0;
                var why = new List<string>();
                foreach (var (data, it) in allInjected.Take(40))
                {
                    var dd = (StaticContainerData)data;
                    var before = dd.Template.Items.Count();
                    var clone = cloner.Clone(dd);
                    var after = clone?.Template?.Items?.Count() ?? -1;
                    var mine = clone?.Template?.Items?.FirstOrDefault(
                        x => string.Equals(x.Id.ToString(), it.Id.ToString(), StringComparison.OrdinalIgnoreCase));
                    var ctF = RootTplOf(dd.Template);
                    double expStackF = 1.0;
                    if (ctF != null && catOfContainer.TryGetValue(ctF, out var catF)
                        && stacksOfCat.TryGetValue(catF, out var stF)
                        && stF.TryGetValue(it.Template.ToString(), out var svF)) expStackF = svF;
                    bool good = after == before
                                && mine != null
                                && string.Equals(mine.Template.ToString(), it.Template.ToString(), StringComparison.OrdinalIgnoreCase)
                                && string.Equals(mine.ParentId, it.ParentId, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(mine.SlotId, it.SlotId, StringComparison.OrdinalIgnoreCase)
                                && mine.Upd?.StackObjectsCount == expStackF;
                    if (good) ok++;
                    else { bad++; if (why.Count < 3) why.Add($"{RootTplOf(dd.Template)}: {before}→{after} mine={(mine != null)}"); }
                }
                Check(bad == 0, $"FastCloner 克隆后预注入件原样保留（{ok} 个容器）", string.Join("; ", why));
            }
            catch (Exception ex) { Check(false, "⑦d 测试异常：" + ex.GetType().Name + ": " + ex.Message); }

            // ── ⑪ 不累积 ────────────────────────────────────────────────────
            Console.WriteLine("\n[断言 ⑪] transformer 每局在新鲜对象上跑 ⇒ 连续两次 .Value 注入量一致（不累积）");
            var again = lazy.Value.StaticContainers.ToList();
            int againExtra = 0;
            foreach (var c in again) againExtra += Math.Max(0, (c.Template?.Items?.Count() ?? 0) - 1);
            Check(againExtra == injTotal, $"第二次读 .Value 注入量仍为 {injTotal}（实际 {againExtra}），不是 2 倍");

            // ── ①b chance=1 / chance=0 ──────────────────────────────────────
            Console.WriteLine("\n[断言 ①b] 边界：chance=1 ⇒ 每个匹配容器都 +1；chance=0 ⇒ 完全不动");
            RunChanceTest(locs, jsonText, itemTpls, logLines, restores, 1.0);
            RunChanceTest(locs, jsonText, itemTpls, logLines, restores, 0.0);

            // ── ⑨ 尺寸不符跳过 ──────────────────────────────────────────────
            Console.WriteLine("\n[断言 ⑨] 尺寸过滤：4x2 军工电池塞 2x2 夹克 ⇒ 注入 0、日志报跳过");
            RunSizeSkipTest(locs, jsonText, itemTpls, logLines, restores);

            // ── ⑩ 非法 tpl ──────────────────────────────────────────────────
            Console.WriteLine("\n[断言 ⑩] 非法 tpl：容器/物品 tpl 查不到 ⇒ 整类丢弃 + warning，不抛");
            RunBadTplTest(locs, jsonText, itemTpls, logLines, restores);

            // ── ⑧ 日志格式 ──────────────────────────────────────────────────
            Console.WriteLine("\n[断言 ⑧] 日志逐字格式：`[容器注入] woods 医疗箱：命中 N/M 个容器，注入 K 件`");
            var medLine = logLines.FirstOrDefault(l => Regex.IsMatch(l, @"\[容器注入\] woods 医疗箱：命中 \d+/\d+ 个容器，注入 \d+ 件"));
            Check(medLine != null, "医疗箱日志格式逐字匹配", medLine);
            int catLines = logLines.Count(l => Regex.IsMatch(l, @"\[容器注入\] woods (夹克|医疗箱|工具箱|保险箱|武器箱|旅行包)：命中 \d+/\d+ 个容器，注入 \d+ 件"));
            Check(catLines >= 6, "老六类各一行统计（兼容旧格式）", $"{catLines} 行");

            // ── ⑫ v2 新增诊断日志（实机排查用，逐字格式）────────────────────
            Console.WriteLine("\n[断言 ⑫] v2 诊断日志：`生成钩子已到达` + `本局容器 N 个，含注入物件的容器 M 个，改 parentId K 件`");
            var arriveLine = logLines.FirstOrDefault(l => l.Contains("生成钩子已到达") && l.Contains("map=woods"));
            Check(arriveLine != null, "每次生成都会打「生成钩子已到达」（证明 postfix 真跑了）", arriveLine);
            var raidLine = logLines.FirstOrDefault(l =>
                Regex.IsMatch(l, @"\[容器注入\] woods：本局容器 \d+ 个，含注入物件的容器 \d+ 个，改 parentId \d+ 件；补齐补注入 \d+ 件"));
            Check(raidLine != null, "每局一行「容器/含注入物件容器/改 parentId」诊断（v1 就是这里静默）", raidLine);
            var lostLine = logLines.FirstOrDefault(l => l.Contains("预注入件登记了") && l.Contains("找不到"));
            Check(lostLine != null, "能明确指出「预注入件登记了但生成结果里找不到我们的件」（v1 丢件的正是这一类）", lostLine);

            // ══════════════════════════════════════════════════════════════════
            //  ⑬ ★v3：覆盖率 / 注入明细 / 样例 / 显式 location
            // ══════════════════════════════════════════════════════════════════
            Console.WriteLine("\n[断言 ⑬] v3：白名单覆盖率 / 注入明细日志 / 样例 / 显式 location（模拟一整局 429 个容器）");
            try
            {
                int coveredCnt = catOfContainer.Keys.Sum(t => countByTpl.TryGetValue(t, out var n) ? n : 0);
                Check(coveredCnt * 10 >= baseList.Count * 9,
                      $"白名单覆盖率 ≥90%（{coveredCnt}/{baseList.Count} = {100.0 * coveredCnt / baseList.Count:0.0}%）");
                Check(notCovered.Count <= 3, "未归类的容器 tpl 只剩少数（按需求「先列出、不强行归类」）",
                      string.Join("、", notCovered.Select(kv => $"{kv.Key}×{kv.Value}")));

                int distinctCats = catOfContainer.Values.Distinct().Count();
                var labels = string.Join("|", labelOfCat.Values);
                int allCatLines = logLines.Count(l => Regex.IsMatch(l,
                    @"\[容器注入\] woods (" + labels + @")：命中 \d+/\d+ 个容器，注入 \d+ 件"));
                Check(allCatLines >= distinctCats, $"每个类别各一行统计（共 {distinctCats} 类）", $"{allCatLines} 行");

                var covLine = logLines.FirstOrDefault(l => Regex.IsMatch(l,
                    @"\[容器注入\] woods：容器覆盖率 \d+/\d+ 属于注入类别（\d+\.\d%）"));
                Check(covLine != null, "打出一行「容器覆盖率 X/Y 属于注入类别（Z%）+ 未归类 tpl」", covLine);

                // ── 模拟一整局：429 个容器（每个都带着我们的预注入件 + 1 件官方战利品）全过一遍 postfix
                var fresh = lazy.Value.StaticContainers.ToList();     // 刚读一次：_injected 登记的就是这一份的 id
                var raid = new List<SpawnpointTemplate>();
                foreach (var c in fresh) raid.Add(MakeGenerated(c, Array.Empty<string>(), out var _));
                var beforeV3 = logLines.Count;
                ContainerLootInjectDimension.FixUpGeneratedLoot(MapKey, raid);
                var v3Lines = logLines.Skip(beforeV3).ToList();

                var detailLine = v3Lines.FirstOrDefault(l => Regex.IsMatch(l, @"\[容器注入\] woods：注入明细 .+（共 \d+ 件）$"));
                Check(detailLine != null, "每局一行「注入明细 显卡×12 …（共 N 件）」", detailLine);
                var sampleLine = v3Lines.FirstOrDefault(l => Regex.IsMatch(l,
                    @"\[容器注入\] woods：样例 .+\([0-9a-f]{8}…\) → .+"));
                Check(sampleLine != null, "每局一行「样例 工具箱(5909d50c…) → 显示卡…」（最多 5 对）", sampleLine);
                if (sampleLine != null)
                    Check(sampleLine.Split('；').Length <= 5, "样例最多 5 对「容器 tpl → 注入 tpl」",
                          $"{sampleLine.Split('；').Length} 对");

                int direct = 0, badLoc = 0, overlap = 0, outOfGrid = 0, nullLoc = 0;
                var locWhy = new List<string>();
                foreach (var sp in raid)
                {
                    var ct = RootTplOf(sp);
                    if (ct == null || !catOfContainer.ContainsKey(ct)) continue;
                    AuditPlaced(sp, itemTpls, ref direct, ref badLoc, ref overlap, ref outOfGrid, ref nullLoc, locWhy);
                }
                Check(badLoc == 0 && outOfGrid == 0 && overlap == 0,
                      "全场注入件 location 合法：显式坐标准确落在格子内、互不重叠（null 只出现在真没空格时）",
                      $"直接子物品 {direct}，非法坐标 {badLoc}，越界 {outOfGrid}，重叠 {overlap}，null {nullLoc} {string.Join("; ", locWhy.Take(3))}");

                var jsonRaid = R.ToJson(raid) ?? "";
                Check(jsonRaid.Length > 1000 && Regex.IsMatch(jsonRaid, "\"slotId\":\"main\"")
                      && (jsonRaid.Contains("\"location\":null") || Regex.IsMatch(jsonRaid, "\"location\":\\{[^}]*\"x\"")),
                      "整套 location JSON 可序列化（null 或 {x,y,r} 两种形态都在）",
                      $"len={jsonRaid.Length}");

                var ammoTpl = "6768c25aa7b238f14a08d3f6";      // 7.62x51 M80A1（crate/ammobox 池）
                var ammoHit = raid.SelectMany(sp => sp.Items)
                                  .FirstOrDefault(it => it != null && it.Template.ToString() == ammoTpl);
                Check(ammoHit != null && ammoHit.Upd?.StackObjectsCount == 40,
                      "弹药注入件按 stacks 写 StackObjectsCount（M80A1=40，不是 1 发）",
                      ammoHit == null ? "本局没抽到 M80A1" : $"{ammoHit.Upd?.StackObjectsCount}");

                // ── 格子真满 ⇒ **放弃注入**（把件摘掉，绝不留 location=null 的件）
                const string fullJson = @"{""Id"":""container_probe_full"",""IsContainer"":true,""Root"":""aaaaaaaaaaaaaaaaaaaaaaaa"",
                  ""Items"":[
                    {""_id"":""aaaaaaaaaaaaaaaaaaaaaaaa"",""_tpl"":""578f8778245977358849a9b5""},
                    {""_id"":""b00000000000000000000001"",""_tpl"":""5d235b4d86f7742e017bc88a"",""parentId"":""aaaaaaaaaaaaaaaaaaaaaaaa"",""slotId"":""main"",""location"":{""x"":0,""y"":0,""r"":""Horizontal""}},
                    {""_id"":""b00000000000000000000002"",""_tpl"":""5d235b4d86f7742e017bc88a"",""parentId"":""aaaaaaaaaaaaaaaaaaaaaaaa"",""slotId"":""main"",""location"":{""x"":1,""y"":0,""r"":""Horizontal""}},
                    {""_id"":""b00000000000000000000003"",""_tpl"":""5d235b4d86f7742e017bc88a"",""parentId"":""aaaaaaaaaaaaaaaaaaaaaaaa"",""slotId"":""main"",""location"":{""x"":0,""y"":1,""r"":""Horizontal""}},
                    {""_id"":""b00000000000000000000004"",""_tpl"":""5d235b4d86f7742e017bc88a"",""parentId"":""aaaaaaaaaaaaaaaaaaaaaaaa"",""slotId"":""main"",""location"":{""x"":1,""y"":1,""r"":""Horizontal""}}
                  ]}";
                var full = (SpawnpointTemplate)R.FromJson(typeof(SpawnpointTemplate), fullJson);
                var beforeFull = logLines.Count;
                var nItemsBefore = full.Items.Count();
                ContainerLootInjectDimension.FixUpGeneratedLoot(MapKey, new List<SpawnpointTemplate> { full });
                var fullLines = logLines.Skip(beforeFull).ToList();
                var added = full.Items.Skip(nItemsBefore).ToList();
                Check(added.Count == 0 && full.Items.Count() == nItemsBefore,
                      "2x2 夹克被 4 件 1x1 塞满 ⇒ **放弃注入**（件被摘掉，不是留一件 location=null 混过去）",
                      $"{nItemsBefore} → {full.Items.Count()}");
                Check(fullLines.Any(l => l.Contains("无空位跳过")),
                      "日志明确报出「无空位跳过 N」（实机据此判断是不是没空格被放弃）",
                      fullLines.FirstOrDefault(l => l.Contains("无空位跳过")));
            }
            catch (Exception ex) { Check(false, "⑬ 测试异常：" + ex.GetType().Name + ": " + ex.Message); }

            // ══════════════════════════════════════════════════════════════════
            //  ⑭ ★v3 自算格子（用户验收 ①②③）：4x3+3 件已有物品 / 满格放弃 / 旋转 r=1
            // ══════════════════════════════════════════════════════════════════
            Console.WriteLine("\n[断言 ⑭] v3 自己算格子：①4x3 容器注入 1x2 不重叠且合法 ②满格放弃 ③只剩 2x1 时旋转 r=1");
            try
            {
                const string bagTpl = "578f87a3245977356274f2cb";      // SportBag 4x3
                const string jacketTpl = "578f8778245977358849a9b5";   // Jacket 2x2
                const string salewa = "544fb45d4bdc2dee738b4568";      // Salewa 急救包 1x2（截图里那件）
                const string coin = "5d235b4d86f7742e017bc88a";         // GP 币 1x1

                // ── ① 4x3 容器已有 3 件 1x1（占满第一行）⇒ 注入 1x2 必须落在合法空格且不重叠
                var c1 = RunGridCase(locs, "woods_probe_grid1", bagTpl, salewa, new[]
                {
                    (coin, 0, 0, false), (coin, 1, 0, false), (coin, 2, 0, false),
                });
                // 注入件 = Salewa；原来那几件是 GP 币（tpl 不同，据此区分，别靠下标）
                var mine1 = c1.Items.Where(it => string.Equals(it.Template.ToString(), salewa,
                                                             StringComparison.OrdinalIgnoreCase)).ToList();
                bool ok1 = mine1.Count == 1;
                int x1 = -1, y1 = -1, r1 = -1;
                if (ok1)
                {
                    var (has, xx, yy, rr) = ProbeReadLoc(mine1[0].Location);
                    ok1 = has;
                    x1 = xx; y1 = yy; r1 = rr;
                    if (ok1)
                    {
                        int w = r1 == 1 ? 2 : 1, h = r1 == 1 ? 1 : 2;     // Salewa 1x2
                        ok1 = x1 >= 0 && y1 >= 0 && x1 + w <= 4 && y1 + h <= 3;
                        // 与那 3 件 1x1（y=0 整行）不重叠
                        for (int cy = y1; cy < y1 + h; cy++)
                            for (int cx = x1; cx < x1 + w; cx++)
                                if (cy == 0 && cx <= 2) ok1 = false;
                    }
                }
                Check(ok1, "① 4x3 容器（已有 3 件 1x1 @ (0,0)(1,0)(2,0)）注入 1 件 1x2：落在合法格且**不与已有物品重叠**",
                      mine1.Count == 0 ? "没注入" : $"1x2 @ ({x1},{y1}) r={r1}");
                Check(mine1.Count == 1 && string.Equals(mine1[0].SlotId, "main", StringComparison.OrdinalIgnoreCase),
                      "① 注入件 slotId 仍是该 grid 的 _name（main）", mine1.FirstOrDefault()?.SlotId);

                // ── ② 2x2 夹克塞满 4 件 1x1 ⇒ 放弃注入（件被摘掉，Items 里只剩容器根）
                var c2 = RunGridCase(locs, "woods_probe_grid2", jacketTpl, salewa, new[]
                {
                    (coin, 0, 0, false), (coin, 1, 0, false), (coin, 0, 1, false), (coin, 1, 1, false),
                });
                int n2 = c2.Items.Count();
                Check(n2 == 5, "② 满格：注入件被**放弃**（2x2 夹克只剩容器根 + 4 件原有 1x1 = 5 件）",
                      $"Items={n2}（原有 5 件 + 注入件应被摘掉）");

                // ── ③ 只剩 2x1（第一行空、第二行占满）⇒ 1x2 只能旋转 90° 放下 ⇒ r=1
                var c3 = RunGridCase(locs, "woods_probe_grid3", jacketTpl, salewa, new[]
                {
                    (coin, 0, 1, false), (coin, 1, 1, false),
                });
                var mine3 = c3.Items.Where(it => string.Equals(it.Template.ToString(), salewa,
                                                               StringComparison.OrdinalIgnoreCase)).ToList();
                int x3 = -1, y3 = -1, r3 = -1;
                if (mine3.Count == 1)
                {
                    var (has, xx, yy, rr) = ProbeReadLoc(mine3[0].Location);
                    if (has) { x3 = xx; y3 = yy; r3 = rr; }
                }
                Check(mine3.Count == 1 && x3 == 0 && y3 == 0 && r3 == 1,
                      "③ 1x2 放进只剩 2x1 的位置 ⇒ 旋转 r=1（Vertical）放 (0,0)",
                      mine3.Count == 0 ? "没注入" : $"r={r3} @ ({x3},{y3})");

                // ── ④ 用户指定日志格式：`格子分配：Duffle 4x3 → 注入件(私酒 1x2) 放 (2,1)；跳过 N 个无空位`
                var allocLine = logLines.FirstOrDefault(l => Regex.IsMatch(l,
                    @"\[容器注入\] 格子分配：\S+ \d+x\d+ → 注入件\(\S+ \d+x\d+\) 放 \(\d+,\d+\)"));
                Check(allocLine != null, "④ 日志逐字格式「格子分配：<容器> 4x3 → 注入件(<物品> 1x2) 放 (2,1)」", allocLine);
                Check(allocLine != null && Regex.IsMatch(allocLine, @"；跳过 \d+ 个无空位"),
                      "④ 同一行带「；跳过 N 个无空位」（满格被放弃的数量）", allocLine);
            }
            catch (Exception ex) { Check(false, "⑭ 测试异常：" + ex.GetType().Name + ": " + ex.Message); Console.WriteLine(ex.ToString()); }

            // ── ⑤ 还原逐字节 ────────────────────────────────────────────────
            Console.WriteLine("\n[断言 ⑤] 还原（transformer 转直通）后重新读 .Value，与注入前逐字节相同");
            for (int i = restores.Count - 1; i >= 0; i--) restores[i]();
            var restoredObj = lazy.Value;
            var restoredJson = R.ToJson(restoredObj);
            Check(restoredJson == baselineJson, "还原后 .Value 序列化 == 注入前（逐字节）", $"len {baselineJson?.Length} vs {restoredJson?.Length}");
            Check(restoredObj.StaticContainers.Count() == baseList.Count, "容器数量回到原值",
                  $"{restoredObj.StaticContainers.Count()} vs {baseList.Count}");
            Check(!ReferenceEquals(restoredObj, baselineObj), "还原后确实是新反序列化的对象（证明没有改动被缓存）");

            var restoreFile = Path.Combine(tmpDir, "restored.json");
            var baseFile = Path.Combine(tmpDir, "baseline.json");
            File.WriteAllText(restoreFile, restoredJson ?? "");
            File.WriteAllText(baseFile, baselineJson ?? "");
            Check(new FileInfo(restoreFile).Length == new FileInfo(baseFile).Length
                  && File.ReadAllBytes(restoreFile).SequenceEqual(File.ReadAllBytes(baseFile)),
                  "落盘文件字节级相等", $"{new FileInfo(restoreFile).Length} bytes");

            // ── 实机文件未被改动 ────────────────────────────────────────────
            Console.WriteLine("\n[断言] 实机文件全程只读（SHA-256 前后一致）");
            Check(Sha256(srcStatic) == srcHash, "staticContainers.json 未被写盘", srcHash.Substring(0, 16) + "…");

            Console.WriteLine("\n" + "=".PadRight(104, '='));
            Console.WriteLine($"结果：{Pass} 项通过 / {Fail} 项失败");
            return Fail == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  边界测试
    // ══════════════════════════════════════════════════════════════════════════

    private static void RunChanceTest(Dictionary<string, Location> locs, string jsonText, ItemTable tpls,
                                      List<string> logLines, List<Action> restores, double chance)
    {
        var key = "woods_probe_c" + (int)(chance * 100);
        try
        {
            var lz = new LazyLoad<StaticContainerDetails>(() => (StaticContainerDetails)R.FromJson(
                typeof(StaticContainerDetails), jsonText, null), false);
            locs[key] = new Location { StaticContainers = lz };

            var ev = new EventDef
            {
                Id = "probe_chance_" + chance,
                ContainerLootInject = new Dictionary<string, ContainerLootInjectMapOpt>
                {
                    [key] = new ContainerLootInjectMapOpt
                    {
                        Chance = chance,
                        Categories = new Dictionary<string, ContainerLootCategoryOpt>
                        {
                            ["medbox"] = new()
                            {
                                Containers = new List<string> { "5909d24f86f77466f56e6855", "5d6fe50986f77449d97f7463" },
                                Items = new Dictionary<string, int>
                                {
                                    ["590c657e86f77412b013051d"] = 3,
                                    ["5c0e530286f7747fa1419862"] = 4,
                                }
                            }
                        }
                    }
                }
            };
            ContainerLootInjectDimension.Apply(ev);
            var list = lz.Value.StaticContainers.ToList();
            int medCount = 0, got = 0, bad = 0;
            foreach (var c in list)
            {
                if (!string.Equals(RootTplOf(c.Template), "5909d24f86f77466f56e6855", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(RootTplOf(c.Template), "5d6fe50986f77449d97f7463", StringComparison.OrdinalIgnoreCase)) continue;
                medCount++;
                int extra = Math.Max(0, (c.Template?.Items?.Count() ?? 0) - 1);
                if (extra > 1) bad++;
                got += extra;
            }
            if (chance >= 1.0)
                Check(got == medCount && bad == 0, $"chance=1：医疗箱 {medCount} 个全部 +1 件（实际 {got}）");
            else
                Check(got == 0, $"chance=0：医疗箱 {medCount} 个一件都不加（实际 {got}）");
        }
        catch (Exception ex) { Check(false, $"chance={chance} 测试异常：{ex.GetType().Name}: {ex.Message}"); }
    }

    private static void RunSizeSkipTest(Dictionary<string, Location> locs, string jsonText, ItemTable tpls,
                                        List<string> logLines, List<Action> restores)
    {
        const string key = "woods_probe_size";
        try
        {
            var lz = new LazyLoad<StaticContainerDetails>(() => (StaticContainerDetails)R.FromJson(
                typeof(StaticContainerDetails), jsonText, null), false);
            locs[key] = new Location { StaticContainers = lz };

            var ev = new EventDef
            {
                Id = "probe_size",
                ContainerLootInject = new Dictionary<string, ContainerLootInjectMapOpt>
                {
                    [key] = new ContainerLootInjectMapOpt
                    {
                        Chance = 1.0,
                        Categories = new Dictionary<string, ContainerLootCategoryOpt>
                        {
                            ["jacket"] = new()
                            {
                                Containers = new List<string> { "578f8778245977358849a9b5" },   // 2x2
                                Items = new Dictionary<string, int> { ["5d03794386f77420415576f5"] = 1 }  // 4x2
                            }
                        }
                    }
                }
            };
            var before = logLines.Count;
            ContainerLootInjectDimension.Apply(ev);
            var list = lz.Value.StaticContainers.ToList();
            int jackets = 0, got = 0;
            foreach (var c in list)
            {
                if (!string.Equals(RootTplOf(c.Template), "578f8778245977358849a9b5", StringComparison.OrdinalIgnoreCase)) continue;
                jackets++;
                got += Math.Max(0, (c.Template?.Items?.Count() ?? 0) - 1);
            }
            Check(jackets > 0 && got == 0, $"夹克 {jackets} 个容器、注入 {got} 件（4x2 塞不进 2x2）");
            var line = logLines.Skip(before).FirstOrDefault(l => l.Contains("夹克") && l.Contains("尺寸不符"));
            Check(line != null && line.Contains($"跳过 {jackets} 个尺寸不符"), "日志明确报出尺寸跳过数", line);
        }
        catch (Exception ex) { Check(false, "尺寸过滤测试异常：" + ex.GetType().Name + ": " + ex.Message); }
    }

    private static void RunBadTplTest(Dictionary<string, Location> locs, string jsonText, ItemTable tpls,
                                      List<string> logLines, List<Action> restores)
    {
        const string key = "woods_probe_bad";
        try
        {
            var lz = new LazyLoad<StaticContainerDetails>(() => (StaticContainerDetails)R.FromJson(
                typeof(StaticContainerDetails), jsonText, null), false);
            locs[key] = new Location { StaticContainers = lz };
            var ev = new EventDef
            {
                Id = "probe_bad",
                ContainerLootInject = new Dictionary<string, ContainerLootInjectMapOpt>
                {
                    [key] = new ContainerLootInjectMapOpt
                    {
                        Chance = 1.0,
                        Categories = new Dictionary<string, ContainerLootCategoryOpt>
                        {
                            ["jacket"] = new()
                            {
                                Containers = new List<string> { "aaaaaaaaaaaaaaaaaaaaaaaa" },   // 不存在
                                Items = new Dictionary<string, int> { ["bbbbbbbbbbbbbbbbbbbbbbbb"] = 1 }
                            }
                        }
                    }
                }
            };
            var before = logLines.Count;
            Exception thrown = null;
            try { ContainerLootInjectDimension.Apply(ev); } catch (Exception ex) { thrown = ex; }
            var silent = logLines.Skip(before).Where(l => l.Contains("⚠")).ToList();
            Check(thrown == null, "非法 tpl 不抛异常（降级）");
            Check(silent.Any(l => l.Contains("不在 TemplateTable.Items") || l.Contains("通过校验")),
                  "有明确 warning 说明整类被丢弃", silent.FirstOrDefault());
            Check(lz.Value.StaticContainers.All(c => (c.Template?.Items?.Count() ?? 0) == 1),
                  "非法配置没有向任何容器注入");
        }
        catch (Exception ex) { Check(false, "非法 tpl 测试异常：" + ex.GetType().Name + ": " + ex.Message); }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  数据装载 / 小工具
    // ══════════════════════════════════════════════════════════════════════════

    private sealed class ItemInfo
    {
        public int W, H;
        public List<(int H, int V)> Grids = new();
        /// <summary>★ 与 <see cref="Grids"/> 一一对应的真实 `_props.Grids[]._name`（客户端按它匹配 slotId）。</summary>
        public List<string> GridNames = new();
        public string Name;
    }

    private sealed class ItemTable : Dictionary<string, ItemInfo>
    {
        public ItemTable() : base(StringComparer.OrdinalIgnoreCase) { }
    }

    private sealed class FakeItem
    {
        public FakeProps Properties { get; set; }
    }

    private sealed class FakeProps
    {
        public int? Width { get; set; }
        public int? Height { get; set; }
        public List<FakeGrid> Grids { get; set; } = new();
    }

    private sealed class FakeGrid
    {
        /// <summary>★ 对应 items.json 的 `_props.Grids[]._name`（维度要靠它决定 slotId）。</summary>
        public string Name { get; set; }
        public FakeGridProps Properties { get; set; }
    }

    private sealed class FakeGridProps
    {
        public int? CellsH { get; set; }
        public int? CellsV { get; set; }
    }

    private sealed class FakeTemplateTable
    {
        public IDictionary Items { get; set; }
    }

    private sealed class FakeLocationTable
    {
        private readonly Dictionary<string, Location> _dict;
        public FakeLocationTable(Dictionary<string, Location> d) { _dict = d; }
        public IDictionary GetDictionary() => _dict;
    }

    /// <summary>读实机 items.json：tpl → (_props.Width/Height, Grids[].cellsH/cellsV)。</summary>
    private static ItemTable LoadItemTpls(string path)
    {
        var res = new ItemTable();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var info = new ItemInfo();
            if (p.Value.TryGetProperty("_props", out var props))
            {
                if (props.TryGetProperty("Width", out var w) && w.ValueKind == JsonValueKind.Number) info.W = w.GetInt32();
                if (props.TryGetProperty("Height", out var h) && h.ValueKind == JsonValueKind.Number) info.H = h.GetInt32();
                if (props.TryGetProperty("Grids", out var grids) && grids.ValueKind == JsonValueKind.Array)
                {
                    foreach (var g in grids.EnumerateArray())
                    {
                        if (!g.TryGetProperty("_props", out var gp)) continue;
                        int ch = gp.TryGetProperty("cellsH", out var c1) && c1.ValueKind == JsonValueKind.Number ? c1.GetInt32() : 0;
                        int cv = gp.TryGetProperty("cellsV", out var c2) && c2.ValueKind == JsonValueKind.Number ? c2.GetInt32() : 0;
                        if (ch > 0 && cv > 0)
                        {
                            info.Grids.Add((ch, cv));
                            info.GridNames.Add(g.TryGetProperty("_name", out var gn) && gn.ValueKind == JsonValueKind.String
                                ? gn.GetString() : null);
                        }
                    }
                }
            }
            info.Name = p.Name;
            res[p.Name] = info;
        }
        return res;
    }

    /// <summary>把实机 items.json 转成维度能反射读取的假 TemplateTable（只保留尺寸/Grids）。</summary>
    private static IDictionary BuildFakeItemTable(string itemsPath)
    {
        var table = new Dictionary<string, FakeItem>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(File.ReadAllText(itemsPath));
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var item = new FakeItem { Properties = new FakeProps() };
            if (p.Value.TryGetProperty("_props", out var props))
            {
                if (props.TryGetProperty("Width", out var w) && w.ValueKind == JsonValueKind.Number) item.Properties.Width = w.GetInt32();
                if (props.TryGetProperty("Height", out var h) && h.ValueKind == JsonValueKind.Number) item.Properties.Height = h.GetInt32();
                if (props.TryGetProperty("Grids", out var grids) && grids.ValueKind == JsonValueKind.Array)
                {
                    foreach (var g in grids.EnumerateArray())
                    {
                        var fg = new FakeGrid { Properties = new FakeGridProps() };
                        if (g.TryGetProperty("_name", out var fn) && fn.ValueKind == JsonValueKind.String)
                            fg.Name = fn.GetString();
                        if (g.TryGetProperty("_props", out var gp))
                        {
                            if (gp.TryGetProperty("cellsH", out var c1) && c1.ValueKind == JsonValueKind.Number) fg.Properties.CellsH = c1.GetInt32();
                            if (gp.TryGetProperty("cellsV", out var c2) && c2.ValueKind == JsonValueKind.Number) fg.Properties.CellsV = c2.GetInt32();
                        }
                        item.Properties.Grids.Add(fg);
                    }
                }
            }
            table[p.Name] = item;
        }
        return table;
    }

    private static List<(int H, int V)> GridsOf(string containerTpl, ItemTable tpls)
        => tpls.TryGetValue(containerTpl, out var info) ? info.Grids : new List<(int, int)>();

    /// <summary>
    /// ★ v3：注入件 location 合法性（独立断言，不复用被测代码的算法）：
    ///   · null ⇒ 允许（只有"确实没空格/没有参照类型"才会是 null）
    ///   · 有坐标 ⇒ 必须落在该容器某个真实格子里、且不与同容器其它有坐标的物品重叠
    /// </summary>
    private static bool LocOk(SptLootItem it, string containerTpl, IEnumerable<SptLootItem> siblings,
                              ItemTable tpls, out string why)
    {
        why = null;
        if (it == null) { why = "item 为 null"; return false; }
        if (it.Location == null) return true;
        if (!tpls.TryGetValue(containerTpl, out var cinfo)) { why = "容器 tpl 缺失"; return false; }
        if (!tpls.TryGetValue(it.Template.ToString(), out var iinfo)) { why = "注入物 tpl 缺失"; return false; }

        int w = iinfo.W, h = iinfo.H;
        int xi, yi, ri;
        if (it.Location is ItemLocation loc)
        {
            xi = loc.X ?? 0; yi = loc.Y ?? 0;
            ri = loc.R == ItemRotation.Vertical ? 1 : 0;
        }
        else if (it.Location is JsonElement je)
        {
            xi = je.TryGetProperty("x", out var jx) ? jx.GetInt32() : 0;
            yi = je.TryGetProperty("y", out var jy) ? jy.GetInt32() : 0;
            ri = je.TryGetProperty("r", out var jr)
                ? (jr.ValueKind == JsonValueKind.String ? (jr.GetString() == "Vertical" ? 1 : 0) : jr.GetInt32())
                : 0;
        }
        else { why = "location 类型未知"; return false; }
        if (ri != 0) { var t = w; w = h; h = t; }

        // 该 grid 的尺寸（slotId 对应的那个）
        int idx = cinfo.GridNames.FindIndex(n => string.Equals(n ?? "main", it.SlotId ?? "main", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) { why = "slotId 找不到对应 grid"; return false; }
        var (gh, gv) = cinfo.Grids[idx];
        if (xi < 0 || yi < 0 || xi + w > gh || yi + h > gv) { why = $"{xi},{yi} {w}x{h} 越界 {gh}x{gv}"; return false; }

        // 与同容器其它有坐标的直接子物品比对
        foreach (var sib in siblings)
        {
            if (sib == null || ReferenceEquals(sib, it)) continue;
            if (!string.Equals(sib.SlotId ?? "main", it.SlotId ?? "main", StringComparison.OrdinalIgnoreCase)) continue;
            if (sib.Location == null) continue;
            if (!tpls.TryGetValue(sib.Template.ToString(), out var sinfo)) continue;
            int sw = sinfo.W, sh = sinfo.H;
            int sx, sy, sr;
            if (sib.Location is ItemLocation sl) { sx = sl.X ?? 0; sy = sl.Y ?? 0; sr = sl.R == ItemRotation.Vertical ? 1 : 0; }
            else continue;
            if (sr != 0) { var t = sw; sw = sh; sh = t; }
            bool overlap = xi < sx + sw && sx < xi + w && yi < sy + sh && sy < yi + h;
            if (overlap) { why = $"与 {sib.Template} 在 {sx},{sy} 重叠"; return false; }
        }
        return true;
    }

    /// <summary>★ v3：审一个容器的全部直接子物品的落位（统计非法坐标 / 越界 / 重叠 / null）。</summary>
    private static void AuditPlaced(SpawnpointTemplate sp, ItemTable tpls,
                                    ref int direct, ref int badLoc, ref int overlap, ref int outOfGrid, ref int nullLoc,
                                    List<string> why)
    {
        var ctpl = RootTplOf(sp);
        if (ctpl == null || !tpls.TryGetValue(ctpl, out var cinfo)) return;

        var placed = new List<(int X, int Y, int W, int H, string Tpl)>();
        foreach (var it in sp.Items)
        {
            if (it == null) continue;
            if (!string.Equals(it.ParentId, sp.Root, StringComparison.OrdinalIgnoreCase)) continue;   // 只看容器直接子物品
            direct++;
            if (it.Location == null) { nullLoc++; continue; }
            if (!tpls.TryGetValue(it.Template.ToString(), out var iinfo)) { badLoc++; continue; }
            int w = iinfo.W, h = iinfo.H, x, y, r;
            if (it.Location is ItemLocation loc) { x = loc.X ?? 0; y = loc.Y ?? 0; r = loc.R == ItemRotation.Vertical ? 1 : 0; }
            else { badLoc++; continue; }
            if (r != 0) { var t = w; w = h; h = t; }

            int idx = cinfo.GridNames.FindIndex(n => string.Equals(n ?? "main", it.SlotId ?? "main", StringComparison.OrdinalIgnoreCase));
            if (idx < 0) { badLoc++; continue; }
            var (gh, gv) = cinfo.Grids[idx];
            if (x < 0 || y < 0 || x + w > gh || y + h > gv)
            {
                outOfGrid++;
                if (why.Count < 5) why.Add($"{ctpl}/{it.Template} {x},{y} {w}x{h} 越界 {gh}x{gv}");
                continue;
            }
            foreach (var p in placed)
            {
                if (x < p.X + p.W && p.X < x + w && y < p.Y + p.H && p.Y < y + h)
                {
                    overlap++;
                    if (why.Count < 5) why.Add($"{ctpl}: {it.Template}@{x},{y} 与 {p.Tpl}@{p.X},{p.Y} 重叠");
                    break;
                }
            }
            placed.Add((x, y, w, h, it.Template.ToString()));
        }
    }

    /// <summary>
    /// 按实机 items.json 算「这件物品应该被写进哪个 grid」：优先 `main`，否则第一个塞得进的。
    /// 与维度里的 <c>PickGrid</c> 同规则，用来做**独立**断言（不是拿被测代码自证）。
    /// </summary>
    private static string ExpectedGrid(string containerTpl, int w, int h, ItemTable tpls)
    {
        if (!tpls.TryGetValue(containerTpl, out var info)) return null;
        string fallback = null;
        for (int i = 0; i < info.Grids.Count; i++)
        {
            var (gh, gv) = info.Grids[i];
            if (!((w <= gh && h <= gv) || (w <= gv && h <= gh))) continue;
            var nm = info.GridNames[i];
            if (string.IsNullOrEmpty(nm)) nm = "main";
            if (string.Equals(nm, "main", StringComparison.OrdinalIgnoreCase)) return nm;
            if (fallback == null) fallback = nm;
        }
        return fallback;
    }

    /// <summary>
    /// 造一个「SPT 生成完之后」的 `SpawnpointTemplate`：
    ///   · `Root` = 新 MongoId，且根物品 `_id` 换成同一个（`LocationLootGenerator.cs:311-313` 的行为）
    ///   · `dropIds` 里的物品**被剔除**（复现 v1 事故：预注入件没进返回对象）
    ///   · 再追加 1 件「官方物品」带显式 location（模拟官方 `Union` 追加）
    /// </summary>
    private static SpawnpointTemplate MakeGenerated(StaticContainerData data, string[] dropIds, out string newRoot)
    {
        var clone = (SpawnpointTemplate)R.FromJson(typeof(SpawnpointTemplate), R.ToJson(data.Template));
        newRoot = Guid.NewGuid().ToString("N").Substring(0, 24);

        var keep = new List<SptLootItem>();
        foreach (var it in clone.Items)
        {
            if (dropIds != null && dropIds.Any(d => string.Equals(d, it.Id.ToString(), StringComparison.OrdinalIgnoreCase)))
                continue;
            keep.Add(it);
        }
        if (keep.Count == 0) throw new Exception("MakeGenerated: 容器模板空了");

        keep[0].Id = new MongoId(newRoot);              // SPT 只改根物品 _id
        clone.Root = newRoot;

        keep.Add(new SptLootItem                       // 官方物品：显式坐标（客户端走 CreateItemLocation 分支）
        {
            Id = new MongoId(Guid.NewGuid().ToString("N").Substring(0, 24)),
            // ★ v3：用 1x1 的 GP 币当"官方战利品"，避免 2x2 的 Grizzly 把 2x2 抽屉/夹克**人为塞满**，
            //   否则整局模拟会凭空多出一堆"没空格"（那不是实机分布）。
            Template = "5d235b4d86f7742e017bc88a",
            ParentId = newRoot,
            SlotId = "main",
            Location = new ItemLocation { X = 0, Y = 0, R = ItemRotation.Horizontal },
        });

        clone.Items = keep;
        return clone;
    }

    /// <summary>
    /// ★ v3 格子用例：造「一个容器 + 已有一批带显式坐标的物品 + 我们的预注入件」的一局，
    /// 跑完 transformer（Apply）+ postfix（FixUpGeneratedLoot），返回**生成之后**的模板。
    ///   · 容器实例 = 独立 mapKey（互不干扰），模板里只有容器根 + 我们预注入的 1 件；
    ///   · 再模拟 SPT 换根 `_id`，并把 `preplaced` 那批「官方战利品」按给定坐标追加进去。
    /// </summary>
    private static SpawnpointTemplate RunGridCase(Dictionary<string, Location> locs, string key,
        string containerTpl, string itemTpl,
        (string Tpl, int X, int Y, bool Rot)[] preplaced)
    {
        var json = "{\"StaticWeapons\":[],\"StaticContainers\":[{\"Probability\":1.0,\"Template\":{"
                 + "\"Id\":\"probe_container_" + key + "\",\"IsContainer\":true,"
                 + "\"Root\":\"aaaaaaaaaaaaaaaaaaaaaaaa\",\"Items\":[{\"_id\":\"aaaaaaaaaaaaaaaaaaaaaaaa\",\"_tpl\":\""
                 + containerTpl + "\"}]}}],\"StaticForced\":[]}";
        var lz = new LazyLoad<StaticContainerDetails>(
            () => (StaticContainerDetails)R.FromJson(typeof(StaticContainerDetails), json, null), false);
        locs[key] = new Location { StaticContainers = lz };

        ContainerLootInjectDimension.Apply(NewEvent(key, 1.0, "probe_" + key,
            new List<string> { containerTpl }, new Dictionary<string, int> { [itemTpl] = 1 }));

        var tmpl = lz.Value.StaticContainers.First().Template;
        var newRoot = Guid.NewGuid().ToString("N").Substring(0, 24);
        var list = tmpl.Items.ToList();
        list[0].Id = new MongoId(newRoot);              // SPT 只改根物品 _id
        tmpl.Root = newRoot;
        int n = 1;
        foreach (var p in preplaced)
        {
            list.Add(new SptLootItem
            {
                Id = new MongoId("c" + (n++).ToString("D4") + Guid.NewGuid().ToString("N").Substring(0, 19)),
                Template = p.Tpl,
                ParentId = newRoot,
                SlotId = "main",
                Location = new ItemLocation
                {
                    X = p.X,
                    Y = p.Y,
                    R = p.Rot ? ItemRotation.Vertical : ItemRotation.Horizontal,
                },
            });
        }
        tmpl.Items = list;
        ContainerLootInjectDimension.FixUpGeneratedLoot(key, new List<SpawnpointTemplate> { tmpl });
        return tmpl;
    }

    /// <summary>探针侧独立读坐标：兼容 SPT 强类型 `ItemLocation` 与反序列化出来的 `JsonElement`。</summary>
    private static (bool ok, int x, int y, int r) ProbeReadLoc(object loc)
    {
        if (loc == null) return (false, 0, 0, 0);
        if (loc is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            int x = je.TryGetProperty("x", out var px) && px.ValueKind == JsonValueKind.Number ? px.GetInt32() : 0;
            int y = je.TryGetProperty("y", out var py) && py.ValueKind == JsonValueKind.Number ? py.GetInt32() : 0;
            int r = 0;
            if (je.TryGetProperty("r", out var pr))
                r = pr.ValueKind == JsonValueKind.String
                    ? (string.Equals(pr.GetString(), "Vertical", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    : (pr.ValueKind == JsonValueKind.Number ? pr.GetInt32() : 0);
            return (true, x, y, r);
        }
        if (loc is ItemLocation il)
            return (true, il.X ?? 0, il.Y ?? 0, il.R == ItemRotation.Vertical ? 1 : 0);
        try
        {
            var t = loc.GetType();
            int x = Convert.ToInt32(t.GetProperty("X")?.GetValue(loc) ?? 0);
            int y = Convert.ToInt32(t.GetProperty("Y")?.GetValue(loc) ?? 0);
            var rr = t.GetProperty("R")?.GetValue(loc)?.ToString();
            int r = string.Equals(rr, "Vertical", StringComparison.OrdinalIgnoreCase) ? 1
                  : (rr == null ? 0 : Convert.ToInt32(t.GetProperty("R")!.GetValue(loc)));
            return (true, x, y, r);
        }
        catch { return (false, 0, 0, 0); }
    }

    /// <summary>把某容器 tpl 的所有格子改名（证明 slotId 是从数据里读的、不是硬编码 "main"）。</summary>
    private static void RenameGrids(IDictionary table, string tpl, string newName)
    {
        if (!table.Contains(tpl)) return;
        if (table[tpl] is not FakeItem fi) return;
        foreach (var g in fi.Properties.Grids) g.Name = newName;
    }

    /// <summary>造一个只含某一类的临时活动（给边界测试用）。</summary>
    private static EventDef NewEvent(string mapKey, double chance, string cat, List<string> containers,
                                     Dictionary<string, int> items)
    {
        return new EventDef
        {
            Id = "probe_" + mapKey,
            ContainerLootInject = new Dictionary<string, ContainerLootInjectMapOpt>
            {
                [mapKey] = new ContainerLootInjectMapOpt
                {
                    Chance = chance,
                    Categories = new Dictionary<string, ContainerLootCategoryOpt>
                    {
                        [cat] = new ContainerLootCategoryOpt { Containers = containers, Items = items }
                    }
                }
            }
        };
    }

    private static EventDef LoadEvent(string path)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        var list = JsonSerializer.Deserialize<List<EventDef>>(File.ReadAllText(path), opts);
        return list?.FirstOrDefault(e => e.ContainerLootInject != null && e.ContainerLootInject.ContainsKey(MapKey));
    }

    private static string RootTplOf(SpawnpointTemplate tmpl)
    {
        try
        {
            if (tmpl?.Items == null) return null;
            var root = tmpl.Root;
            object first = null;
            foreach (var it in tmpl.Items)
            {
                if (it == null) continue;
                if (first == null) first = it;
                if (!string.IsNullOrEmpty(root) && string.Equals(it.Id.ToString(), root, StringComparison.OrdinalIgnoreCase))
                    return it.Template.ToString();
            }
            return first == null ? null : ((SptLootItem)first).Template.ToString();
        }
        catch { return null; }
    }

    private static bool IsHex24(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length != 24) return false;
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }

    private static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
