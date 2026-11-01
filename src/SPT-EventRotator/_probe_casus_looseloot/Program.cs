// ─────────────────────────────────────────────────────────────────────────────
//  ProbeCasusLooseLoot —— 「赴死之人向你致敬」取箱目标的离线探针（2026-10-24 第二轮）
//
//  目标：在**实机实际加载的 interchange/looseLoot.json 副本**上跑**真身**代码
//        （直接 <Compile Include="../LooseLootInjectDimension.cs" />，不是重写一份），断言：
//    ① 全局 valuable.chance=0 时，只有 region「小黄车」内 maxPoints 个点被替换（2~3 个），
//       其余 3817 个点一个都不动（日志逐字：命中 6 个，实际替换 3 个（上限 3））
//    ①b **按「到车（110.16, 21.34, 290.89）的 3D 距离」挑最近的 N 个点**；
//       逐点日志逐字 `interchange：小黄车 选区替换点 (x, y, z) 距车 d 米`（每点一行、最多 5 行）；
//       最近的 guaranteedPoints 个 `Probability` 被设成 1.0 ⇒ 每局必刷（而不是 73%/71% 的掷骰）
//    ①c ManimalInterchange 的 looseLoot 坐标系与 woods 同一套（x/z 交换后盒内命中 = 0；y 与车同层）
//    ② 替换后的 tpl = 自造物品 6a1f00c70000000000000001，且是单物品刷点
//    ③ 该 tpl **不是容器**（`_props.Grids` 空）；对照原版武器箱 Grids 非空
//    ③b **items 表里查得到**（按 Loader 的落点路径塞进 TemplateTable 再按 MongoId 查回）；
//       `QuestItem == true`（★2026-10-24 实机订正：它不是"地上不出"，而是把刷点从通用 SpawnLoot
//       改成交给 ManageQuestLoot **按玩家**复活 —— 见 Program.cs 里 [断言 ③c] 的逐行证据）
//    ④ 还原（transformer 直通）后重新读 .Value 与注入前**逐字节相同**；切别的活动仍是原样；切回又生效
//    ⑤ 诊断回归：把旧的容器类武器箱塞进 region 时，维度必须打一条「容器类物品」warning
//    ⑥ 兜底方案（车旁 append 静态容器）**不可行**：实机 906 个静态容器 Position 全 (0,0,0)，
//       客户端按 Id 在场景里找对象（找不到 → `lootId is missing`）⇒ 新 Id 永远不生成
//
//  ★ 物品外观（第二轮修）：Prefab 必须**逐字等于**原版武器箱 59fb023c… 的 Prefab
//    （上一版误用 675a081c… 的传单箱 bundle ⇒ 实机在地上像"一张海报"）。
//
//  ★ 关键：SPT 会先读核心 looseLoot.json，再被 `user/mods/<mod>/db/locations/...` 覆盖 ——
//    实机日志 interchange 点位 3820、region 命中 6，对应的就是 ManimalInterchange 那份；
//    核心那份只有 3159 点且盒内 0 命中。探针**优先取 mod 覆盖文件**并在输出里标明来源，
//    否则测的不是实机实际加载的数据（会误报"命中 0"）。
//
//  只读实机文件；临时副本写到系统 temp，退出时删掉。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using EventRotator;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Path = System.IO.Path;

