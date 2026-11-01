using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;

namespace EventRotator;

/// <summary>
/// ★★★ **静态战利品容器注入维度**（`events.json` 字段 <see cref="EventDef.StaticLootAdd"/>，JSON 键 `staticLootAdd`）。
///
/// 需求（用户原话）：「活动开启时往静态战利品容器里塞指定物品」——翻抽屉 / 外套 / 电脑就能捡到；
/// 活动关掉、切换、所有活动关闭 ⇒ 完整还原。对应 PPES 的 `OnLoad: ctx =&gt; Effects.AddStaticLoot(ctx, tpl, 2)`。
///
/// ── 落点（**以真实 DLL 与实机数据为准，不是按记忆写的**）─────────────────────────────────
///   `SPTarkov.Server.Core.Models.Eft.Common.Location.StaticLoot`
///     : `LazyLoad&lt;Dictionary&lt;MongoId, StaticLootDetails&gt;&gt;`
///   `StaticLootDetails`（JSON 名逐字，见 `SPT_Data/database/locations/&lt;map&gt;/staticLoot.json`）：
///     · `ItemCountDistribution : IEnumerable&lt;ItemCountDistribution&gt;` —— `{ count, relativeProbability }`
///     · `ItemDistribution      : IEnumerable&lt;ItemDistribution&gt;`      —— `{ tpl, relativeProbability }`
///   `ItemDistribution.RelativeProbability` 的真实类型是 **`float?`**（`System.Nullable&lt;Single&gt;`），
///   `ItemCountDistribution.Count` 是 `int?`；`tpl` 是 **MongoId**（赋值必须走 `R.Coerce`，直接塞 string 会抛）。
///   容器键 = **容器 tpl**（如抽屉 `578f87b7245977356274f2cd` / 外套 `578f8778245977358849a9b5` /
///   电脑 `59139c2186f77411564f8e42`），键本身不含名字；名字只存在于
///   `TemplateTable.Items[容器tpl].Name`（JSON `_name`，实测 = `"Drawer"` / `"Jacket"` / `"PCblock"`）。
///
/// ── 消费方（IL 实证，`Mono.Cecil` 扫 `SPTarkov.Server.Core.dll`）─────────────────────────
///   `SPTarkov.Server.Core.Generators.Loot.LocationLootGenerator::GenerateStaticContainers`
///   每局都读 `location.StaticLoot.Value`（IL 偏移 0110/0115），存入局部 `staticLootDist`，再
///   `AddLootToContainer(...)` → `GetWeightedCountOfContainerItems(itemcountDistribution)`
///   + `GetPossibleLootItemsForContainer(itemDistribution)`（`ProbabilityObjectArray&lt;MongoId, float?&gt;`
///   → `DrawAndRemove(count)`）。**所以 `relativeProbability` 是权重、不是百分比**：
///   单容器命中概率 ≈ `1 - (1 - w/(Σw + w))^平均件数`。
///
/// ── 为什么必须用 `AddTransformer`，而不是直接改 `.Value` ───────────────────────────────
///   SPT 自己在 `PostDbLoadService.ReduceStaticItemWeight` 与
///   `SeasonalEventService.ApplyChristmasEvent` 里就是 `LazyLoad.AddTransformer(Func&lt;T,T&gt;)`；
///   而且 `GenerateStaticContainers` 每次进图都重新读 `.Value` ⇒ 只在 `Apply()` 那一刻改一份对象
///   会被下一次读取丢掉（`LazyLoad.Build()` 会重新反序列化）。本维度因此**两条路都走**：
///     ① `Apply()` 时对当前 `.Value` 对象注入一次（自证 + 若 SPT 缓存该对象则立刻生效）；
///     ② `AddTransformer` 每局在**新鲜对象**上再注入一次（幂等：已有该 tpl 就跳过）。
///   还原 = 把 ① 里那份对象的 `ItemDistribution` **换回原始对象引用**（逐字段复原，不是"删条目"），
///   同时把 transformer 变直通（摘掉 `_active`）。**全程不写盘** ⇒ `staticLoot.json` 逐字节不变。
///
/// ── 权重依据（本次实测，`staticLoot.json` bigmap，2026-10-29）─────────────────────────────
///   | 容器    | 条目数 | 权重和  | 中位  | P90   | 最大   | 平均件数 | w=60 时单容器命中率 |
///   |---------|-------|--------|-------|-------|--------|---------|--------------------|
///   | Drawer  | 118   | 257627 | 894   | 6483  | 10856  | 0.90    | ≈0.021%            |
///   | Jacket  | 176   | 200053 | 497   | 3548  | 13986  | 1.07    | ≈0.032%            |
///   | PCblock | 13    | 162577 | 12616 | 23442 | 23587  | 1.50    | ≈0.055%            |
///   ⇒ **`weight: 60` 远低于 P90（≈3500~23400），实际是"万分之一"级**；
///     PPES 原写法 `percent=2` 换算成权重 = `Σw × 2/98` ≈ 3317~5257（正好**高于** P90），
///     单容器命中率 ≈2%。要"翻几次就能捡到"，`weight` 应写 **4000~5000**；写成 60 时本维度
///     仍会照做，但会在日志里打出实测命中率（不偷偷放过）。
///
/// ── 幂等 / 还原 / 降级 ──────────────────────────────────────────────────────────────
///   · 幂等：同一（地图 + 活动）重复 `Apply` ⇒ 直接跳过；transformer 内逐 tpl 判重（已有则不再追加）。
///   · 还原：`AddRestore` 闭包逐图复原（把 `ItemDistribution` 换回快照的原始对象）+ 摘掉 `_active`。
///   · 降级：拿不到 LocationTable / 容器不匹配 / tpl 不在 items 表 ⇒ 只 warning，绝不抛。
///   · 不匹配任何容器：**只 warn 一次**（`_warnedNoMatch`）。
/// </summary>
internal static class StaticLootAddDimension
{
    /// <summary>拿不到 `LocationTable` 时的兜底（与 <see cref="EventDef.StaticLootAdd"/> 的默认一致）。</summary>
    public static object LocationTable;

