// ─────────────────────────────────────────────────────────────────────────────
//  ProbeLooseLoot —— `looseLootInject` 离线探针
//
//  目标：在**实机 woods/looseLoot.json 的临时副本**上跑**真身**代码
//        （直接 <Compile Include="../LooseLootInjectDimension.cs" /> 链接，不是重写一份），断言：
//    ① 点位数按 densityMultiplier 增加（并缩放 SpawnpointCount.Mean）
//    ② 新增点位的 _id / Root / Template.Id 全唯一
//    ③ 无坐标落在偏移量上限之外（x/z 模长 ∈ [0.4,1.2]，y 与源点**逐位相同**）
//    ④ 所有 _tpl 都能在实机 items.json 里解析
//    ⑤ "还原"后重新读 .Value 的序列化结果与注入前**逐字节相同**（transformer 转直通）
//    ⑥ LocationId 全唯一（SPT 会 group by LocationId，撞了就被合并）
//    ⑦ 每个点位 ItemDistribution 的 composedKey 都能在 Items 里找到（否则局内抽不到物品）
//    ⑧ regions 包围盒语义单测（盒内 valuable 概率被 boost 到 1.0 ⇒ 盒内点位 100% 被替换）
//    ⑨ 连续两次 .Value 都得到同样结果（transformer 每局在**新鲜对象**上跑，绝不累积）
//
//  ★ 关键建模：实机 `Location.LooseLoot` 是 `LazyLoad<LooseLoot>`，且 `LooseLoot` **没有**
//    `[CacheLazyLoad]` ⇒ `cacheValue = false` ⇒ **每次读 .Value 都重新反序列化**。
//    探针里的 `FakeLazy<T>` 逐字复刻这个语义（含 transformer 列表），否则测不出真行为。
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
using SPTarkov.Server.Core.Models.Eft.Common;

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
                return File.Exists(p) ? Assembly.LoadFrom(p) : null;
            }
            catch { return null; }
        };

        ModDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var spt = args.Length > 0 ? args[0] : DefaultSpt;
        var srcLooseLoot = Path.Combine(spt, "SPT_Data", "database", "locations", MapKey, "looseLoot.json");
        var itemsPath = Path.Combine(spt, "SPT_Data", "database", "templates", "items.json");
        var eventsPath = Path.Combine(ModDir, "events.json");

        Console.WriteLine("=".PadRight(100, '='));
        Console.WriteLine("looseLootInject 离线探针（真身代码 + 实机数据副本）");
        Console.WriteLine("  mod 目录   : " + ModDir);
        Console.WriteLine("  实机数据   : " + srcLooseLoot);
        foreach (var p in new[] { srcLooseLoot, itemsPath, eventsPath })
            if (!File.Exists(p)) { Console.WriteLine("[FATAL] 缺文件: " + p); return 2; }

        // ★ 临时副本的根目录：默认系统 temp；某些受限环境里 .NET 写不了系统 temp，
        //   用 PROBE_TMP=<工作区内某个目录> 覆盖即可（本探针只读实机、只写这里）。
        var tmpRoot = Environment.GetEnvironmentVariable("PROBE_TMP");
        if (string.IsNullOrWhiteSpace(tmpRoot)) tmpRoot = Path.GetTempPath();
        var tmpDir = Path.Combine(tmpRoot, "looseloot_probe_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmpDir);
        var tmpLoose = Path.Combine(tmpDir, "looseLoot.json");
        File.Copy(srcLooseLoot, tmpLoose, true);
        Console.WriteLine($"  临时副本   : {tmpLoose}  ({new FileInfo(tmpLoose).Length:N0} bytes)");
        Console.WriteLine($"  实机文件只读：探针全程不碰 {srcLooseLoot}（副本退出即删）");

        try
        {
            var jsonText = File.ReadAllText(tmpLoose);
            var itemTpls = LoadItemTpls(itemsPath);
            Console.WriteLine($"\n[0] items.json 载入：{itemTpls.Count} 个 tpl");

            // 复刻 LazyLoad<LooseLoot>（cacheValue=false）：每次 .Value 都重新反序列化 + 过 transformer
            var lazy = new FakeLazy<LooseLoot>(() => (LooseLoot)R.FromJson(typeof(LooseLoot), jsonText,
                                                                            m => Console.WriteLine("  [json] " + m)));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var baselineObj = lazy.Value;
            Console.WriteLine($"[0] 首次读 .Value（反序列化）：{sw.ElapsedMilliseconds} ms，"
                              + $"spawnpoints={baselineObj.Spawnpoints.Count()}、forced={baselineObj.SpawnpointsForced?.Count() ?? 0}");

            // 实机 events.json 里的真配置（验证 JSON 能真正解析进模型）
            var ev = LoadEvent(eventsPath);
            if (ev == null) { Console.WriteLine("[FATAL] events.json 里没有带 looseLootInject 的活动"); return 2; }
            var opt = ev.LooseLootInject[MapKey];
            Console.WriteLine($"[0] events.json → '{ev.Id}'，onlyOnMap='{ev.OnlyOnMap}'，"
                              + $"densityMultiplier={opt.DensityMultiplier}、valuable.chance={opt.Valuable?.Chance}、"
                              + $"decor.chance={opt.Decor?.Chance}、regions={opt.Regions?.Count ?? 0}");

            // ---- 注入前快照（注意：这些是**另一份对象**，transformer 不会改到它们）----
            var origSpawnpoints = baselineObj.Spawnpoints.ToList();
            var origCount = origSpawnpoints.Count;
            var origMean = baselineObj.SpawnpointCount?.Mean ?? double.NaN;
            var origTemplateIds = new HashSet<string>(origSpawnpoints.Select(s => s.Template?.Id).Where(x => x != null),
                                                      StringComparer.OrdinalIgnoreCase);
            var origPositions = origSpawnpoints.Select(GetXyz).Where(p => p.HasValue).Select(p => p.Value).ToList();
            var injected = MergePool(opt);
            var beforeHits = CountPoolSingle(origSpawnpoints, injected);
            var preSingleByLoc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sp in origSpawnpoints)
            {
                var its = sp.Template?.Items?.ToList();
                preSingleByLoc[sp.LocationId ?? ""] = (its != null && its.Count == 1) ? its[0].Template.ToString() : null;
            }

            sw.Restart();
            var baseline = R.ToJson(baselineObj);
            Console.WriteLine($"[0] 基线序列化：{sw.ElapsedMilliseconds} ms，{baseline?.Length ?? 0} chars");
            Check(baseline != null && baseline.Length > 1000, "基线序列化成功");

            // ---- 跑真身维度 ----
            var restores = new List<Action>();
            LooseLootInjectDimension.LocationTable = new FakeLocationTable(new Dictionary<string, object>
            {
                [MapKey] = new FakeLocation { LooseLoot = lazy }
            });
            var logLines = new List<string>();
            LooseLootInjectDimension.Log = m => { logLines.Add(m); Console.WriteLine("  " + m); };
            LooseLootInjectDimension.AddRestore = a => restores.Add(a);
            LooseLootInjectDimension.ResetApplied();

            sw.Restart();
            LooseLootInjectDimension.Apply(ev);
            Console.WriteLine($"\n[1] Apply 完成：{sw.ElapsedMilliseconds} ms（含自证读取一次 .Value），登记还原闭包 {restores.Count} 个");

            // ---- 再读一次 .Value 拿"注入了的"对象（本局真正会用的那种）----
            sw.Restart();
            var afterLoose = lazy.Value;
            var after = afterLoose.Spawnpoints.ToList();
            Console.WriteLine($"[1] 读 .Value（注入后）：{sw.ElapsedMilliseconds} ms，spawnpoints={after.Count}");
            int expected = (int)Math.Round(origCount * (opt.DensityMultiplier ?? 1.0));

            // ── ① 点位数 ────────────────────────────────────────────────────
            Console.WriteLine("\n[断言 ①] 点位数按倍数增加");
            Check(after.Count == expected, $"点位数 {origCount} → {after.Count}（期望 {expected}）", $"×{opt.DensityMultiplier}");
            Check(Math.Abs((afterLoose.SpawnpointCount?.Mean ?? 0) - origMean * (opt.DensityMultiplier ?? 1)) < 1e-6,
                  $"SpawnpointCount.Mean 同步缩放 {origMean:0.0000} → {afterLoose.SpawnpointCount?.Mean:0.0000}",
                  "否则局内抽取数量不变（IL 证：num=round(mult×Normal(Mean,Std))）");

            // ── ② 唯一性 ────────────────────────────────────────────────────
            Console.WriteLine("\n[断言 ②] 新增点位 _id / Root / Template.Id 全唯一");
            var added = after.Skip(origCount).ToList();
            Check(added.Count > 0, "确实新增了点位", $"+{added.Count}");
            var addedTplIds = added.Select(s => s.Template?.Id).Where(x => x != null).ToList();
            var dupTplIds = after.Select(s => s.Template?.Id).Where(x => x != null)
                                 .GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
            Check(addedTplIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() == addedTplIds.Count,
                  $"克隆点 Template.Id 彼此不重复（{addedTplIds.Count} 个）");
            Check(addedTplIds.All(id => !origTemplateIds.Contains(id)),
                  "克隆点 Template.Id 与原点位不撞",
                  $"原点位自带重复 Id {dupTplIds.Count} 组（本体数据如此，不是本次引入的）");

            var allItemIds = new List<string>();
            var allRoots = new List<string>();
            foreach (var sp in after)
            {
                if (sp.Template?.Items == null) continue;
                foreach (var it in sp.Template.Items) allItemIds.Add(it.Id.ToString());
                if (sp.Template.Root != null) allRoots.Add(sp.Template.Root);
            }
            Check(allItemIds.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).All(g => g.Count() == 1),
                  "全部 Items._id 全表唯一", $"共 {allItemIds.Count} 个");
            Check(allRoots.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).All(g => g.Count() == 1),
                  "全部 Template.Root 唯一", $"共 {allRoots.Count} 个");
            Check(after.All(s => s.Template?.Items != null && s.Template.Items.Any(it => it.Id.ToString() == s.Template.Root)),
                  "每个点位都有某个 Item._id == Template.Root（Root 不悬空）");

            // ── ⑥ LocationId 唯一 ───────────────────────────────────────────
            Console.WriteLine("\n[断言 ⑥] LocationId 全唯一（SPT GenerateDynamicLoot 末尾 group by LocationId）");
            var locIds = after.Select(s => s.LocationId).Where(x => x != null).ToList();
            Check(locIds.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).All(g => g.Count() == 1),
                  "LocationId 全唯一", $"共 {locIds.Count} 个");

            // ── ③ 坐标偏移上限 ──────────────────────────────────────────────
            Console.WriteLine("\n[断言 ③] 新增点位坐标只做 x/z 偏移、模长 ∈[0.4,1.2]m、y 不变");
            int badR = 0, badY = 0; double minR = double.MaxValue, maxR = 0;
            // 配对方式：克隆点的 `Template.Id` = 源点 Id 去掉尾部 "[uuid]" + 新 "[uuid]"
            //   ⇒ 用"去括号前缀"配回**真正的源点**（不能用最近邻：地图上本来就有相距 <0.4m 的其它原点位）
            var origByBase = new Dictionary<string, List<(double X, double Y, double Z)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var sp in origSpawnpoints)
            {
                var id = sp.Template?.Id;
                var p0 = GetXyz(sp);
                if (id == null || !p0.HasValue) continue;
                var b0 = BaseId(id);
                if (!origByBase.TryGetValue(b0, out var lst0)) origByBase[b0] = lst0 = new List<(double, double, double)>();
                lst0.Add(p0.Value);
            }
            foreach (var sp in added)
            {
                var id = sp.Template?.Id;
                var p = GetXyz(sp);
                if (id == null || !p.HasValue) { badR++; continue; }
                if (!origByBase.TryGetValue(BaseId(id), out var cands) || cands.Count == 0) { badY++; continue; }
                bool anySameY = false, anyInRange = false;
                double bestInRange = double.MaxValue;
                foreach (var q in cands)
                {
                    if (q.Y != p.Value.Y) continue;
                    anySameY = true;
                    double d = Math.Sqrt((p.Value.X - q.X) * (p.Value.X - q.X) + (p.Value.Z - q.Z) * (p.Value.Z - q.Z));
                    if (d >= 0.4 - 1e-3 && d <= 1.2 + 1e-3) { anyInRange = true; bestInRange = Math.Min(bestInRange, d); }
                }
                if (!anySameY) { badY++; continue; }
                if (!anyInRange) { badR++; continue; }
                minR = Math.Min(minR, bestInRange); maxR = Math.Max(maxR, bestInRange);
            }
            Check(badY == 0, "所有克隆点都能按 Id 前缀配回源点、且 y 与源点逐位相同", $"配对失败 {badY}");
            Check(badR == 0, "所有克隆点与源点的水平距离 ∈ [0.4, 1.2] m", $"越界 {badR}；实测 {minR:0.000}~{maxR:0.000} m");

            // ── ④ tpl 可解析 ───────────────────────────────────────────────
            Console.WriteLine("\n[断言 ④] 所有 _tpl 都能在 items.json 解析");
            var allTpls = after.SelectMany(s => s.Template?.Items ?? Enumerable.Empty<SptLootItem>())
                               .Select(it => it.Template.ToString()).Distinct().ToList();
            var unknown = allTpls.Where(t => !itemTpls.Contains(t)).ToList();
            Check(unknown.Count == 0, $"全部 _tpl 可解析（distinct {allTpls.Count} 个）",
                  unknown.Count == 0 ? "含注入的 moonshine/彩卡/符号钥匙/水/玩偶" : "缺失: " + string.Join(",", unknown.Take(5)));

            // ── ⑦ ItemDistribution 一致性 ──────────────────────────────────
            Console.WriteLine("\n[断言 ⑦] ItemDistribution 的 composedKey 都能在 Items 里找到（否则局内抽空）");
            int badDist = 0;
            foreach (var sp in after)
            {
                var items = sp.Template?.Items?.ToList() ?? new List<SptLootItem>();
                var keys = new HashSet<string>(items.Select(i => i.ComposedKey).Where(k => k != null), StringComparer.Ordinal);
                var dist = sp.ItemDistribution?.ToList() ?? new List<LooseLootItemDistribution>();
                if (dist.Count == 0 || items.Count == 0) { badDist++; continue; }
                if (dist.Any(d => d.ComposedKey?.Key == null || !keys.Contains(d.ComposedKey.Key))) badDist++;
            }
            Check(badDist == 0, "全部点位 Items ↔ ItemDistribution 对齐", $"不一致 {badDist} 个");
            int single = after.Count(s => (s.ItemDistribution?.Count() ?? 0) == 1);
            Console.WriteLine($"      单物品刷点 {single} 个 / 多物品刷点 {after.Count - single} 个"
                              + "（注入把命中的点多物品收敛成单物品，见维度注释 IL 铁证 ③）");

            // ── ⑨ 不累积（再读一次 .Value）──────────────────────────────────
            Console.WriteLine("\n[断言 ⑨] transformer 每局在新鲜对象上跑 ⇒ 连续两次 .Value 结果一致（不累积）");
            var again = lazy.Value;
            int againUnique = again.Spawnpoints.Select(s => s.Template?.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int expectUnique = origTemplateIds.Count + addedTplIds.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Check(again.Spawnpoints.Count() == expected && againUnique == expectUnique,
                  "第二次读 .Value 仍是同样点位数（没有 2 倍/4 倍累积）",
                  $"{again.Spawnpoints.Count()} 点；唯一 Id {againUnique} = 原点位唯一 {origTemplateIds.Count} + 克隆 {addedTplIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()}");

            // ── 统计 ───────────────────────────────────────────────────────
            Console.WriteLine("\n[实测数字] 替换分布（森林）");
            var afterHits = CountPoolSingle(after, injected);
            int preSum = beforeHits.Values.Sum(), postSum = afterHits.Values.Sum();
            int replacedExact = 0;
            foreach (var sp in after)
            {
                var items = sp.Template?.Items?.ToList();
                if (items == null || items.Count != 1) continue;
                var tpl = items[0].Template.ToString();
                if (!injected.ContainsKey(tpl)) continue;
                if (!preSingleByLoc.TryGetValue(sp.LocationId ?? "", out var preTpl)) { replacedExact++; continue; }  // 克隆点
                if (!string.Equals(preTpl, tpl, StringComparison.OrdinalIgnoreCase)) replacedExact++;
            }
            int dimReported = 0;
            foreach (var line in logLines)
            {
                var mm = System.Text.RegularExpressions.Regex.Match(line, @"物品替换 valuable (\d+) / decor (\d+)");
                if (mm.Success) dimReported = int.Parse(mm.Groups[1].Value) + int.Parse(mm.Groups[2].Value);
            }
            foreach (var kv in afterHits.OrderByDescending(x => x.Value))
            {
                int pre = beforeHits.TryGetValue(kv.Key, out var b) ? b : 0;
                Console.WriteLine($"      {kv.Key}  (w={injected[kv.Key]})  {pre} → {kv.Value}   Δ{kv.Value - pre:+#;-#;0}");
            }
            Check(postSum > preSum, $"注入池点位：{preSum} → {postSum}（净 +{postSum - preSum}）",
                  $"维度自报替换 {dimReported} 处；探针独立计 {replacedExact}（差额来自「未替换的克隆点本身恰好是池点位」）");
            double frac = 1.0 * replacedExact / after.Count;
            double drawPerRaid = 3.8 * (afterLoose.SpawnpointCount?.Mean ?? 0);   // location.json woods=1.9 × 本活动 2.0
            Console.WriteLine($"      ★ 估算局内实际落地的注入替换物 ≈ {drawPerRaid:0} 抽取点 × {100.0 * frac:0.00}% ≈ {drawPerRaid * frac:0} 件/局"
                              + "（location.json woods looseLootMultiplier=1.9 × 本活动 2.0 = 3.8）");

            // ── ⑤ 还原逐字节 ───────────────────────────────────────────────
            Console.WriteLine("\n[断言 ⑤] 还原（transformer 转直通）后重新读 .Value，与注入前逐字节相同");
            for (int i = restores.Count - 1; i >= 0; i--) restores[i]();
            var restoredObj = lazy.Value;
            var restored = R.ToJson(restoredObj);
            Check(restored == baseline, "还原后 .Value 的序列化结果 == 注入前（逐字节）",
                  $"len {baseline?.Length} vs {restored?.Length}");
            Check(restoredObj.Spawnpoints.Count() == origCount
                  && Math.Abs((restoredObj.SpawnpointCount?.Mean ?? 0) - origMean) < 1e-12,
                  "点位数量与 SpawnpointCount.Mean 都回到原值");
            Check(!ReferenceEquals(restoredObj, baselineObj), "还原后确实是新反序列化的对象（证明没有改动被缓存）");
            var restoreFile = Path.Combine(tmpDir, "restored.json");
            File.WriteAllText(restoreFile, restored ?? "");
            var origFile = Path.Combine(tmpDir, "baseline.json");
            File.WriteAllText(origFile, baseline ?? "");
            Check(new FileInfo(restoreFile).Length == new FileInfo(origFile).Length
                  && File.ReadAllBytes(restoreFile).SequenceEqual(File.ReadAllBytes(origFile)),
                  "落盘文件字节级相等", $"{new FileInfo(restoreFile).Length} bytes");

            // ── ⑧ regions 单测（换一个 mapKey，避免复用已挂 transformer 的 LazyLoad）─────
            Console.WriteLine("\n[断言 ⑧] regions 包围盒语义（盒内 valuableBoost 把概率顶到 1.0）");
            RunRegionTest(jsonText, itemTpls);

            // ── ⑩ maxPoints（区内确定性替换上限）─────────────────────────────────
            Console.WriteLine("\n[断言 ⑩] maxPoints：区内按「距圆心近→远」最多替换 N 个，超出上限退回全局概率");
            RunCapTest(jsonText);

            // ── ⑪ 重叠区：maxPoints 小者优先，同一位置只替换一次 ─────────────────
            Console.WriteLine("\n[断言 ⑪] 重叠区优先级：maxPoints 小者先生效（声明顺序无关），点位不重复替换");
            RunOverlapTest(jsonText);

            Console.WriteLine("\n" + "=".PadRight(100, '='));
            Console.WriteLine($"结果：{Pass} 项通过 / {Fail} 项失败");
            return Fail == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    // ── regions 单测：盒子只包住 1 个点位，valuable.chance=0.5 + boost=2 ⇒ 盒内必 100% 被替换 ──
    private static void RunRegionTest(string looseJson, HashSet<string> itemTpls)
    {
        const string RegionMapKey = "woods_probe_regions";
        try
        {
            var lazy = new FakeLazy<LooseLoot>(() => (LooseLoot)R.FromJson(typeof(LooseLoot), looseJson));
            var probe0 = lazy.Value;
            var target = probe0.Spawnpoints.First(s => GetXyz(s).HasValue);
            var t = GetXyz(target).Value;

            var ev = new EventDef
            {
                Id = "probe_regions",
                LooseLootInject = new Dictionary<string, LooseLootInjectMapOpt>
                {
                    [RegionMapKey] = new LooseLootInjectMapOpt
                    {
                        DensityMultiplier = 1.0,
                        Valuable = new LooseLootPoolOpt
                        {
                            Chance = 0.5,
                            Tpls = new Dictionary<string, int> { ["5d1b376e86f774252519444e"] = 1 }
                        },
                        Decor = new LooseLootPoolOpt { Chance = 0, Tpls = new Dictionary<string, int>() },
                        Regions = new List<LooseLootRegionOpt>
                        {
                            new LooseLootRegionOpt
                            {
                                Name = "probe-box",
                                Min = new LooseLootPoint2D { X = t.X - 0.001, Z = t.Z - 0.001 },
                                Max = new LooseLootPoint2D { X = t.X + 0.001, Z = t.Z + 0.001 },
                                ValuableBoost = 2.0          // 0.5 × 2 = 1.0 ⇒ 盒内必中
                            }
                        }
                    }
                }
            };

            var restores = new List<Action>();
            LooseLootInjectDimension.LocationTable = new FakeLocationTable(new Dictionary<string, object>
            {
                [RegionMapKey] = new FakeLocation { LooseLoot = lazy }
            });
            LooseLootInjectDimension.Log = m => { };
            LooseLootInjectDimension.AddRestore = a => restores.Add(a);
            LooseLootInjectDimension.Apply(ev);

            var baselineJson = R.ToJson(probe0);          // Apply 之前读到的对象（无 transformer）
            var injected = lazy.Value;                    // ← Apply 已挂 transformer；这一次读到的才是注入后的

            var inBox = injected.Spawnpoints.Where(sp =>
            {
                var p = GetXyz(sp);
                return p.HasValue && Math.Abs(p.Value.X - t.X) <= 1e-3 && Math.Abs(p.Value.Z - t.Z) <= 1e-3;
            }).ToList();
            Check(inBox.Count >= 1 && inBox.All(sp => IsSingleTpl(sp, "5d1b376e86f774252519444e")),
                  $"盒内 {inBox.Count} 个点位全部被替换成目标 tpl（0.5×2.0=1.0 ⇒ 100%）");
            int replaced = injected.Spawnpoints.Count(sp => IsSingleTpl(sp, "5d1b376e86f774252519444e"));
            Check(replaced > inBox.Count && replaced < injected.Spawnpoints.Count(),
                  $"盒外只按 0.5 概率替换（总替换 {replaced} 个，非全量 {injected.Spawnpoints.Count()}）");
            for (int i = restores.Count - 1; i >= 0; i--) restores[i]();
            var restored = R.ToJson(lazy.Value);
            Check(restored == baselineJson, "regions 单测同样可完整还原（逐字节）");
        }
        catch (Exception ex)
        {
            Check(false, "regions 单测异常：" + ex.Message);
        }
    }

    private static bool IsSingleTpl(object sp, string tpl)
    {
        var items = (sp as Spawnpoint)?.Template?.Items?.ToList();
        return items != null && items.Count == 1 && string.Equals(items[0].Template.ToString(), tpl, StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ⑩ / ⑪ maxPoints（区内确定性替换上限）与重叠区优先级
    //  用真身配置跑在实机 woods 的真实点位上：盒子选「三联房」(-1.13,-74.74) r=8
    // ══════════════════════════════════════════════════════════════════════════

    private const double TX = -1.13, TZ = -74.74, TR = 8.0;
    private const string CapTpl = "5d1b376e86f774252519444e";   // moonshine：本机 woods 基线里 0 个

    /// <summary>盒子内"可替换"点位（与 C# IsReplaceable 同规则），按 (到圆心距离, 文件顺序) 排序。</summary>
    private static List<(string Loc, double D)> BoxPoints(List<Spawnpoint> list, double cx, double cz, double r)
    {
        var res = new List<(string, double)>();
        for (int i = 0; i < list.Count; i++)
        {
            var sp = list[i];
            if (sp?.Template == null) continue;
            if (sp.Template.IsAlwaysSpawn == true) continue;
            var id = sp.Template.Id;
            if (!string.IsNullOrEmpty(id) && id.StartsWith("christmas", StringComparison.OrdinalIgnoreCase)) continue;
            var p = GetXyz(sp);
            if (p == null) continue;
            if (Math.Abs(p.Value.X - cx) > r || Math.Abs(p.Value.Z - cz) > r) continue;
            res.Add((sp.LocationId ?? ("#" + i), Math.Sqrt(Math.Pow(p.Value.X - cx, 2) + Math.Pow(p.Value.Z - cz, 2))));
        }
        // C# 是 (距离, 列表下标) —— 这里 list 顺序即文件顺序，等价
        return res.OrderBy(t => t.Item2).ToList();
    }

    private static HashSet<string> ReplacedLocIds(LooseLoot loot, string tpl)
        => new HashSet<string>(loot.Spawnpoints.Where(s => IsSingleTpl(s, tpl)).Select(s => s.LocationId ?? ""),
                              StringComparer.OrdinalIgnoreCase);

    /// <summary>⑩ maxPoints：盒内 cap=3、valuable.chance=0 ⇒ 只有"距圆心最近的 3 个"被确定性替换。</summary>
    private static void RunCapTest(string looseJson)
    {
        const string key = "woods_probe_cap";
        try
        {
            var lazy = new FakeLazy<LooseLoot>(() => (LooseLoot)R.FromJson(typeof(LooseLoot), looseJson));
            var baseline = lazy.Value;
            var box = BoxPoints(baseline.Spawnpoints.ToList(), TX, TZ, TR);
            var ev = new EventDef
            {
                Id = "probe_cap",
                LooseLootInject = new Dictionary<string, LooseLootInjectMapOpt>
                {
                    [key] = new LooseLootInjectMapOpt
                    {
                        DensityMultiplier = 1.0,
                        Valuable = new LooseLootPoolOpt { Chance = 0, Tpls = new Dictionary<string, int> { [CapTpl] = 1 } },
                        Regions = new List<LooseLootRegionOpt>
                        {
                            new LooseLootRegionOpt
                            {
                                Name = "cap-box",
                                Min = new LooseLootPoint2D { X = TX - TR, Z = TZ - TR },
                                Max = new LooseLootPoint2D { X = TX + TR, Z = TZ + TR },
                                ValuableBoost = 1.0,
                                MaxPoints = 3
                            }
                        }
                    }
                }
            };
            var restores = new List<Action>();
            LooseLootInjectDimension.LocationTable = new FakeLocationTable(new Dictionary<string, object>
            {
                [key] = new FakeLocation { LooseLoot = lazy }
            });
            var logLines = new List<string>();
            LooseLootInjectDimension.Log = m => logLines.Add(m);
            LooseLootInjectDimension.AddRestore = a => restores.Add(a);
            LooseLootInjectDimension.Apply(ev);
            var injected = lazy.Value;

            Check(box.Count > 3, $"盒内可替换点位 {box.Count} 个（> cap 3，截断才有意义）");
            var got = ReplacedLocIds(injected, CapTpl);
            var want = box.Take(3).Select(t => t.Loc).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Check(got.Count == 3, $"valuable.chance=0 下**只有** maxPoints=3 个点被替换（实际 {got.Count}）");
            Check(got.SetEquals(want), "被替换的正是**距圆心最近的 3 个**（中心最肥）",
                  $"最近 3 个距离 {string.Join("/", box.Take(3).Select(t => t.Item2.ToString("0.00")))} m");
            var capLine = logLines.FirstOrDefault(l => l.Contains("region「cap-box」"));
            Check(capLine != null && capLine.Contains("命中 " + box.Count + " 个") && capLine.Contains("实际替换 3 个"),
                  "日志逐字格式：命中 N 个，实际替换 M 个（上限 X）", capLine);

            // ── ⑩b：把 chance 打开 ⇒ 区内**超出上限**的点位照旧走全局概率（替换数 > 3）──
            var ev2 = new EventDef
            {
                Id = "probe_cap2",
                LooseLootInject = new Dictionary<string, LooseLootInjectMapOpt>
                {
                    [key] = new LooseLootInjectMapOpt
                    {
                        DensityMultiplier = 1.0,
                        Valuable = new LooseLootPoolOpt { Chance = 0.5, Tpls = new Dictionary<string, int> { [CapTpl] = 1 } },
                        Regions = ev.LooseLootInject[key].Regions
                    }
                }
            };
            LooseLootInjectDimension.ResetApplied();
            LooseLootInjectDimension.Apply(ev2);
            var injected2 = lazy.Value;
            int got2 = injected2.Spawnpoints.Count(s => IsSingleTpl(s, CapTpl));
            Check(got2 > 3, $"区内超出上限的点位维持全局 valuable.chance=0.5（替换 {got2} 个 > 上限 3）");
            var got2Ids = ReplacedLocIds(injected2, CapTpl);
            Check(want.IsSubsetOf(got2Ids), "确定性替换的 3 个点不受全局掷骰影响（仍被替换）");

            for (int i = restores.Count - 1; i >= 0; i--) restores[i]();
        }
        catch (Exception ex)
        {
            Check(false, "maxPoints 单测异常：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>⑪ 重叠：内盒 cap=3 + 外盒 cap=12 套住同一片 ⇒ 内盒（cap 小）先吃自己 3 个，
    /// 其余内盒点**不会**被外盒再替换一次；总替换 = 3 + (外盒非内盒点里前 12 个)。</summary>
    private static void RunOverlapTest(string looseJson)
    {
        const string key = "woods_probe_overlap";
        const double OR = 60.0;      // 外盒半径
        const int OuterCap = 12;
        try
        {
            var lazy = new FakeLazy<LooseLoot>(() => (LooseLoot)R.FromJson(typeof(LooseLoot), looseJson));
            var baseline = lazy.Value;
            var list = baseline.Spawnpoints.ToList();
            var inner = BoxPoints(list, TX, TZ, TR);
            var outer = BoxPoints(list, TX, TZ, OR);
            var outerOnly = outer.Where(t => !inner.Any(i => string.Equals(i.Loc, t.Loc, StringComparison.OrdinalIgnoreCase)))
                                 .Take(OuterCap).ToList();

            var ev = new EventDef
            {
                Id = "probe_overlap",
                LooseLootInject = new Dictionary<string, LooseLootInjectMapOpt>
                {
                    [key] = new LooseLootInjectMapOpt
                    {
                        DensityMultiplier = 1.0,
                        Valuable = new LooseLootPoolOpt { Chance = 0, Tpls = new Dictionary<string, int> { [CapTpl] = 1 } },
                        Regions = new List<LooseLootRegionOpt>
                        {
                            // 故意把大区写在**前面**：优先级只看 maxPoints，不看声明顺序
                            new LooseLootRegionOpt
                            {
                                Name = "outer-big",
                                Min = new LooseLootPoint2D { X = TX - OR, Z = TZ - OR },
                                Max = new LooseLootPoint2D { X = TX + OR, Z = TZ + OR },
                                ValuableBoost = 1.0,
                                MaxPoints = OuterCap
                            },
                            new LooseLootRegionOpt
                            {
                                Name = "inner-small",
                                Min = new LooseLootPoint2D { X = TX - TR, Z = TZ - TR },
                                Max = new LooseLootPoint2D { X = TX + TR, Z = TZ + TR },
                                ValuableBoost = 1.0,
                                MaxPoints = 3
                            }
                        }
                    }
                }
            };
            var restores = new List<Action>();
            var logLines = new List<string>();
            LooseLootInjectDimension.LocationTable = new FakeLocationTable(new Dictionary<string, object>
            {
                [key] = new FakeLocation { LooseLoot = lazy }
            });
            LooseLootInjectDimension.Log = m => logLines.Add(m);
            LooseLootInjectDimension.AddRestore = a => restores.Add(a);
            LooseLootInjectDimension.Apply(ev);
            var injected = lazy.Value;
            var got = ReplacedLocIds(injected, CapTpl);

            var wantInner = inner.Take(3).Select(t => t.Loc).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var wantOuter = outerOnly.Select(t => t.Loc).ToHashSet(StringComparer.OrdinalIgnoreCase);

            Check(wantInner.IsSubsetOf(got), "内盒（maxPoints=3）的 3 个点被替换 —— **声明顺序在后也优先**（cap 小者先）");
            Check(wantOuter.IsSubsetOf(got), $"外盒（maxPoints={OuterCap}）替换了它自己那 {outerOnly.Count} 个非内盒点");
            Check(got.Count == 3 + outerOnly.Count,
                  $"同一位置只替换一次：总替换 {got.Count} = 内盒 3 + 外盒 {outerOnly.Count}", null);
            // 内盒里**超出 3 个**的点位不应被外盒二次替换（它们已被内盒"认领"，即便没超上限也不让给外盒）
            var innerBeyond = inner.Skip(3).Select(t => t.Loc).ToList();
            Check(innerBeyond.All(l => !got.Contains(l)),
                  $"内盒超限的 {innerBeyond.Count} 个点位没有被外盒二次替换（一个点只归一个区）");

            if (logLines.Count > 0)
                Console.WriteLine($"      [日志] {logLines.FirstOrDefault(l => l.Contains("region「inner-small」"))}");
            if (logLines.Count > 0)
                Console.WriteLine($"      [日志] {logLines.FirstOrDefault(l => l.Contains("region「outer-big」"))}");

            for (int i = restores.Count - 1; i >= 0; i--) restores[i]();
        }
        catch (Exception ex)
        {
            Check(false, "重叠区单测异常：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ── 工具 ──────────────────────────────────────────────────────────────────

    private static EventDef LoadEvent(string path)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        var list = JsonSerializer.Deserialize<List<EventDef>>(File.ReadAllText(path), opts);
        return list?.FirstOrDefault(e => e.LooseLootInject != null && e.LooseLootInject.ContainsKey(MapKey));
    }

    private static HashSet<string> LoadItemTpls(string path)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var p in doc.RootElement.EnumerateObject()) set.Add(p.Name);
        return set;
    }

    private static (double X, double Y, double Z)? GetXyz(object sp)
    {
        var pos = (sp as Spawnpoint)?.Template?.Position;
        if (pos == null) return null;
        var v = pos.Value;
        return (v.X, v.Y, v.Z);
    }

    /// <summary>统计"单物品刷点里那条 tpl 属于注入池"的点位数。</summary>
    private static Dictionary<string, int> CountPoolSingle(List<Spawnpoint> list, IDictionary<string, int> pool)
    {
        var res = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var sp in list)
        {
            var items = sp.Template?.Items?.ToList();
            if (items == null || items.Count != 1) continue;
            var tpl = items[0].Template.ToString();
            if (!pool.ContainsKey(tpl)) continue;
            res[tpl] = res.TryGetValue(tpl, out var c) ? c + 1 : 1;
        }
        return res;
    }

    private static Dictionary<string, int> MergePool(LooseLootInjectMapOpt opt)
    {
        var set = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (opt?.Valuable?.Tpls != null) foreach (var kv in opt.Valuable.Tpls) set[kv.Key] = kv.Value;
        if (opt?.Decor?.Tpls != null) foreach (var kv in opt.Decor.Tpls) set[kv.Key] = kv.Value;
        return set;
    }

    /// <summary>去掉 Template.Id 尾部 " [...]"（与维度里 StripTrailingBracket 同规则）。</summary>
    private static string BaseId(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        var t = id.TrimEnd();
        if (t.EndsWith("]"))
        {
            int lb = t.LastIndexOf('[');
            if (lb > 0) return t.Substring(0, lb).TrimEnd();
        }
        return t;
    }
}

// ── 复刻 SPT 的 LazyLoad<T>（cacheValue=false）：每次 .Value 重新反序列化 + 依次套 transformer ──
internal sealed class FakeLazy<T>
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

internal sealed class FakeLocationTable
{
    private readonly Dictionary<string, object> _dict;
    public FakeLocationTable(Dictionary<string, object> d) { _dict = d; }
    public IDictionary GetDictionary() => _dict;      // R.LocationDict 找的就是这个公开方法
}

internal sealed class FakeLocation
{
    public FakeLazy<LooseLoot> LooseLoot { get; set; }
}