internal static class Program
{
    private const string DefaultSpt = @"D:\PureEFTv4.1\EFTv4.1\SPT_Runtime";
    private const string MapKey = "interchange";
    private const string CasusId = "casus_belli";
    private const string OtherId = "wild_wild_woods";
    private const string QuestId = "2c9d5272b5674c718fadae75";
    private const string OldTpl = "59fb023c86f7746d0d4b423c";   // 原版武器箱（容器类）
    private const string NewTpl = "6a1f00c70000000000000001";   // 自造「竞技场奖励模板箱」（非容器）

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
                return File.Exists(p) ? Assembly.LoadFrom(p) : null;
            }
            catch { return null; }
        };

        ModDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var spt = args.Length > 0 ? args[0] : DefaultSpt;
        var itemsPath = Path.Combine(spt, "SPT_Data", "database", "templates", "items.json");
        var coreLoose = Path.Combine(spt, "SPT_Data", "database", "locations", MapKey, "looseLoot.json");
        var eventsPath = Path.Combine(ModDir, "events.json");
        var qlPath = Path.Combine(ModDir, "questlines", "casus_belli.json");

        // ★ 实机真正加载的是 mod 覆盖文件（核心文件会被整份替换）
        var looseCandidates = new List<string>();
        var modsDir = Path.Combine(spt, "user", "mods");
        if (Directory.Exists(modsDir))
        {
            foreach (var d in Directory.GetDirectories(modsDir))
            {
                var p = Path.Combine(d, "db", "locations", MapKey, "looseLoot.json");
                if (File.Exists(p)) looseCandidates.Add(p);
            }
        }
        if (looseCandidates.Count == 0 && File.Exists(coreLoose)) looseCandidates.Add(coreLoose);
        if (looseCandidates.Count == 0) { Console.WriteLine("[FATAL] 找不到 interchange/looseLoot.json"); return 2; }
        // 多个覆盖时取 spawnpoints 最多的那份（= 实机日志里 3820 点的那份）
        var srcLooseLoot = looseCandidates
            .Select(p => (p, n: CountSpawnpoints(p)))
            .OrderByDescending(x => x.n).First().p;

        foreach (var p in new[] { srcLooseLoot, itemsPath, eventsPath, qlPath })
            if (!File.Exists(p)) { Console.WriteLine("[FATAL] 缺文件: " + p); return 2; }

        Console.WriteLine("=".PadRight(96, '='));
        Console.WriteLine("casus_belli / interchange looseLootInject 离线探针（真身代码 + 实机数据副本）");
        Console.WriteLine("  mod 目录   : " + ModDir);
        Console.WriteLine("  实机点位源 : " + srcLooseLoot + $"  ({CountSpawnpoints(srcLooseLoot)} spawnpoints)");
        Console.WriteLine("  核心文件   : " + coreLoose + $"  ({CountSpawnpoints(coreLoose)} spawnpoints，被 mod 覆盖)");
        Console.WriteLine("  实机文件只读：探针全程不碰它（临时副本退出即删）");

        var tmpRoot = Environment.GetEnvironmentVariable("PROBE_TMP");
        if (string.IsNullOrWhiteSpace(tmpRoot)) tmpRoot = Path.GetTempPath();
        var tmpDir = Path.Combine(tmpRoot, "casus_looseloot_probe_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmpDir);

        try
        {
            var looseJson = File.ReadAllText(srcLooseLoot);
            var events = LoadEvents(eventsPath);
            var casus = events.FirstOrDefault(e => e.Id == CasusId);
            var other = events.FirstOrDefault(e => e.Id == OtherId);
            if (casus == null) { Console.WriteLine("[FATAL] events.json 里没有 casus_belli"); return 2; }
            if (other == null) { Console.WriteLine("[FATAL] events.json 里没有 wild_wild_woods（切活动用）"); return 2; }
            var opt = casus.LooseLootInject[MapKey];
            var region = opt.Regions.Single();

            Console.WriteLine($"\n[0] casus_belli.looseLootInject.interchange：density={opt.DensityMultiplier}、"
                              + $"valuable.chance={opt.Valuable?.Chance}、tpls={(opt.Valuable?.Tpls is { } tp ? string.Join(",", tp.Keys) : "")}、"
                              + $"decor.chance={opt.Decor?.Chance}、region「{region.Name}」maxPoints={region.MaxPoints}、"
                              + $"guaranteedPoints={region.GuaranteedPoints}、anchor=({region.Anchor?.X},{region.Anchor?.Y},{region.Anchor?.Z})「{region.AnchorName}」");

            // ── 读 questline 的自造物品定义（真身 loader 用的就是这段 JSON） ──
            var qlDoc = JsonDocument.Parse(File.ReadAllText(qlPath));
            var qlItems = qlDoc.RootElement.GetProperty("items");
            var qlLocalesEn = qlDoc.RootElement.GetProperty("locales").GetProperty("en");
            var qlLocalesCh = qlDoc.RootElement.GetProperty("locales").GetProperty("ch");
            string newItemJson = qlItems.GetProperty(NewTpl).GetRawText();
            var newItem = ParseTemplateItem(newItemJson);
            var newProps = R.Prop(newItem, "Properties");

            Console.WriteLine($"\n[1] 自造物品 {NewTpl}：_name={R.Prop(newItem, "Name")}、_parent={R.Prop(newItem, "Parent")}、"
                              + $"W/H={R.Prop(newProps, "Width")}/{R.Prop(newProps, "Height")}、Grids={(R.Prop(newProps, "Grids") as ICollection)?.Count ?? 0}");
            Check(R.Prop(newItem, "Name")?.ToString() == "casus_arena_template_crate", "items 定义读得出来（_name=casus_arena_template_crate）");
            Check(qlLocalesEn.GetProperty(NewTpl + " Name").GetString() == "Arena reward template crate",
                  "locale en Name 逐字 = Arena reward template crate");
            Check(qlLocalesCh.GetProperty(NewTpl + " Name").GetString() == "竞技场奖励模板箱",
                  "locale ch Name 逐字 = 竞技场奖励模板箱");
            foreach (var suffix in new[] { " Name", " ShortName", " Description" })
                Check(qlLocalesEn.TryGetProperty(NewTpl + suffix, out _) && qlLocalesCh.TryGetProperty(NewTpl + suffix, out _),
                      "locale en/ch 都有「" + suffix.Trim() + "」三件套之一");

            // ── itemMeta / _parent 链（必须是可拾取 barter 类，且本机真实存在） ──
            var itemsDoc = JsonDocument.Parse(File.ReadAllText(itemsPath));
            Check(itemsDoc.RootElement.TryGetProperty(NewTpl, out _) == false, "新 tpl 不与本机 items.json 撞车");
            var parent = R.Prop(newItem, "Parent")?.ToString();
            string chain = parent;
            while (!string.IsNullOrEmpty(parent) && itemsDoc.RootElement.TryGetProperty(parent, out var pnode))
            {
                var pp = pnode.TryGetProperty("_parent", out var pv) ? pv.GetString() : null;
                if (string.IsNullOrEmpty(pp)) break;
                parent = pp;
                chain += " → " + pp;
                if (chain.Length > 200) break;
            }
            Check(!string.IsNullOrEmpty(chain) && chain.Contains("5448eb774bdc2d0a728b4567"),
                  "新物品的 _parent 链落在可拾取 BarterItem 类 5448eb774bdc2d0a728b4567",
                  chain);

            // ★ Prefab 必须**逐字等于**本机原版武器箱 59fb023c… 的 Prefab（不是"看起来差不多"的箱子）
            const string BoxTpl = "59fb023c86f7746d0d4b423c";          // item_container_weaponcase（5×2 容器）
            const string FlyerTpl = "675a081c1474133a0d0bb214";        // 上一版误用的传单箱（地上像海报）
            var newPrefab = (R.Prop(R.Prop(newProps, "Prefab"), "Path") ?? "").ToString();
            var boxRoot = itemsDoc.RootElement.GetProperty(BoxTpl);
            var boxProps = boxRoot.GetProperty("_props");
            var boxPrefabPath = boxProps.GetProperty("Prefab").GetProperty("path").GetString();
            Check(string.Equals(boxPrefabPath, newPrefab, StringComparison.OrdinalIgnoreCase),
                  "新物品 Prefab **逐字等于**原版武器箱 59fb023c… 的 Prefab", newPrefab);
            var boxW = boxProps.GetProperty("Width").GetInt32();
            var boxH = boxProps.GetProperty("Height").GetInt32();
            Check(boxW == 5 && boxH == 2, "原版武器箱自身的真尺寸 = 5×2（如实记录，不是 2×2/3×2）",
                  $"{boxW}×{boxH}");
            var flyerPath = itemsDoc.RootElement.GetProperty(FlyerTpl).GetProperty("_props")
                                    .GetProperty("Prefab").GetProperty("path").GetString();
            Check(!string.Equals(newPrefab, flyerPath, StringComparison.OrdinalIgnoreCase),
                  "已不再使用 675a081c… 的 Prefab（上一版那张「海报」的根因）", flyerPath);
            // ★ 铁证：上一版那个 prefab 的主人是**一张海报**（本体 locale en Name 逐字）——
            //   玩家说"像一张海报"不是错觉，是被选成了 Poster 的模型。
            var enLocalePath = Path.Combine(spt, "SPT_Data", "database", "locales", "global", "en.json");
            if (File.Exists(enLocalePath))
            {
                using var enDoc = JsonDocument.Parse(File.ReadAllText(enLocalePath));
                string LName(string tpl) => enDoc.RootElement.TryGetProperty(tpl + " Name", out var v) ? v.GetString() : null;
                string LShort(string tpl) => enDoc.RootElement.TryGetProperty(tpl + " ShortName", out var v) ? v.GetString() : null;
                Console.WriteLine($"      · locale en 名：675a081c… =「{LName("675a081c1474133a0d0bb214")}」/「{LShort("675a081c1474133a0d0bb214")}」"
                                  + $"；59fb023c… =「{LName("59fb023c86f7746d0d4b423c")}」/「{LShort("59fb023c86f7746d0d4b423c")}」");
                Check((LName("675a081c1474133a0d0bb214") ?? "").Contains("poster", StringComparison.OrdinalIgnoreCase),
                      "上一版 prefab 的主人本体名就叫「…poster / Poster」⇒ 地上像海报是必然（不是渲染 bug）");
            }
            Check(newPrefab.Contains("item_container_weaponcase"), "Prefab 落在 containers/item_container_weaponcase 目录");
            var newW = (int)R.Prop(newProps, "Width");
            var newH = (int)R.Prop(newProps, "Height");
            Check(newW == 3 && newH == 2, "新物品格子 = 3×2（5 宽塞不进 4×4 口袋 ⇒ 捡不起来，故取 3×2）",
                  $"{newW}×{newH}");
            Check(newW <= 4 && newH <= 4, "3×2 能塞进 4×4 口袋（玩家不用背背包也能捡）");

            // ── 任务条件 target ──
            var q = qlDoc.RootElement.GetProperty("quests").GetProperty(QuestId);
            var targets = new List<string>();
            foreach (var c in q.GetProperty("conditions").GetProperty("AvailableForFinish").EnumerateArray())
            {
                if (!c.TryGetProperty("conditionType", out var ct)) continue;
                var t = ct.GetString();
                if (t != "FindItem" && t != "HandoverItem") continue;
                foreach (var x in c.GetProperty("target").EnumerateArray()) targets.Add(x.GetString());
            }
            Check(targets.Count == 2 && targets.All(t => t == NewTpl),
                  $"FindItem / HandoverItem target 都是新 tpl（实际 {string.Join(",", targets)}）");

            // ── 构造 TemplateTable（真身诊断要读它） ──
            var oldItemJson = itemsDoc.RootElement.GetProperty(OldTpl).GetRawText();
            var oldItem = ParseTemplateItem(oldItemJson);
            var tplTable = new FakeTemplateTable
            {
                Items = new Dictionary<MongoId, TemplateItem>
                {
                    [new MongoId(NewTpl)] = newItem,
                    [new MongoId(OldTpl)] = oldItem,
                }
            };

            // ── FakeLazy：复刻 cacheValue=false（每次 .Value 重新反序列化 + 套 transformer） ──
            var lazy = new FakeLazy<LooseLoot>(() => (LooseLoot)R.FromJson(typeof(LooseLoot), looseJson));
            var baselineObj = lazy.Value;
            var baselineJson = R.ToJson(baselineObj);
            var baseline = baselineObj.Spawnpoints.ToList();
            Console.WriteLine($"\n[2] 基线：spawnpoints={baseline.Count}，mean={baselineObj.SpawnpointCount?.Mean:0.###}，序列化 {baselineJson?.Length:N0} chars");

            // ── 探针独立算 region 命中（与维度同规则：非必刷 / 非 christmas / 有坐标 / 在盒内） ──
            //    并按「到锚点（车）的 3D 距离」排序 —— 这就是维度挑点的唯一依据，探针自己算一遍对照。
            var hits = new List<Hit>();
            foreach (var sp0 in baseline)
            {
                if (!IsReplaceable(sp0)) continue;
                if (!TryXZ(sp0, out var x, out var z)) continue;
                if (!(x >= Math.Min(region.Min.X, region.Max.X) && x <= Math.Max(region.Min.X, region.Max.X)
                      && z >= Math.Min(region.Min.Z, region.Max.Z) && z <= Math.Max(region.Min.Z, region.Max.Z)))
                    continue;
                var p = sp0.Template.Position.Value;
                double d3 = region.Anchor == null ? 0 : Dist3(p.X, p.Y, p.Z, region.Anchor.X, region.Anchor.Y, region.Anchor.Z);
                hits.Add(new Hit { Dist = d3, X = p.X, Y = p.Y, Z = p.Z, Sp = sp0 });
            }
            hits.Sort((a, b) => a.Dist.CompareTo(b.Dist));
            int regionHits = hits.Count;

            Console.WriteLine($"[2] region「{region.Name}」盒内可替换点位：{regionHits} 个（上限 {region.MaxPoints}、必刷 {region.GuaranteedPoints}）");
            if (region.Anchor != null)
            {
                Console.WriteLine($"[2] 锚点「{region.AnchorName}」= ({region.Anchor.X}, {region.Anchor.Y}, {region.Anchor.Z})；"
                                  + "盒内点位按 3D 距离由近到远：");
                foreach (var h in hits)
                    Console.WriteLine($"      ({h.X:0.##}, {h.Y:0.##}, {h.Z:0.##})  d3D={h.Dist:0.##} m  "
                                      + $"dXZ={Math.Sqrt((h.X - region.Anchor.X) * (h.X - region.Anchor.X) + (h.Z - region.Anchor.Z) * (h.Z - region.Anchor.Z)):0.##} m  "
                                      + $"dy={h.Y - region.Anchor.Y:+0.##;-0.##}  p={h.Sp.Probability:0.#####}  loc={h.Sp.LocationId}");
            }

            // ── 坐标系核对：x/z 交换后是否还有命中？y 带是否覆盖车的 y？ ──
            Console.WriteLine("[2] 坐标系核对（ManimalInterchange 的 looseLoot 是否与 woods 同一套 x/y/z）：");
            int swappedHits = 0;
            foreach (var h in hits)
                if (h.Z >= Math.Min(region.Min.X, region.Max.X) && h.Z <= Math.Max(region.Min.X, region.Max.X)
                    && h.X >= Math.Min(region.Min.Z, region.Max.Z) && h.X <= Math.Max(region.Min.Z, region.Max.Z))
                    swappedHits++;

            // ── 跑真身维度 ──
            var restores = new List<Action>();
            var logLines = new List<string>();
            LooseLootInjectDimension.LocationTable = new FakeLocationTable(new Dictionary<string, object>
            {
                [MapKey] = new FakeLocation { LooseLoot = lazy }
            });
            LooseLootInjectDimension.TemplateTable = tplTable;
            LooseLootInjectDimension.Log = m => { logLines.Add(m); Console.WriteLine("      " + m); };
            LooseLootInjectDimension.AddRestore = a => restores.Add(a);
            LooseLootInjectDimension.ResetApplied();
            LooseLootInjectDimension.Apply(casus);

            var afterObj = lazy.Value;
            var after = afterObj.Spawnpoints.ToList();

            // ── ① 只有 region 内 maxPoints 个点被替换 ──
            Console.WriteLine("\n[断言 ①] 全局 chance=0 ⇒ 只有 region 内 maxPoints 个点被替换，其余点位不动");
            var replaced = after.Where(sp => ItemTpl(sp) == NewTpl).ToList();
            int cap = region.MaxPoints ?? 0;
            Check(regionHits >= cap, $"盒内命中 {regionHits} 个点（≥ maxPoints {cap}，才可能替换 {cap} 个）");
            Check(replaced.Count == cap,
                  $"实际替换 {replaced.Count} 个 = maxPoints {cap}（不是全图）",
                  $"全图共 {after.Count} 个点位");
            Check(replaced.Count >= 2 && replaced.Count <= 3, "替换数量在验收要求 2~3 之间", $"{replaced.Count}");
            Check(after.Count == baseline.Count, "densityMultiplier=1.0 ⇒ 点位数不变（没有克隆）",
                  $"{baseline.Count} → {after.Count}");
            var hitLine = logLines.FirstOrDefault(l => l.Contains($"region「{region.Name}」"));
            Check(hitLine != null && hitLine.Contains($"命中 {regionHits} 个") && hitLine.Contains($"实际替换 {replaced.Count} 个"),
                  "维度日志逐字：命中 N 个，实际替换 M 个（上限 X）", hitLine);
            Check(logLines.Any(l => l.Contains("物品替换 valuable 0 / decor 0")),
                  "全局替换 valuable 0 / decor 0（chance=0 生效，没再出现 3769 全图事故）",
                  logLines.FirstOrDefault(l => l.Contains("物品替换")));

            // ══ ①b 【本次核心】替换点是不是"离车最近的 N 个" + 逐点实测坐标日志 + 必刷 ══
            Console.WriteLine($"\n[断言 ①b] 按「到锚点的 3D 距离」精确挑点 / 逐点坐标日志 / 必刷（上限 {cap}）");
            var wantLocs = hits.Take(cap).Select(h => h.Sp.LocationId).ToList();
            var gotLocs = replaced.Select(sp => sp.LocationId).ToList();
            Check(wantLocs.Count == cap && gotLocs.OrderBy(x => x).SequenceEqual(wantLocs.OrderBy(x => x)),
                  $"被替换的 {cap} 个点 == 盒内**离车最近的 {cap} 个**（按 3D 距离）",
                  "最近：" + string.Join(" / ", hits.Take(cap).Select(h => $"{h.Dist:0.##}m")));

            var repDists = new List<double>();
            foreach (var sp in replaced)
            {
                var p = sp.Template.Position.Value;
                repDists.Add(Dist3(p.X, p.Y, p.Z, region.Anchor.X, region.Anchor.Y, region.Anchor.Z));
            }
            repDists.Sort();
            Check(repDists.Count > 0 && repDists[0] <= 6.0,
                  $"最近的替换点距车 ≤ 6 m（实测 {repDists.FirstOrDefault():0.##} m）");
            Check(repDists.Last() <= 12.0, $"最远的替换点距车 ≤ 12 m（实测 {repDists.LastOrDefault():0.##} m）");

            // 逐点日志：`interchange：小黄车 选区替换点 (x, y, z) 距车 d 米`，每个点一行、最多 5 行
            var ptLines = logLines.Where(l => l.Contains("选区替换点")).ToList();
            var ptRe = new System.Text.RegularExpressions.Regex(
                @"^\[EventRotator\]\[地面战利品注入\] interchange：小黄车 选区替换点 \(-?\d+(\.\d+)?, -?\d+(\.\d+)?, -?\d+(\.\d+)?\) 距车 \d+(\.\d+)? 米$");
            Check(ptLines.Count == Math.Min(cap, 5) && ptLines.All(l => ptRe.IsMatch(l)),
                  $"逐点坐标日志 {ptLines.Count} 行，逐字匹配「… 选区替换点 (x, y, z) 距车 d 米」（≤5 行）",
                  ptLines.FirstOrDefault());
            Console.WriteLine("      （逐点日志实测）");
            foreach (var l in ptLines) Console.WriteLine("        " + l);

            // 必刷：前 guaranteedPoints 个（= 最近的）Probability == 1.0；其余保持原值
            var baseProb = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
            foreach (var sp in baseline) baseProb[sp.LocationId ?? ""] = sp.Probability;
            int guaranteed = region.GuaranteedPoints ?? 0;
            int mustSpawn = 0, probKept = 0;
            foreach (var sp in replaced)
            {
                var p = sp.Template.Position.Value;
                double d = Dist3(p.X, p.Y, p.Z, region.Anchor.X, region.Anchor.Y, region.Anchor.Z);
                bool isNearest = hits.Take(guaranteed).Any(h => h.Sp.LocationId == sp.LocationId);
                if (isNearest) { if (sp.Probability == 1.0) mustSpawn++; }
                else if (baseProb.TryGetValue(sp.LocationId ?? "", out var bp) && sp.Probability == bp) probKept++;
            }
            Check(guaranteed >= 1 && mustSpawn == guaranteed,
                  $"最近 {guaranteed} 个替换点 Probability 已设 1.0 ⇒ 局内必刷（SPT：Probability==1.0 不进概率抽取）",
                  $"实测 {mustSpawn}/{guaranteed}；其余 {probKept} 个保持原概率");
            Check(logLines.Any(l => l.Contains("Probability=1.0") && l.Contains("每局必刷")),
                  "维度打出了「Probability=1.0 ⇒ 每局必刷」的日志",
                  logLines.FirstOrDefault(l => l.Contains("Probability=1.0")));

            // 坐标系：x/z 若被交换，盒内命中应为 0；y 必须与车同一层的量级
            Console.WriteLine("[断言 ①c] ManimalInterchange 的 looseLoot 坐标系与 woods 一致（x/z 未交换、y 同轴）");
            Check(swappedHits == 0, $"把 x/z 交换后盒内命中 = {swappedHits}（0 ⇒ 没有轴交换）");
            Check(regionHits == 6, $"按 (x,z) 判定盒内恰好 {regionHits} 个点");
            Check(hits.All(h => Math.Abs(h.Y - region.Anchor.Y) <= 5.0),
                  "盒内所有点的 y 与车同一层（|dy| ≤ 5 m）",
                  "dy=" + string.Join("/", hits.Select(h => $"{h.Y - region.Anchor.Y:0.##}")));
            Check(hits.All(h => h.Y > -50 && h.Y < 100),
                  "盒内 y 落在 interchange 的实测 y 带内（-13 ~ 39）",
                  $"y={string.Join("/", hits.Select(h => $"{h.Y:0.##}"))}");

            // 逐点比对：除了被替换那 2 个，其他点位 Items 的 tpl 与基线一一对应（按 LocationId）
            var baseByLoc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sp in baseline) baseByLoc[sp.LocationId ?? ""] = ItemTpl(sp) ?? "";
            int changedOther = 0;
            foreach (var sp in after)
            {
                var loc = sp.LocationId ?? "";
                if (!baseByLoc.TryGetValue(loc, out var beforeTpl)) { changedOther++; continue; }   // 不该有克隆点
                var nowTpl = ItemTpl(sp) ?? "";
                if (nowTpl != beforeTpl && nowTpl != NewTpl) changedOther++;
            }
            Check(changedOther == 0, $"除注入 tpl 外没有任何其它 tpl 被改动（差异 {changedOther} 个点）");

            // ── ② 替换后的 tpl = 新自造物品 ──
            Console.WriteLine("\n[断言 ②] 替换后的点位里装的正是自造物品");
            Check(replaced.Count > 0 && replaced.All(sp => ItemTpl(sp) == NewTpl),
                  $"被替换点位的 _tpl 全部 = {NewTpl}");
            Check(replaced.All(sp => (sp.ItemDistribution?.Count() ?? 0) == 1 && sp.Template.Items.Count() == 1),
                  "替换点是单物品刷点：Items 1 条 + ItemDistribution 1 条（合成键对齐，局内必落该件）");
            Check(!baseline.Any(sp => ItemTpl(sp) == NewTpl),
                  "基线里本来 0 个该 tpl（所以「替换后 = 新 tpl」统计不会混入原有物品）");

            // ── ③ 新 tpl 不是容器；旧武器箱是容器 ──
            Console.WriteLine("\n[断言 ③] 新 tpl 不是容器（IsContainer 等价判据 = _props.Grids 非空）");
            var newGrids = R.Prop(newProps, "Grids") as ICollection;
            Check(newGrids == null || newGrids.Count == 0,
                  "新物品 _props.Grids 为空 ⇒ 不是容器类物品", $"Grids={(newGrids?.Count ?? 0)}");
            var oldProps = R.Prop(oldItem, "Properties");
            var oldGrids = R.Prop(oldProps, "Grids") as ICollection;
            Check(oldGrids != null && oldGrids.Count > 0,
                  "对照：原版武器箱 59fb023c… 的 _props.Grids 非空（所以它是容器类物品）",
                  $"Grids={oldGrids?.Count}");
            Check(R.Prop(newItem, "IsContainer") == null && R.Prop(newProps, "IsContainer") == null
                  || !(R.Prop(newProps, "IsContainer") is bool b && b),
                  "新物品模板上没有 IsContainer=true 标记");

            // ── ③b 「items.json 里能查到」＝ 按 QuestLineDimension.Load 的落点路径把它塞进 TemplateTable 再按 MongoId 查回来 ──
            Console.WriteLine("\n[断言 ③b] 新 tpl 能被服务端 items 表查到 + 能被 loose loot 生成器接受");
            var table = new Dictionary<MongoId, TemplateItem> { [new MongoId(NewTpl)] = newItem };
            bool tableOk = table.TryGetValue(new MongoId(NewTpl), out var found) && found != null;
            Check(tableOk, "按 MongoId 在 TemplateTable.Items 里查得到新 tpl（Loader 的落点同型）");
            Check(found != null && (R.Prop(R.Prop(found, "Properties"), "Grids") as ICollection)?.Count is null or 0,
                  "查回来的模板 `_props.Grids` 为空 ⇒ 不是容器");
            Check(found != null && string.Equals((R.Prop(R.Prop(R.Prop(found, "Properties"), "Prefab"), "Path") ?? "").ToString(),
                                                 newPrefab, StringComparison.OrdinalIgnoreCase),
                  "查回来的模板 Prefab 与 JSON 逐字一致");
            // ★★ 2026-10-24 实机订正（取代旧的「QuestItem 会摘走刷点 ⇒ 千万别标」结论）：
            //   客户端 `GameWorld.GetQuestLootReady`（Assembly-CSharp.decompiled.cs:552111-552126）确实把
            //   `QuestItem == true` 的刷点从 `LootData` 里摘出来放进 `_questLootItems`，**不**走通用 SpawnLoot；
            //   但紧接着 `ManageQuestLoot`（:552855-552895）会为"任务已接 + FindItem 未完成 + 任务物品栏里还没有"
            //   的玩家 `SpawnLootItem(questLootItem, initial:true, player)`（:552329：`CloneItem()` 后落到
            //   `lootItem.Position`）⇒ 箱**照样在地上**，只是变成"只为该玩家刷"的个人任务物品。
            //   若没有任何匹配玩家：:552888 把 `ValidProfiles=null`，而 `LootItem.Init`（:916089）对
            //   `item.QuestItem` 执行 `SetActive(validProfiles != null && ...)` ⇒ 物体隐藏（所以没接任务时看不见，
            //   这是官方任务物品行为，不是"刷点被删"）。
            //   ★ 反向铁证：`ItemManipulator.Add`（:857408-857415）对 `item.QuestItem && !在任务容器` 抛
            //   QuestItemInNonQuestStashError，对 `!item.QuestItem && 在任务容器` 抛 NonQuestItemInQuestStashError
            //   ⇒ QuestItem=false 的物品**永远**进不了任务物品栏（与任务是 FindItem 无关）。
            Check(R.Prop(R.Prop(found, "Properties"), "QuestItem") is bool qi && qi,
                  "QuestItem == true（进任务物品栏的唯一开关；刷点由 GetQuestLootReady→ManageQuestLoot 按玩家复活）");
            // ★ `ManageQuestLoot` 复活时**不经过**通用 SpawnLoot 的 `SpawnedInSession = true`
            //   （:552162 只对非任务物品的 lootData 生效）⇒ 这种任务物品没有 FiR 位。
            //   官方惯例（下面 ③c 从实机 quests.json/items.json 现场统计）：QuestItem=true 的
            //   FindItem/HandoverItem 全部 `onlyFoundInRaid=false`。若仍写 true，客户端
            //   `ConditionItem.FindInItemPass`（:1074556）会要求 `MarkedAsSpawnedInSession` ⇒ FindItem 恒 0、
            //   `GetItemsForCondition`（:1074453）过滤后上交窗口为空。
            int ourFirTrue = 0;
            foreach (var c in q.GetProperty("conditions").GetProperty("AvailableForFinish").EnumerateArray())
            {
                if (!c.TryGetProperty("conditionType", out var ct)) continue;
                var t = ct.GetString();
                if (t != "FindItem" && t != "HandoverItem") continue;
                if (c.TryGetProperty("onlyFoundInRaid", out var fv) && fv.ValueKind == JsonValueKind.True) ourFirTrue++;
            }
            Check(ourFirTrue == 0,
                  "本任务的 FindItem / HandoverItem 均 onlyFoundInRaid=false（任务物品没有 FiR 位，与官方 121/121 一致）",
                  $"onlyFoundInRaid=true 的条件 {ourFirTrue} 条");

            var rtItems = new Dictionary<MongoId, TemplateItem> { [new MongoId(NewTpl)] = newItem, [new MongoId(OldTpl)] = oldItem };
            Check(after.SelectMany(sp => sp.Template.Items).Where(it => it.Template == new MongoId(NewTpl)).All(it => rtItems.ContainsKey(it.Template)),
                  "loose loot 生成器产出的替换点，其 `_tpl` 都能在 items 表里解析");

            // ── ③c 官方惯例：QuestItem=true 的物品，FindItem/HandoverItem 的 onlyFoundInRaid 统计 ──
            //   依据：`ManageQuestLoot` 复活任务物品时不写 `SpawnedInSession`（见 ③b），
            //   所以官方把这类条件写成 onlyFoundInRaid=false；否则客户端 FiR 过滤会让条件永远不满足。
            Console.WriteLine("\n[断言 ③c] 官方 QuestItem=true 物品的 FindItem/HandoverItem 的 onlyFoundInRaid 惯例");
            var questsPath = Path.Combine(spt, "SPT_Data", "database", "templates", "quests.json");
            int officialFindQi = 0, officialFindQiFir = 0, officialHandQi = 0, officialHandQiFir = 0;
            if (!File.Exists(questsPath))
                Check(false, "找不到 quests.json（无法统计官方惯例）", questsPath);
            else
            {
                using var qDoc = JsonDocument.Parse(File.ReadAllText(questsPath));
                bool IsQuestItemTarget(JsonElement tgt)
                {
                    if (tgt.ValueKind != JsonValueKind.Array) return false;
                    foreach (var t in tgt.EnumerateArray())
                        if (t.ValueKind == JsonValueKind.String
                            && itemsDoc.RootElement.TryGetProperty(t.GetString(), out var it)
                            && it.TryGetProperty("_props", out var pr)
                            && pr.TryGetProperty("QuestItem", out var qv)
                            && qv.ValueKind == JsonValueKind.True) return true;
                    return false;
                }
                foreach (var qe in qDoc.RootElement.EnumerateObject())
                {
                    if (!qe.Value.TryGetProperty("conditions", out var conds)) continue;
                    if (!conds.TryGetProperty("AvailableForFinish", out var aff) || aff.ValueKind != JsonValueKind.Array) continue;
                    foreach (var c in aff.EnumerateArray())
                    {
                        if (!c.TryGetProperty("conditionType", out var ct) || !c.TryGetProperty("target", out var tgt)) continue;
                        var t = ct.GetString();
                        if (t != "FindItem" && t != "HandoverItem") continue;
                        if (!IsQuestItemTarget(tgt)) continue;
                        bool fir = c.TryGetProperty("onlyFoundInRaid", out var fv) && fv.ValueKind == JsonValueKind.True;
                        if (t == "FindItem") { officialFindQi++; if (fir) officialFindQiFir++; }
                        else { officialHandQi++; if (fir) officialHandQiFir++; }
                    }
                }
                Check(officialFindQi > 100 && officialFindQiFir == 0,
                      $"官方 QuestItem=true 的 FindItem 命中 {officialFindQi} 条，其中 onlyFoundInRaid=true 的 {officialFindQiFir} 条 ⇒ 惯例=false");
                Check(officialHandQi > 0 && officialHandQiFir <= 1,
                      $"官方 QuestItem=true 的 HandoverItem 命中 {officialHandQi} 条，其中 onlyFoundInRaid=true 的 {officialHandQiFir} 条");
            }
            // `_parent` 也是官方 QuestItem 物品在用的父类（148 个官方任务物品里有 10 个同 parent）
            Check(string.Equals(R.Prop(newItem, "Parent")?.ToString(), "590c745b86f7743cc433c5f2", StringComparison.OrdinalIgnoreCase),
                  "新物品 _parent=590c745b86f7743cc433c5f2（BarterItem；官方 QuestItem 物品共 10 个用同一父类）",
                  R.Prop(newItem, "Parent")?.ToString());


            // ── ④ 还原 + 切活动 + 切回 ──
            Console.WriteLine("\n[断言 ④] 还原 / 切到别的活动 / 再切回本活动");
            for (int i = restores.Count - 1; i >= 0; i--) restores[i]();
            var restoredJson = R.ToJson(lazy.Value);
            Check(restoredJson == baselineJson, "还原后 .Value 的序列化结果 == 注入前（逐字节）",
                  $"len {baselineJson?.Length} vs {restoredJson?.Length}");
            Check(lazy.Value.Spawnpoints.Count() == baseline.Count, "点位数量回到原值");

            restores.Clear();
            LooseLootInjectDimension.ResetApplied();          // PlugIn 切活动时先做这一步
            LooseLootInjectDimension.Apply(other);             // 切到 wild_wild_woods（它只管 woods，interchange 应直通）
            var afterOther = R.ToJson(lazy.Value);
            Check(afterOther == baselineJson, $"切到另一个活动（{OtherId}）后 interchange 仍是原样（逐字节）",
                  $"len {afterOther?.Length}");

            restores.Clear();
            LooseLootInjectDimension.ResetApplied();
            LooseLootInjectDimension.Apply(casus);             // 再切回本活动
            var again = lazy.Value.Spawnpoints.Count(sp => ItemTpl(sp) == NewTpl);
            Check(again == (region.MaxPoints ?? 0), $"切回 casus_belli 后重新生效，替换数 {again} = maxPoints {region.MaxPoints}");

            // ── ⑤ 容器类 tpl 诊断 warning（回归） ──
            Console.WriteLine("\n[断言 ⑤] 注入容器类 tpl（旧武器箱）时必须打 warning");
            var diagEv = new EventDef
            {
                Id = "probe_container_diag",
                LooseLootInject = new Dictionary<string, LooseLootInjectMapOpt>
                {
                    [MapKey] = new LooseLootInjectMapOpt
                    {
                        DensityMultiplier = 1.0,
                        Valuable = new LooseLootPoolOpt
                        {
                            Chance = 0,
                            Tpls = new Dictionary<string, int> { [OldTpl] = 1 }
                        },
                        Decor = new LooseLootPoolOpt { Chance = 0, Tpls = new Dictionary<string, int>() },
                        Regions = new List<LooseLootRegionOpt>
                        {
                            new LooseLootRegionOpt
                            {
                                Name = "诊断盒",
                                Min = new LooseLootPoint2D { X = region.Min.X, Z = region.Min.Z },
                                Max = new LooseLootPoint2D { X = region.Max.X, Z = region.Max.Z },
                                ValuableBoost = 1.0,
                                MaxPoints = 1
                            }
                        }
                    }
                }
            };
            restores.Clear();
            logLines.Clear();
            LooseLootInjectDimension.ResetApplied();
            LooseLootInjectDimension.Apply(diagEv);
            int oldReplaced = lazy.Value.Spawnpoints.Count(sp => ItemTpl(sp) == OldTpl);
            var warnLine = logLines.FirstOrDefault(l => l.Contains("容器类物品"));
            Check(oldReplaced >= 1, $"诊断事件确实把 {oldReplaced} 个点换成了容器类旧武器箱");
            Check(warnLine != null && warnLine.Contains(OldTpl),
                  "维度对容器类 tpl 打了 warning（这类当 loose loot 可能不显示）", warnLine);
            for (int i = restores.Count - 1; i >= 0; i--) restores[i]();

            // ── ⑥ 兜底方案（"在小黄车位置新增一个静态容器"）可行性：用**实机 interchange staticContainers.json** 自证 ──
            Console.WriteLine("\n[断言 ⑥] 兜底方案「车旁 append 一个静态容器」**不可行**的实测依据");
            var scPath = Path.Combine(Path.GetDirectoryName(srcLooseLoot) ?? "", "staticContainers.json");
            if (!File.Exists(scPath))
                Check(false, "找不到 interchange/staticContainers.json", scPath);
            else
            {
                using var scDoc = JsonDocument.Parse(File.ReadAllText(scPath));
                var scl = scDoc.RootElement.GetProperty("staticContainers");
                int total = scl.GetArrayLength(), nonZeroPos = 0, isContainer = 0;
                var scIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in scl.EnumerateArray())
                {
                    var t = c.GetProperty("template");
                    if (t.GetProperty("IsContainer").GetBoolean()) isContainer++;
                    var pos = t.GetProperty("Position");
                    if (pos.GetProperty("x").GetDouble() != 0 || pos.GetProperty("y").GetDouble() != 0
                        || pos.GetProperty("z").GetDouble() != 0) nonZeroPos++;
                    var id = t.GetProperty("Id").GetString();
                    if (!string.IsNullOrEmpty(id)) scIds.Add(id);
                }
                Check(total > 0 && nonZeroPos == 0,
                      $"实机 interchange 的 {total} 个静态容器 **Position 全是 (0,0,0)**（{nonZeroPos} 个非零）"
                      + " ⇒ 位置由客户端场景对象提供，服务端 JSON 里根本没有坐标可写");
                Check(scIds.Count == total, "每个静态容器的 Id 都是场景实例名（Lootable_00000…）且唯一", $"唯一 Id {scIds.Count}");
                Check(isContainer == total, "静态容器条目全部 IsContainer=true", $"{isContainer}/{total}");
                Console.WriteLine("      客户端 `SpawnLoot`（Assembly-CSharp.decompiled.cs:552171-552231）对 IsContainer 刷点的处理：");
                Console.WriteLine("        var staticLoot = array2.FirstOrDefault(x => x.Id == lootId);   // array2 = 场景里已有的 StaticLoot/LootableContainer/StationaryWeapon");
                Console.WriteLine("        if (staticLoot == null) UnityEngine.Debug.LogError(lootId + \" is missing\");   // ⇒ 新 Id 在场景里不存在 ⇒ 什么都不生成");
                Console.WriteLine("      ⇒ 想要\"新增一个能开的箱子\"，得往**客户端地图场景**里放对象（Unity 资产），服务端 append 一条 JSON 做不到。");
            }

            Console.WriteLine("\n" + "=".PadRight(96, '='));
            Console.WriteLine($"结果：{Pass} 项通过 / {Fail} 项失败");
            return Fail == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    // ── 工具 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 解析 `TemplateItem`。⚠ **不能**用 R.FromJson：R 的 options 开了
    /// `PropertyNameCaseInsensitive = true`，而 SPT 的 `TemplateItemProperties` 里有一对
    /// 只差大小写的属性（`ShotgunDispersion` / `shotgunDispersion`，各自带 JsonPropertyName）
    /// ⇒ STJ 会抛 "JSON property name ... collides"。SPT 自己的 JsonUtil 用的是**大小写敏感** options。
    /// 这里按 SPT 同款建 options（MongoId 转换器 + 枚举转换器），只为读数据，不改生产代码。
    /// </summary>
    private static TemplateItem ParseTemplateItem(string json)
    {
        var o = new JsonSerializerOptions { PropertyNameCaseInsensitive = false };
        var tConv = Type.GetType("SPTarkov.Server.Core.Utils.Json.Converters.StringToMongoIdConverter, SPTarkov.Server.Core", false);
        if (tConv != null && Activator.CreateInstance(tConv) is System.Text.Json.Serialization.JsonConverter conv) o.Converters.Add(conv);
        var tEnum = Type.GetType("SPTarkov.Server.Core.Utils.Json.Converters.EftEnumConverterFactory, SPTarkov.Server.Core", false);
        if (tEnum != null && Activator.CreateInstance(tEnum) is System.Text.Json.Serialization.JsonConverter econv) o.Converters.Add(econv);
        return JsonSerializer.Deserialize<TemplateItem>(json, o);
    }

    private static List<EventDef> LoadEvents(string path)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<List<EventDef>>(File.ReadAllText(path), opts);
    }

    private static int CountSpawnpoints(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("spawnpoints").GetArrayLength();
        }
        catch { return -1; }
    }

    /// <summary>点位的单物品 tpl（多物品刷点返回 null；维度把替换点收敛成单物品）。</summary>
    private static string ItemTpl(Spawnpoint sp)
    {
        var items = sp?.Template?.Items?.ToList();
        return items != null && items.Count == 1 ? items[0].Template.ToString() : null;
    }

    private static bool IsReplaceable(Spawnpoint sp)
    {
        if (sp?.Template == null) return false;
        if (sp.Template.IsAlwaysSpawn == true) return false;
        var id = sp.Template.Id;
        if (!string.IsNullOrEmpty(id) && id.StartsWith("christmas", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool TryXZ(Spawnpoint sp, out double x, out double z)
    {
        x = z = double.NaN;
        var pos = sp?.Template?.Position;
        if (pos == null) return false;
        x = pos.Value.X; z = pos.Value.Z;
        return true;
    }

    private static double Dist3(double x1, double y1, double z1, double x2, double y2, double z2)
        => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2) + (z1 - z2) * (z1 - z2));

    /// <summary>盒内命中点（探针自己算的 3D 坐标 + 到锚点距离 + 原始概率）。</summary>
    private sealed class Hit
    {
        public double Dist, X, Y, Z;
        public Spawnpoint Sp;
    }

    private sealed class FakeLazy<T>
    {
        private readonly Func<T> _deserialize;
        private readonly List<Func<T, T>> _transformers = new();
        public FakeLazy(Func<T> deserialize) { _deserialize = deserialize; }
        public T Value
        {
            get
            {
                var v = _deserialize();
                foreach (var t in _transformers) v = t(v);
                return v;
            }
        }
        public void AddTransformer(Func<T, T> transformer) { _transformers.Add(transformer); }
    }

    private sealed class FakeTemplateTable
    {
        public Dictionary<MongoId, TemplateItem> Items { get; set; }
    }

    private sealed class FakeLocationTable
    {
        private readonly Dictionary<string, object> _dict;
        public FakeLocationTable(Dictionary<string, object> d) { _dict = d; }
        public IDictionary GetDictionary() => _dict;
    }

    private sealed class FakeLocation
    {
        public FakeLazy<LooseLoot> LooseLoot { get; set; }
    }
}