    /// <summary>用来把容器 tpl 翻译成名字（`TemplateTable.Items[tpl].Name` = JSON `_name`）。</summary>
    public static object TemplateTable;

    public static Action<string> Log;
    public static Action<Action> AddRestore;

    /// <summary>`weight` 不写或 ≤0 时的默认相对权重。</summary>
    public const double DefaultWeight = 60;

    private sealed class Plan
    {
        public string EvId;
        public string MapKey;
        public double Weight;
        /// <summary>tpl → 追加条目数（= 每个容器里最多能抽到几件，因为 draw 是"抽走不放回"）。</summary>
        public List<(string Tpl, int Count)> Items = new();
        public List<string> ContainerTpls = new();
        public Dictionary<string, string> ContainerNames = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>容器 tpl → 我们替换之前的 `ItemDistribution` **原始对象引用**（还原用）。</summary>
        public Dictionary<string, object> OrigDist = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>① 注入时改的那份 `Dictionary&lt;MongoId, StaticLootDetails&gt;`（还原用）。</summary>
        public object LootDict;
        public int Runs;
        public bool Logged;
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<string, Plan> _active = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _hooked = new(StringComparer.OrdinalIgnoreCase);
    private static bool _warnedNoMatch;

    /// <summary>
    /// ★ 当前**已放行**的 tpl（= 生效活动里 `staticLootAdd.items` 的键）。
    /// `ItemFilterService.IsLootableItemBlacklisted` 的 Harmony postfix 只对这里面的 tpl 返回 false；
    /// 别的物品（哪怕也在黑名单里）**一律保持原样**。
    /// 维护点：<see cref="Apply"/> 成功注入后加、<see cref="ResetApplied"/> 清空。
    /// </summary>
    private static readonly HashSet<string> _allowTpls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Harmony 放行补丁的挂载状态：0=未尝试 1=已挂 2=挂失败（只试一次）。</summary>
    private static int _patchState;

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][静态战利品] " + m); } catch { } }
    private static void Warn(string m) { try { Log?.Invoke("[EventRotator][静态战利品] ⚠ " + m); } catch { } }

    /// <summary>给 postfix 用：这个 tpl 是不是当前生效活动要求放行的。</summary>
    internal static bool IsAllowedTpl(string tpl)
    {
        if (string.IsNullOrEmpty(tpl)) return false;
        lock (_gate) return _allowTpls.Count > 0 && _allowTpls.Contains(tpl);
    }

    /// <summary>供 Plugin 在「还原 + 套用」开头调用：清掉当前生效计划（transformer 变直通）+ 停止黑名单放行。</summary>
    public static void ResetApplied()
    {
        try
        {
            lock (_gate)
            {
                _active.Clear();
                _warnedNoMatch = false;
                if (_allowTpls.Count > 0)
                {
                    _allowTpls.Clear();
                    Info("已停止放行（活动关闭/切换）");
                }
            }
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  入口
    // ══════════════════════════════════════════════════════════════════════════

    public static void Apply(EventDef ev)
    {
        var opt = ev?.StaticLootAdd;
        if (opt == null) return;

        if (LocationTable == null) { Warn("拿不到 LocationTable ⇒ 本维度跳过"); return; }

        var keywords = (opt.Containers ?? new List<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (keywords.Count == 0) { Warn("staticLootAdd.containers 为空 ⇒ 跳过（没有要匹配的容器）"); return; }

        var items = new List<(string Tpl, int Count)>();
        foreach (var kv in (opt.Items ?? new Dictionary<string, int>()))
        {
            var tpl = (kv.Key ?? "").Trim().ToLowerInvariant();
            if (tpl.Length != 24 || !tpl.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            { Warn($"items 里的 tpl「{kv.Key}」不是 24 位十六进制 ⇒ 丢弃"); continue; }
            if (kv.Value <= 0) { Warn($"items[{tpl}] 的数量 {kv.Value} ≤ 0 ⇒ 按 1 处理"); }
            items.Add((tpl, Math.Max(1, kv.Value)));
        }
        if (items.Count == 0) { Warn("staticLootAdd.items 为空/全部无效 ⇒ 跳过"); return; }

        // tpl 校验：拿不到 items 表就只 warning，不阻断（物品不存在时客户端会显示 unknown）
        var itemTable = TemplateTable != null ? R.Prop(TemplateTable, "Items") as IDictionary : null;
        if (itemTable != null)
            items = items.Where(it =>
            {
                if (R.DictHas(itemTable, it.Tpl)) return true;
                Warn($"items 里的 tpl {it.Tpl} 不在 TemplateTable.Items 里 ⇒ 丢弃该条");
                return false;
            }).ToList();
        if (items.Count == 0) { Warn("items 全部不在 TemplateTable.Items 里 ⇒ 跳过"); return; }

        double weight = opt.Weight ?? DefaultWeight;
        if (double.IsNaN(weight) || weight <= 0)
        {
            Warn($"weight={weight} 非法 ⇒ 按默认 {DefaultWeight} 处理");
            weight = DefaultWeight;
        }

        try
        {
            var dict = R.LocationDict(LocationTable);
            if (dict == null) { Warn("LocationTable 取不到地图字典 ⇒ 本维度跳过"); return; }

            int maps = 0, total = 0;
            foreach (DictionaryEntry kv in dict)
            {
                var mapKey = kv.Key?.ToString() ?? "";
                if (mapKey.Length == 0 || kv.Value == null) continue;
                try
                {
                    int n = ApplyMap(ev, mapKey, kv.Value, keywords, items, weight);
                    if (n > 0) { maps++; total += n; }
                }
                catch (Exception ex)
                {
                    Warn($"地图 '{mapKey}' 注入失败（已降级，其余地图照常）：{ex.GetType().Name}: {ex.Message}");
                }
            }

            if (total == 0)
            {
                if (!_warnedNoMatch)
                {
                    _warnedNoMatch = true;
                    var names = string.Join("/", keywords.Select(k => $"「{k}」"));
                    Warn($"没有任何静态战利品容器的名字/模板名匹配 {names} ⇒ 本维度不生效（只提示这一次）");
                }
                return;
            }
            Info($"✔ 活动 {ev.Id}：staticLootAdd 已生效 —— {maps} 张图 / {total} 个容器类；"
               + $"物品 {string.Join("、", items.Select(it => $"{ItemNames.Short(it.Tpl)}×{it.Count}"))} @权重 {weight:0.##}");

            // ★★★ 真问题（2026-10-29 线上）：光把条目写进 ItemDistribution 没用 ——
            //   `LocationLootGenerator.GetPossibleLootItemsForContainer` 在**构造候选池时**就用
            //   `ItemFilterService.IsLootableItemBlacklisted(tpl)` 把黑名单物品滤掉了，而
            //   `660bbc47…（加密U盘）` 正在 `configs/item.json → lootableItemBlacklist` 里。
            //   改 config / 改它的缓存字段都行不通（ctor 已把 config 拷进私有 HashSet 字段，
            //   且只提供 `AddItemToLootableBlacklistCache`，没有 Remove）⇒ 用 Harmony postfix 放行。
            EnsureBlacklistPatch();
            AllowTpls(items);
        }
        catch (Exception ex) { Warn("失败（已降级，不影响服务端）：" + ex.GetType().Name + ": " + ex.Message); }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ★ lootableItemBlacklist 放行（Harmony postfix，补丁常驻 + 条件判断）
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>把本活动要注入的 tpl 加入"允许清单"（幂等；只在有新 tpl 时打一行日志）。</summary>
    private static void AllowTpls(List<(string Tpl, int Count)> items)
    {
        List<string> added = null;
        lock (_gate)
        {
            foreach (var it in items)
            {
                if (_allowTpls.Add(it.Tpl)) (added ??= new List<string>()).Add(it.Tpl);
            }
        }
        if (added == null || added.Count == 0) return;
        Info($"已放行 lootableItemBlacklist 过滤：{added.Count} 个 tpl（"
           + string.Join("、", added.Select(t => $"{t[..Math.Min(8, t.Length)]}… {ItemNames.Short(t)}")) + "）");
    }

    /// <summary>
    /// 挂 <c>ItemFilterService.IsLootableItemBlacklisted(MongoId) : bool</c> 的 postfix（只挂一次）。
    /// 补丁**常驻**、靠 <see cref="IsAllowedTpl"/> 判空 —— 不做 patch/unpatch，避免反复挂载带来抖动。
    /// 类型必须从 <see cref="RuntimeTypes"/> 取运行时那份（4.1.6 prepatch 双载铁律）。
    /// </summary>
    private static void EnsureBlacklistPatch()
    {
        lock (_gate)
        {
            if (_patchState != 0) return;
            try
            {
                var t = RuntimeTypes.Find("SPTarkov.Server.Core.Services.Items.ItemFilterService");
                if (t == null) { _patchState = 2; Warn("找不到 ItemFilterService ⇒ 无法放行 lootableItemBlacklist，U 盘仍会被过滤掉"); return; }

                var m = t.GetMethod("IsLootableItemBlacklisted",
                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (m == null) { _patchState = 2; Warn("找不到 ItemFilterService.IsLootableItemBlacklisted ⇒ 无法放行"); return; }

                var harmony = new HarmonyLib.Harmony("com.kirisame.eventrotator.staticlootblacklist");
                var postfix = new HarmonyLib.HarmonyMethod(typeof(LootableItemBlacklistFilterPatch)
                    .GetMethod(nameof(LootableItemBlacklistFilterPatch.Postfix),
                               System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public));
                harmony.Patch(m, postfix: postfix);
                _patchState = 1;

                var asm = t.Assembly;
                var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(asm)?.Name ?? "(none)";
                Info($"已挂 IsLootableItemBlacklisted postfix：{t.FullName}.{m.Name}("
                   + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + $") : {m.ReturnType.Name}"
                   + $"｜assembly={asm.GetName().Name} v{asm.GetName().Version}｜ALC={alc}"
                   + $"｜与运行时 Core 同一份={ReferenceEquals(asm, RuntimeTypes.LiveCoreAssembly)}"
                   + $"｜GetPatchedMethods 含它={harmony.GetPatchedMethods().Contains(m)}");
            }
            catch (Exception ex)
            {
                _patchState = 2;
                Warn("挂 IsLootableItemBlacklisted postfix 失败（已降级：U 盘仍会被黑名单过滤）：" + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  单图
    // ══════════════════════════════════════════════════════════════════════════

    private static int ApplyMap(EventDef ev, string mapKey, object loc, List<string> keywords,
                                List<(string Tpl, int Count)> items, double weight)
    {
        var lazy = R.Prop(loc, "StaticLoot");
        if (lazy == null) return 0;                                  // 不是地图（或该图无静态战利品）：静默跳过

        Plan cur;
        lock (_gate)
        {
            if (_active.TryGetValue(mapKey, out cur) && string.Equals(cur.EvId, ev.Id, StringComparison.OrdinalIgnoreCase))
            {
                Info($"{mapKey}：活动 {ev.Id} 的静态战利品注入已登记过（幂等跳过，transformer 已在位）");
                return cur.ContainerTpls.Count;
            }
            if (cur != null)
            {
                Warn($"{mapKey}：已有活动 {cur.EvId} 的 staticLootAdd 生效 ⇒ 本次活动 {ev.Id} 跳过该图（先声明者优先）");
                return 0;
            }
        }

        // ── ① 先读一份「原始」数据：容器匹配 + 记下每个容器 `ItemDistribution` 的**原始对象引用** ──
        //   为什么先读：`LazyLoad.AddTransformer()` 内部会调 `Clear()`（IL 实证：AddTransformer → Clear）
        //   ⇒ 挂 hook 之后 `cacheValue=true` 的缓存也会作废、下一次 `.Value` 是新对象。
        //   所以快照必须在挂 hook **之前**取，否则快照拿到的是"已被注入过的"列表。
        var preValue = R.Prop(lazy, "Value");
        if (!(preValue is IDictionary preDict)) { Warn($"{mapKey}：StaticLoot 不是字典 ⇒ 跳过"); return 0; }

        var plan = new Plan { EvId = ev.Id, MapKey = mapKey, Weight = weight, Items = items };
        var matches = ResolveContainers(preDict, keywords, plan.ContainerNames);
        if (matches.Count == 0) return 0;

        plan.ContainerTpls = matches.Select(m => m.Tpl).ToList();
        foreach (var m in matches)
        {
            var details = LookupDict(preDict, m.Tpl);
            if (details == null) continue;
            plan.OrigDist[m.Tpl] = R.Prop(details, "ItemDistribution");
        }

        lock (_gate) _active[mapKey] = plan;

        // ── ② 挂 transformer（每局/每次 Build 在**新鲜对象**上注入；幂等）──
        bool hooked;
        lock (_gate) hooked = _hooked.Contains(mapKey);
        if (!hooked && !Hook(lazy, mapKey))
        {
            lock (_gate) { if (_active.TryGetValue(mapKey, out var s) && ReferenceEquals(s, plan)) _active.Remove(mapKey); }
            Warn($"{mapKey}：挂 LazyLoad.AddTransformer 失败 ⇒ 该图不生效（原始数据一行没动）");
            return 0;
        }

        // ── ③ 挂完 hook 再读一次：这就是"活对象"（cacheValue=true 时会被缓存住） ──
        //       ⚠ 两种情况必须都覆盖：
        //         · 刚挂上 hook（AddTransformer → Clear）⇒ 这次读是新构建，transformer 已经注入过；
        //         · hook **早就在位**（换活动时 `_hooked` 命中、不会再 Clear）⇒ 读到的是**缓存里的旧对象**，
        //           transformer 这轮不会被调用 ⇒ 必须在这里**直接对活对象注入**，否则 B 活动一条都进不去。
        //       `Inject` 内部按 tpl 判重 ⇒ 两种情况都幂等，不会翻倍。
        var live = R.Prop(lazy, "Value");
        plan.LootDict = live;
        if (live is IDictionary liveDict) Inject(liveDict, plan, snapshot: true);
        int verified = VerifyInjected(live, plan);
        if (verified == 0)
            Warn($"{mapKey}：transformer 已挂，但自证读取里没找到注入条目（首次进图会再试）");

        var cap = plan;
        AddRestore?.Invoke(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (_active.TryGetValue(mapKey, out var s) && ReferenceEquals(s, cap)) _active.Remove(mapKey);
                }
                int restored = RestoreSnapshot(cap);
                Info($"✔ {mapKey} 已还原：{restored} 个容器的 itemDistribution 换回原始列表（transformer 转直通；"
                   + "staticLoot.json 全程只读、从未写盘）");
            }
            catch (Exception ex) { Warn($"{mapKey} 还原异常：{ex.GetType().Name}: {ex.Message}"); }
        });

        LogPlan(plan, matches);
        return matches.Count;
    }

    /// <summary>自证：活对象里每个命中容器都应有我们的条目（返回命中的容器数）。</summary>
    private static int VerifyInjected(object live, Plan plan)
    {
        int hit = 0;
        try
        {
            if (!(live is IDictionary d)) return 0;
            foreach (var ctpl in plan.ContainerTpls)
            {
                var details = LookupDict(d, ctpl);
                if (details == null) continue;
                var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (R.Prop(details, "ItemDistribution") is IEnumerable en)
                    foreach (var o in en)
                    {
                        var t = R.Prop(o, "Tpl")?.ToString();
                        if (!string.IsNullOrEmpty(t)) have.Add(t);
                    }
                if (plan.Items.Any(it => have.Contains(it.Tpl))) hit++;
            }
        }
        catch { }
        return hit;
    }

    /// <summary>把 `ItemDistribution` 换回快照里的原始对象引用（逐字段复原，不是只删条目）。</summary>
    private static int RestoreSnapshot(Plan plan)
    {
        int n = 0;
        try
        {
            if (!(plan.LootDict is IDictionary d)) return 0;
            foreach (var kv in plan.OrigDist)
            {
                var details = LookupDict(d, kv.Key);
                if (details == null) continue;
                if (R.SetProp(details, "ItemDistribution", kv.Value)) n++;
            }
        }
        catch (Exception ex) { Warn($"{plan.MapKey} 复原异常（已尽量复原）：{ex.Message}"); }
        return n;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  容器匹配（具名优先，其次子串）
    // ══════════════════════════════════════════════════════════════════════════

    private sealed class Match
    {
        public string Tpl;
        public string Name;
        public string Keyword;
        public string How;      // 具名 / 子串
    }

    private static List<Match> ResolveContainers(IDictionary containerDict, List<string> keywords,
                                                 Dictionary<string, string> nameOut)
    {
        var all = new List<(string Tpl, string Name)>();
        foreach (DictionaryEntry e in containerDict)
        {
            var tpl = e.Key?.ToString() ?? "";
            if (tpl.Length == 0) continue;
            var name = TplName(tpl);
            all.Add((tpl, name ?? ""));
            nameOut[tpl] = name ?? "";
        }

        var result = new List<Match>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kw in keywords)
        {
            // ★ 2026-10-29 用户要求："闪存盘注入所有容器" ⇒ 支持通配符 `*`
            //   （该图 staticLoot 里的**每一个**容器都命中；`How="通配"` 便于日志区分，
            //     见 LogPlan 的「命中 N 个容器（通配 *）」）
            if (kw == "*")
            {
                foreach (var a in all)
                    if (taken.Add(a.Tpl)) result.Add(new Match { Tpl = a.Tpl, Name = a.Name, Keyword = kw, How = "通配" });
                continue;
            }

            // ① 具名：容器 `_name` 或 tpl 与关键字**逐字相等**（忽略大小写）
            var exact = all.Where(a =>
                string.Equals(a.Name, kw, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Tpl, kw, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0)
            {
                foreach (var a in exact)
                    if (taken.Add(a.Tpl)) result.Add(new Match { Tpl = a.Tpl, Name = a.Name, Keyword = kw, How = "具名" });
                continue;
            }
            // ② 子串：名字/模板名里含关键字（如 "pc" → "PCblock"）
            foreach (var a in all)
            {
                if (!(a.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                      a.Tpl.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (taken.Add(a.Tpl)) result.Add(new Match { Tpl = a.Tpl, Name = a.Name, Keyword = kw, How = "子串" });
            }
        }
        return result;
    }

    private static string TplName(string tpl)
    {
        try
        {
            var table = TemplateTable != null ? R.Prop(TemplateTable, "Items") : null;
            if (table == null) return null;
            var item = R.DictGet(table as IDictionary, tpl);
            return item == null ? null : R.Prop(item, "Name") as string;
        }
        catch { return null; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  注入（作用在一份 `Dictionary<MongoId, StaticLootDetails>` 上）
    // ══════════════════════════════════════════════════════════════════════════

    private static int Inject(IDictionary dict, Plan plan, bool snapshot)
    {
        int touched = 0;
        var wanted = new HashSet<string>(plan.ContainerTpls, StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in dict)
        {
            var ctpl = e.Key?.ToString() ?? "";
            if (!wanted.Contains(ctpl)) continue;
            var details = e.Value;
            if (details == null) continue;

            var distObj = R.Prop(details, "ItemDistribution");
            var elems = new List<object>();
            if (distObj is IEnumerable en) foreach (var o in en) if (o != null) elems.Add(o);
            if (snapshot && !plan.OrigDist.ContainsKey(ctpl)) plan.OrigDist[ctpl] = distObj;

            var existingTpls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in elems)
            {
                var t = R.Prop(o, "Tpl")?.ToString();
                if (!string.IsNullOrEmpty(t)) existingTpls.Add(t);
            }

            // 幂等：该 tpl 已在这个容器里（原生就有 / 上一轮已注入）⇒ 不再追加
            var todo = plan.Items.Where(it => !existingTpls.Contains(it.Tpl)).ToList();
            if (todo.Count == 0) continue;

            // ⚠ 元素类型必须从**原始集合**（List&lt;ItemDistribution&gt;）取；
            //   从我们自己物化出来的 `List&lt;object&gt;` 取会得到 `object`（Activator 造出来的是 object，
            //   反射设不进去 ⇒ 静默 0 条目。这是本次自检①在第一次跑的时候抓到的真 bug）。
            var elemType = R.ElemType(distObj as IList) ?? PropElemTypeOf(details) ?? elems.FirstOrDefault()?.GetType();
            if (elemType == null || elemType == typeof(object)) elemType = PropElemTypeOf(details);
            if (elemType == null) { Warn($"{plan.MapKey}: 拿不到 ItemDistribution 的元素类型 ⇒ 跳过容器 {ctpl}"); continue; }

            var list = R.NewListOf(elemType) as IList;
            if (list == null) { Warn($"{plan.MapKey}: 造 ItemDistribution 列表失败 ⇒ 跳过容器 {ctpl}"); continue; }
            foreach (var o in elems) list.Add(o);

            int addedHere = 0;
            foreach (var it in todo)
            {
                for (int k = 0; k < it.Count; k++)
                {
                    if (!AddOne(list, elemType, it.Tpl, plan.Weight)) break;
                    addedHere++;
                }
            }
            if (addedHere == 0) continue;
            if (!R.SetProp(details, "ItemDistribution", list))
            {
                Warn($"{plan.MapKey}: 写回 {ctpl} 的 ItemDistribution 失败 ⇒ 该容器跳过");
                continue;
            }
            touched++;
        }
        return touched;
    }

    /// <summary>往列表里追加一条 `{ tpl, relativeProbability }`（类型按运行时元素类型构造）。</summary>
    private static bool AddOne(IList list, Type elemType, string tpl, double weight)
    {
        try
        {
            var item = Activator.CreateInstance(elemType);
            if (item == null) return false;
            if (!R.SetProp(item, "Tpl", tpl)) return false;
            R.SetProp(item, "RelativeProbability", (float)weight);
            list.Add(item);
            return true;
        }
        catch (Exception ex) { Warn($"构造 ItemDistribution({tpl}) 失败：{ex.GetType().Name}: {ex.Message}"); return false; }
    }

    private static Type PropElemTypeOf(object details)
    {
        try
        {
            var p = R.GP(details.GetType(), "ItemDistribution", System.Reflection.BindingFlags.Public |
                                                               System.Reflection.BindingFlags.Instance);
            var t = p?.PropertyType;
            if (t == null) return null;
            if (t.IsGenericType) return t.GetGenericArguments()[0];
            if (t.IsArray) return t.GetElementType();
            return null;
        }
        catch { return null; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  transformer（每局在新鲜对象上注入；幂等）
    // ══════════════════════════════════════════════════════════════════════════

    private static bool Hook(object lazy, string mapKey)
    {
        try
        {
            var t = lazy.GetType().IsGenericType ? lazy.GetType().GetGenericArguments().FirstOrDefault() : null;
            if (t == null) { Warn($"{mapKey}: 拿不到 LazyLoad 的泛型实参 ⇒ 不挂 transformer"); return false; }

            var funcType = typeof(Func<,>).MakeGenericType(t, t);
            var add = lazy.GetType().GetMethod("AddTransformer", new[] { funcType })
                      ?? lazy.GetType().GetMethods().FirstOrDefault(m => m.Name == "AddTransformer" && m.GetParameters().Length == 1);
            if (add == null) { Warn($"{mapKey}: 该 LazyLoad 上没有 AddTransformer（SPT 版本不符？）"); return false; }

            var mi = typeof(StaticLootAddDimension).GetMethod(nameof(TransformerEntry),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (mi == null) { Warn($"{mapKey}: 找不到 TransformerEntry"); return false; }

            var p = Expression.Parameter(t, "v");
            var body = Expression.Block(
                Expression.Call(mi, Expression.Constant(mapKey), Expression.Convert(p, typeof(object))),
                p);                                  // 官方 transformer 约定：改完返回同一个对象
            var del = Expression.Lambda(funcType, body, p).Compile();
            add.Invoke(lazy, new object[] { del });

            lock (_gate) _hooked.Add(mapKey);
            Info($"{mapKey}：已挂 LazyLoad.AddTransformer（StaticLoot 的 .Value 每次进图重建 ⇒ 预注入会被丢弃，"
               + "官方 PostDbLoadService/SeasonalEventService 也用这条道路）");
            return true;
        }
        catch (Exception ex) { Warn($"{mapKey}: 挂 transformer 失败：{ex.Message}"); return false; }
    }

    /// <summary>官方 transformer 回调：每局反序列化出一份**新**的静态战利品字典后由 SPT 调用。</summary>
    private static void TransformerEntry(string mapKey, object dict)
    {
        Plan st;
        lock (_gate) { if (!_active.TryGetValue(mapKey, out st)) return; }
        if (st == null || !(dict is IDictionary d)) return;
        try
        {
            int touched = Inject(d, st, snapshot: false);
            st.Runs++;
            if (!st.Logged && touched > 0)
            {
                st.Logged = true;
                Info($"{mapKey}：本局注入已到达 —— 命中容器 {touched} 个（第 {st.Runs} 次构建）");
            }
        }
        catch (Exception ex)
        {
            Warn($"{mapKey}: transformer 注入异常（本局退化为官方原样）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  日志：匹配结果 + 权重依据（实测命中率）
    // ══════════════════════════════════════════════════════════════════════════

    private static void LogPlan(Plan plan, List<Match> matches)
    {
        try
        {
            // ★ 通配符命中：单独一行，N = 该图 staticLoot 里的**实际容器总数**（= 全部命中）
            if (matches.Any(m => m.How == "通配"))
            {
                Info($"{plan.MapKey}：命中 {matches.Count} 个容器（通配 *）");
                var sample = matches.Take(6).Select(m => $"{m.Name}({m.Tpl.Substring(0, 8)}…)");
                Info($"{plan.MapKey}：通配样例 —— {string.Join("、", sample)}"
                   + (matches.Count > 6 ? $" 等 {matches.Count} 个" : ""));
            }
            else
            {
                var shown = matches.Take(12).Select(m =>
                    $"{m.Name}({m.Tpl.Substring(0, 8)}…·{m.How})");
                Info($"{plan.MapKey}：命中 {matches.Count} 个容器 —— {string.Join("、", shown)}"
                   + (matches.Count > 12 ? $" 等 {matches.Count} 个" : ""));
            }

            // 实测权重分布 + 单容器命中率（每张图只打一次，给"翻几次能捡到"一个可核对的数字）
            // ⚠ 统计必须用**注入前**的原始列表（plan.OrigDist），否则"原有 N 条"会把我们自己的条目算进去。
            foreach (var m in matches.Take(6))
            {
                if (!plan.OrigDist.TryGetValue(m.Tpl, out var orig)) continue;
                var w = new List<double>();
                if (orig is IEnumerable en)
                    foreach (var o in en)
                    {
                        var v = R.Prop(o, "RelativeProbability");
                        if (v != null) w.Add(Convert.ToDouble(v, CultureInfo.InvariantCulture));
                    }
                if (w.Count == 0) continue;
                var sum = w.Sum();
                var p90 = w.OrderBy(x => x).ElementAt((int)(w.Count * 0.9));
                var mean = AvgItemCount(plan.LootDict, m.Tpl);
                var chance = (1 - Math.Pow(1 - plan.Weight / (sum + plan.Weight), Math.Max(0.01, mean))) * 100;
                Info($"{plan.MapKey}/{m.Name}：原有 {w.Count} 条、权重和 {sum:0}、中位 {Median(w):0}、P90 {p90:0}、"
                   + $"平均 {mean:0.##} 件 ⇒ 本次 weight={plan.Weight:0.##} 的单容器命中率 ≈ {chance:0.###}%");
            }
        }
        catch (Exception ex) { Warn($"{plan.MapKey}: 统计日志异常（不影响注入）：{ex.Message}"); }
    }

    private static double Median(List<double> w)
    {
        var s = w.OrderBy(x => x).ToList();
        return s.Count == 0 ? 0 : s[s.Count / 2];
    }

    private static double AvgItemCount(object lootDict, string ctpl)
    {
        try
        {
            var details = lootDict is IDictionary d ? LookupDict(d, ctpl) : null;
            double num = 0, den = 0;
            if (details != null && R.Prop(details, "ItemCountDistribution") is IEnumerable en)
                foreach (var o in en)
                {
                    var c = R.Prop(o, "Count");
                    var p = R.Prop(o, "RelativeProbability");
                    if (c == null || p == null) continue;
                    num += Convert.ToDouble(c, CultureInfo.InvariantCulture) * Convert.ToDouble(p, CultureInfo.InvariantCulture);
                    den += Convert.ToDouble(p, CultureInfo.InvariantCulture);
                }
            return den > 0 ? num / den : 1;
        }
        catch { return 1; }
    }

    internal static object LookupDict(IDictionary d, string key)
    {
        try
        {
            if (d.Contains(key)) return d[key];
            foreach (DictionaryEntry e in d)
                if (string.Equals(e.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase)) return e.Value;
        }
        catch { }
        return null;
    }
}

/// <summary>
/// ★★★ `ItemFilterService.IsLootableItemBlacklisted(MongoId) : Boolean` 的 **postfix 放行补丁**。
///
/// ── 为什么必须是 postfix 而不是改配置（2026-10-29 线上事故，IL 实证）────────────────
///   拦截链路：`LocationLootGenerator.GetPossibleLootItemsForContainer`（构造静态容器候选池）
///     → `ItemFilterService.IsLootableItemBlacklisted(tpl)` → 命中 ⇒ 该条目**根本不进池**
///   而我们注入的 `660bbc47c38b837877075e47`（加密U盘）就在
///   `SPT_Data/configs/item.json → lootableItemBlacklist` 里 ⇒ 写多少权重都白写。
///   改配置/改缓存字段也不行：`ItemFilterService..ctor` 会把
///   `ItemConfig.Blacklist` / `ItemConfig.LootableItemBlacklist` **逐个拷进私有 HashSet 字段**
///   （`ItemBlacklistCache` / `LootableItemBlacklistCache`，IL 偏移 0007-0094），之后
///   `IsLootableItemBlacklisted` 只查**缓存字段**（IL：ldfld LootableItemBlacklistCache / Contains），
///   而且公开 API 只有 `AddItemToLootableBlacklistCache`（只能加、没有 Remove）。
///   ⇒ 唯一干净的办法：在**返回 true 的那一刻**把它改成 false，且**只对我们自己放行的 tpl**。
///
/// ── 边界（绝不能误放行别人的物品）────────────────────────────────────────────────
///   · 原值已经是 false ⇒ 原样返回；
///   · tpl 不在当前生效活动的 `staticLootAdd.items` 里（<see cref="StaticLootAddDimension.IsAllowedTpl"/>）
///     ⇒ 原样返回（哪怕它也在黑名单里）；
///   · 任何异常 ⇒ 原样返回（补丁永远不能把游戏弄坏）。
///   补丁**常驻**（只挂一次），开关靠 `_allowTpls` 判空 —— 不做 patch/unpatch。
/// </summary>
internal static class LootableItemBlacklistFilterPatch
{
    /// <summary>
    /// postfix：`ref bool __result` 改返回值；`object __0` 是第一个参数（运行时那份 `MongoId`，
    /// 不写强类型以免 prepatch 双载时与运行时 Type 不是同一个）。
    /// </summary>
    public static void Postfix(object __0, ref bool __result)
    {
        try
        {
            if (!__result) return;                       // 本来就不在黑名单：原样
            var tpl = __0?.ToString();
            if (string.IsNullOrEmpty(tpl)) return;
            if (StaticLootAddDimension.IsAllowedTpl(tpl)) __result = false;
        }
        catch { /* 补丁绝不抛 */ }
    }
}
