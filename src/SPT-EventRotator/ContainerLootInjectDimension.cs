using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;

namespace EventRotator;

/// <summary>
/// ★★★ **容器战利品注入维度**（`events.json` 字段 <see cref="EventDef.ContainerLootInject"/>）。
///
/// 需求（用户原话）：活动期间在**地图静态容器**里按**容器类型**塞「对应类别的高价值物品」，
/// 概率要低（"容器不能个个出好东西，钱来得太快"）；可还原、不写盘。
///
/// ── 落点（真实 DLL 反编译，不是猜的）───────────────────────────────────────────────
///   `Location.StaticContainers : LazyLoad&lt;StaticContainerDetails&gt;`
///     → `StaticContainerDetails.StaticContainers : IEnumerable&lt;StaticContainerData&gt;`
///       · `StaticContainerData.Probability : float?`
///       · `StaticContainerData.Template : SpawnpointTemplate`
///         — `Id`（容器实例 id，如 `container_woods_design_stuff_00031`）/ `IsContainer` /
///           `Root`（首个 Item 的 `_id`）/ `Items : IEnumerable&lt;SptLootItem&gt;`
///   实机 `SPT_Data/database/locations/woods/staticContainers.json` 与上述模型逐字段对应
///   （429 个容器 / 24 种容器 tpl，反查脚本见 `_probe_containerloot/`）。
///
/// ── ★★ 为什么也用 `AddTransformer` 而不是直接改 `.Value` ──────────────────────────────
///   `StaticContainerDetails` **没有** `[CacheLazyLoad]`（反编译确认；全库只有
///   `GlobalLocaleDictionary` 带这个特性）⇒ `Location.StaticContainers` 的 `cacheValue = false`
///   ⇒ 每次读 `.Value` 都重新反序列化 staticContainers.json。改完就走的写法下一局就丢。
///   本维度只挂 `AddTransformer(Func&lt;StaticContainerDetails, StaticContainerDetails&gt;)`，
///   每次 Build 在**新鲜对象**上注入；还原 = 摘掉 `_active[地图]`（transformer 直通）。
///   **全程不写盘** ⇒ 文件天然逐字节不变。
///
/// ── ★★★ 为什么"只往 Items 里追加"在实战里还不够（本维度踩到的真坑，必须有补丁层）──
///   `LocationLootGenerator.GenerateStaticContainers`（反编译）在生成每局容器时会：
///     `containerClone.Template.Root = new MongoId();`
///     `containerClone.Template.Items.FirstOrDefault().Id = 同一个新 MongoId;`
///   —— **只重写"容器根物品"的 _id**，而它随后用 `Union` 把 `Template.Items` 原样带进结果。
///   我们预注入的子物品 `parentId` 指向的是**旧的根 _id** ⇒ 客户端
///   `ItemFactory.FlatItemsToTree` 里 `NoParentWithIdError` ⇒ 物品被静默丢弃。
///
/// ── ★★★★ v2（实机事故后）：**预注入这条路根本不可靠，postfix 必须能自己补** ──────────
///   实机证据（`SPT_Runtime/user/logs/spt/spt20261024.log`）：
///     · 00:22:54 chance=0.25 ⇒ `A total of: 1822 static items spawned`
///     · 00:41:39 chance=1.0  ⇒ `A total of: 1822 static items spawned`（!!! 一模一样）
///     · 两局 `生成期修复 N 件` 一行都没打（`grep -c 生成期修复` = 0）
///   ⇒ 预注入的 205 件**根本没进 `__result`**（不是父节点写错，是东西不在），
///     而 v1 的 postfix 因为 `_injected` 里有登记、容器 Id 又全都对得上，
///     `fixedN=0 && notSpawned=0` ⇒ **完全静默**，实机查不出原因。
///   ⇒ v2 定案：**postfix 认领得到就修，认领不到就现场补**（`FixUpGeneratedLoot`），
///     并且**每条都打日志**（`生成钩子已到达` + `本局容器 N 个，含注入物件的容器 M 个，改 parentId K 件`）。
///     两路互斥：每个容器每局**只会多 1 件**。
///
/// ── slotId 不再硬编码 "main" ──────────────────────────────────────────────────────
///   客户端 `Assembly-CSharp.decompiled.cs:570058` 是 `parent.Containers.ToDictionary(c =&gt; c.ID)`
///   再 `value.TryGetValue(flatItem.slotId)` —— 查不到就 `NoContainerWithIdError` 丢件。
///   所以 slotId 必须逐字等于该容器 `_props.Grids[]._name`。本活动配的 12 个容器 tpl
///   实测全是 `main`（已用实机 items.json 逐个核对），但代码里**改成从 items.json 读真名**，
///   不再赌这个巧合。
///
/// ── 容器分类 / 尺寸过滤 ─────────────────────────────────────────────────────────────
///   · 分类 = 容器 tpl 白名单（**不按名字**）：`categories.&lt;类&gt;.containers` 里列 tpl。
///   · 注入物 = `categories.&lt;类&gt;.items`（tpl → 相对权重），只从**本类**的物品池里抽。
///   · 尺寸 = 容器 tpl 的 `_props.Grids[]._props.cellsH/cellsV`；
///     物品 tpl 的 `_props.Width/Height`；允许旋转 ⇒ 横竖任一方向塞得进即可。
///     塞不进就**跳过并计数**（日志"跳过 N 个尺寸不符"），绝不硬塞。
///   · 每个容器**最多 1 件**注入；概率 P（默认 0.25）未命中时**完全不动**。
///
/// ── ★★★ v3（本文件当前形态）实机反馈"数据层有、局内没看到"后的三件事 ─────────────────
///   ① **白名单扩到覆盖全图 ≥90% 容器**：原 6 类只覆盖 woods 205/429（48%）——
///      漏掉的正是玩家最常开的箱子（木制板条箱 57、抽屉 36、地面藏匿/埋藏桶 45、尸体 58、
///      手榴弹箱 11、木弹药箱 8）。现新增 `crate / drawer / groundcache / ammobox / grenadebox / corpse`
///      六类 ⇒ **420/429 = 97.9%**（`Food&Stuff 8` + 节日空投箱 1 仍未归类，按用户要求"先列出不强行归类"）。
///   ② **每局“注入明细”+ 前 5 个「容器 tpl → 注入 tpl」样例**（`FixUpGeneratedLoot` 末尾），
///      玩家可对着日志核对自己开的箱子；格式逐字：
///        `[容器注入] woods：注入明细 显卡×12 内存条×9 私酒×14 …（共 145 件）`
///        `[容器注入] woods：样例 工具箱(5909d50c…) → 显示卡；旅行包(578f87a3…) → 私酒`
///   ③ **字段/落地排查**（逐条有出处）：
///      · `_id` 24 hex 唯一（`NewHex24`）✔
///      · `upd` 只含 `StackObjectsCount`；**弹匣/钱类给真实堆叠数**（`categories.*.stacks`，
///        否则 40 发 M80A1 会变成"1 发"）；其余恒 1 ✔
///      · `slotId` = 该容器 `_props.Grids[]._name`（从 items.json 读真名，不写死）✔
///      · `location`：**v3 起 postfix 自己算空格并写显式 `{x,y,r}`**。
///        客户端 `ItemFactory.FlatItemsToTree`（`Assembly-CSharp.decompiled.cs:570091`）是
///        `item2.location != null ? CreateItemLocation(location) : grid.FindFreeSpace(item)`，
///        再 `grid.AddItemWithoutRestrictions(...)`；`Grid.AddInternal` 里
///        **`location == null` ⇒ 直接返回 `NoFreeSpaceError`（真丢件）**，而**给了坐标**
///        只会走 `ignoreRestrictions` 分支（哪怕撞位也照样放进去，只报一条 error）。
///        ⇒ 显式坐标严格优于 null；算不出空格就保持 null 并**计数打日志**。
///      · 新增 warning：登记表说注入过、但 postfix 在该容器 `Items` 里找不到我们的件 ⇒
///        明确打出来（"已现场补注入 N 件"），不再静默。
///
/// ── 降级 / 幂等 ────────────────────────────────────────────────────────────────────
///   · tpl 校验：容器/物品 tpl 不在 `TemplateTable.Items` 里、或容器没有 Grids ⇒ warning + 丢弃该条。
///   · 拿不到 LocationTable / TemplateTable / LazyLoad / AddTransformer ⇒ warning + 跳过，绝不抛。
///   · transformer 内部异常 ⇒ warning + 原样返回（本局退化为官方行为）。
///   · 幂等：`_active[地图]` 记当前活动；同一活动重复 Apply 跳过；transformer 只挂一次。
/// </summary>
internal static class ContainerLootInjectDimension
{
    public static object LocationTable;
    public static object TemplateTable;
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    /// <summary>★ 探针用：固定随机种子（null = 每局随机，实战默认）。</summary>
    public static int? SeedOverride;

    private const double DefaultChance = 0.25;

    /// <summary>配置里的分类键 → 日志中文名。未列出的键直接显示原键名。</summary>
    private static readonly (string Key, string Label)[] CategoryOrder =
    {
        ("jacket", "夹克"), ("medbox", "医疗箱"), ("toolbox", "工具箱"),
        ("safe", "保险箱"), ("weaponbox", "武器箱"), ("duffle", "旅行包"),
        // ★ v3 新增 6 类（woods 实机 staticContainers.json 逐个核对过 tpl/格子，见 events.json）
        ("crate", "木箱/板条箱"), ("drawer", "抽屉/柜子"), ("groundcache", "地面藏匿/埋藏桶"),
        ("ammobox", "弹药箱"), ("grenadebox", "手雷箱"), ("corpse", "尸体"),
    };

    private sealed class MapState
    {
        public string EvId;
        public string MapKey;
        public List<CategoryPlan> Plan;
        public double Chance;
        public int Runs;
        public bool Logged;
    }

    /// <summary>
    /// 容器的一个格子（`items.json → _props.Grids[]`）。
    ///
    /// ★★ `Name` 是**客户端匹配用的 key**，必须逐字照抄 `_props.Grids[]._name`：
    ///   客户端 `ItemFactory.FlatItemsToTree`（`Assembly-CSharp.decompiled.cs:570045/570058`）是
    ///   `parent.Containers.ToDictionary(c =&gt; c.ID)`，再 `value.TryGetValue(flatItem.slotId, ...)`，
    ///   查不到就抛 **`NoContainerWithIdError`** ⇒ 物品被丢弃。
    ///   本维度**不再硬编码 `"main"`**（虽然本活动配的 12 个容器 tpl 实测全是 `main`，
    ///   但保险箱/弹挂/背包类有 `hideout` 之类的格子名，硬编码迟早再踩一次）。
    /// </summary>
    private sealed class GridSpec
    {
        public string Name;
        public int H;
        public int V;
        public override string ToString() => $"{Name}({H}x{V})";
    }

    /// <summary>一次预注入的登记项：注入件的 `_id` + 写进哪个格子（postfix 修复时要照抄）。</summary>
    private sealed class InjRef
    {
        public string Id;
        public string Grid;
        public string Tpl;
    }

    private sealed class CategoryPlan
    {
        public string Key;
        public string Label;
        /// <summary>容器 tpl → 该容器的格子列表（带真实 `_name`）。</summary>
        public Dictionary<string, List<GridSpec>> Containers =
            new(StringComparer.OrdinalIgnoreCase);
        public List<PoolItem> Items = new();
    }

    private sealed class PoolItem
    {
        public string Tpl;
        public int Weight;
        public int Width;
        public int Height;
        /// <summary>★ v3：写进 `upd.StackObjectsCount` 的值（默认 1）。只有 `categories.*.stacks`
        /// 里显式配了的 tpl 才会 &gt;1（弹药/卢布这类可堆叠物，配 1 会变成"1 发子弹/1 卢布"）。</summary>
        public int Stack = 1;
    }

    private sealed class CatStats
    {
        public int Total, Hit, Injected, SizeSkip, Fail;
    }

    private sealed class Stats
    {
        public double Chance;
        public List<(CategoryPlan Plan, CatStats St)> Cats = new();
        /// <summary>本图**全部**静态容器数（不止白名单内的）。</summary>
        public int AllTotal;
        /// <summary>★ v3 覆盖率诊断：本图里**没有归入任何类别**的容器 tpl → 个数。</summary>
        public Dictionary<string, int> UnmatchedTpl = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<string, MapState> _active = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _hooked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 生成期修复登记表：mapKey → 容器实例 Id → 本次预注入的物品（_id + grid 名）。
    /// 由 transformer 在每次 Build 时重建；`GenerateStaticContainers` postfix 立即消费。
    /// ★ 它现在只用来「认领 + 修复」预注入件；**认不出就现场补注入**（见 FixUpGeneratedLoot）。
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, List<InjRef>>> _injected =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>★ 诊断用：哪些地图的 `GenerateStaticContainers` postfix 真的到达过（每图只打印一次）。</summary>
    private static readonly HashSet<string> _arrived = new(StringComparer.OrdinalIgnoreCase);

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][容器注入] " + m); } catch { } }
    private static void Warn(string m) { try { Log?.Invoke("[EventRotator][容器注入] ⚠ " + m); } catch { } }

    /// <summary>供 Plugin 在"还原+套用"开头调用：清掉当前生效配置与生成期登记表。</summary>
    public static void ResetApplied()
    {
        try
        {
            lock (_gate)
            {
                _active.Clear();
                _injected.Clear();
                _arrived.Clear();     // ★ 诊断：每次「还原+套用」后重新打印一次「生成钩子已到达」
            }
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  入口
    // ══════════════════════════════════════════════════════════════════════════

    public static void Apply(EventDef ev)
    {
        if (ev?.ContainerLootInject == null || ev.ContainerLootInject.Count == 0) return;
        if (LocationTable == null) { Warn("拿不到 LocationTable，本维度跳过"); return; }
        if (TemplateTable == null) { Warn("拿不到 TemplateTable（无法做 tpl/尺寸校验），本维度跳过"); return; }
        try
        {
            var dict = R.LocationDict(LocationTable);
            if (dict == null) { Warn("LocationTable 取不到地图字典，本维度跳过"); return; }

            foreach (var mapKv in ev.ContainerLootInject)
            {
                try { ApplyMap(ev, dict, mapKv.Key, mapKv.Value); }
                catch (Exception ex) { Warn($"地图 '{mapKv.Key}' 注入失败（已降级，其余地图照常）：{ex.GetType().Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Warn("失败（已降级，不影响服务端）：" + ex.Message); }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  单图：校验配置 + 登记 + 挂 transformer + 立即自证一次
    // ══════════════════════════════════════════════════════════════════════════

    private static void ApplyMap(EventDef ev, IDictionary locTable, string mapKey, ContainerLootInjectMapOpt opt)
    {
        if (opt == null || string.IsNullOrWhiteSpace(mapKey)) return;

        if (!AllowedByOnlyOnMap(ev.OnlyOnMap, mapKey))
        {
            Info($"地图 '{mapKey}' 不在 onlyOnMap='{ev.OnlyOnMap}' 允许范围内 ⇒ 跳过");
            return;
        }

        var loc = FindLocation(locTable, mapKey);
        if (loc == null) { Warn($"找不到地图 '{mapKey}'，跳过"); return; }

        // ★ 拿 LazyLoad<StaticContainerDetails> 本体（不读 .Value：cacheValue=false，读了也白读）
        var lazy = R.Prop(loc, "StaticContainers");
        if (lazy == null) { Warn($"地图 '{mapKey}' 没有 StaticContainers，跳过"); return; }

        var plan = BuildPlan(mapKey, opt);
        if (plan.Count == 0) { Warn($"地图 '{mapKey}'：没有一条「容器 tpl + 注入物 tpl」通过校验 ⇒ 本维度对该图不生效"); return; }

        double chance = opt.Chance ?? DefaultChance;
        if (double.IsNaN(chance) || chance < 0) chance = DefaultChance;
        if (chance > 1) chance = 1;

        var state = new MapState { EvId = ev.Id, MapKey = mapKey, Plan = plan, Chance = chance };

        bool needHook;
        lock (_gate)
        {
            if (_active.TryGetValue(mapKey, out var cur) && string.Equals(cur.EvId, ev.Id, StringComparison.OrdinalIgnoreCase))
            {
                Info($"地图 '{mapKey}' 的同一活动已登记过（幂等跳过；transformer 已在位）");
                return;
            }
            _active[mapKey] = state;
            _injected.Remove(mapKey);
            needHook = !_hooked.Contains(mapKey);
        }

        if (needHook && !Hook(lazy, mapKey))
        {
            lock (_gate) { if (_active.TryGetValue(mapKey, out var s) && ReferenceEquals(s, state)) _active.Remove(mapKey); }
            Warn($"地图 '{mapKey}' 挂 AddTransformer 失败 ⇒ 本维度对该图不生效");
            return;
        }

        // 自证一次：读 .Value 触发 Build → transformer → 打印首局统计
        try { R.Prop(lazy, "Value"); }
        catch (Exception ex) { Warn($"{mapKey} 自证读取 .Value 失败（transformer 已挂，首次进图会再试）：{ex.Message}"); }

        var capState = state;
        AddRestore?.Invoke(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (_active.TryGetValue(mapKey, out var s) && ReferenceEquals(s, capState)) _active.Remove(mapKey);
                    _injected.Remove(mapKey);
                }
                Info($"✔ {mapKey} 已还原：transformer 转为直通（该活动期间共注入过 {capState.Runs} 局；"
                   + "staticContainers.json 全程只读、从未写盘 ⇒ 下一次读取即原始数据）");
            }
            catch (Exception ex) { Warn($"{mapKey} 还原异常：{ex.Message}"); }
        });
    }

    /// <summary>把"直通 → 注入"的 `Func&lt;StaticContainerDetails, StaticContainerDetails&gt;` 挂到官方 `LazyLoad.AddTransformer`。</summary>
    private static bool Hook(object lazy, string mapKey)
    {
        try
        {
            var t = lazy.GetType().IsGenericType ? lazy.GetType().GetGenericArguments().FirstOrDefault() : null;
            if (t == null) t = PropType(lazy, "Value");
            if (t == null) { Warn("拿不到 LazyLoad 的泛型实参"); return false; }

            var funcType = typeof(Func<,>).MakeGenericType(t, t);
            var add = lazy.GetType().GetMethod("AddTransformer", new[] { funcType })
                      ?? lazy.GetType().GetMethods().FirstOrDefault(m => m.Name == "AddTransformer" && m.GetParameters().Length == 1);
            if (add == null) { Warn("该 LazyLoad 上没有 AddTransformer（SPT 版本不符？）"); return false; }

            var mi = typeof(ContainerLootInjectDimension).GetMethod(nameof(TransformerEntry),
                                                                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (mi == null) { Warn("找不到 TransformerEntry"); return false; }

            var p = Expression.Parameter(t, "v");
            var body = Expression.Block(
                Expression.Call(mi, Expression.Constant(mapKey), Expression.Convert(p, typeof(object))),
                p);                                  // 改完把同一个对象原样返回（官方 transformer 约定）
            var del = Expression.Lambda(funcType, body, p).Compile();
            add.Invoke(lazy, new object[] { del });

            lock (_gate) _hooked.Add(mapKey);
            Info($"{mapKey}：已挂上 LazyLoad.AddTransformer（StaticContainerDetails 无 [CacheLazyLoad] "
               + "⇒ cacheValue=false，直接改 .Value 会被下一次读取丢弃）");
            return true;
        }
        catch (Exception ex) { Warn("挂 transformer 失败：" + ex.Message); return false; }
    }

    /// <summary>官方 transformer 回调：每局反序列化出一份**新** StaticContainerDetails 后由 SPT 调用。</summary>
    private static void TransformerEntry(string mapKey, object details)
    {
        MapState st = null;
        lock (_gate) { if (!_active.TryGetValue(mapKey, out st)) return; }
        if (st == null || details == null) return;
        try
        {
            var stats = Inject(details, mapKey, st);
            st.Runs++;
            if (!st.Logged) { st.Logged = true; LogStats(mapKey, stats, st.Runs); }
        }
        catch (Exception ex)
        {
            Warn($"{mapKey} 注入异常（本局退化为官方原样）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  真正的注入（作用在"本次反序列化出来的那个" StaticContainerDetails 上）
    // ══════════════════════════════════════════════════════════════════════════

    private static Stats Inject(object details, string mapKey, MapState state)
    {
        var st = new Stats { Chance = state.Chance };
        var byContainerTpl = new Dictionary<string, (CategoryPlan Plan, CatStats St)>(StringComparer.OrdinalIgnoreCase);
        foreach (var cp in state.Plan)
        {
            var cs = new CatStats();
            st.Cats.Add((cp, cs));
            foreach (var ctpl in cp.Containers.Keys) byContainerTpl[ctpl] = (cp, cs);
        }

        var containers = R.Prop(details, "StaticContainers") as IEnumerable;
        if (containers == null) { Warn($"地图 '{mapKey}' 的 StaticContainerDetails 没有 StaticContainers，跳过"); return st; }

        var rng = new Random(SeedOverride ?? (Environment.TickCount ^ SeedFor(mapKey)));
        var registry = new Dictionary<string, List<InjRef>>(StringComparer.OrdinalIgnoreCase);
        int warnShown = 0;

        foreach (var holder in containers)
        {
            if (holder == null) continue;
            var tmpl = R.Prop(holder, "Template");
            if (tmpl == null) continue;

            var containerTpl = RootTemplateOf(tmpl);
            if (string.IsNullOrEmpty(containerTpl)) continue;
            st.AllTotal++;                                            // ★ v3 覆盖率分母
            if (!byContainerTpl.TryGetValue(containerTpl, out var hit))
            {
                st.UnmatchedTpl[containerTpl] =                               // ★ v3 覆盖率诊断：未归类 tpl
                    st.UnmatchedTpl.TryGetValue(containerTpl, out var un) ? un + 1 : 1;
                continue;
            }

            hit.St.Total++;
            if (st.Chance <= 0 || rng.NextDouble() >= st.Chance) continue;      // 不命中：完全不动
            hit.St.Hit++;

            var pick = PickWeighted(hit.Plan.Items, rng);
            if (pick == null) continue;

            var grid = TryPickGrid(hit.Plan, containerTpl, pick);
            if (grid == null)
            {
                hit.St.SizeSkip++;
                continue;
            }

            var itemId = TryAppend(tmpl, pick.Tpl, grid.Name, pick.Stack, out var why);
            if (string.IsNullOrEmpty(itemId))
            {
                hit.St.Fail++;
                if (warnShown++ < 3) Warn($"{mapKey}/{hit.Plan.Label} 注入失败（跳过该容器）：{why}");
                continue;
            }
            hit.St.Injected++;

            var cid = R.Prop(tmpl, "Id")?.ToString();
            if (!string.IsNullOrEmpty(cid))
            {
                if (!registry.TryGetValue(cid, out var refs)) registry[cid] = refs = new List<InjRef>();
                refs.Add(new InjRef { Id = itemId, Grid = grid.Name, Tpl = pick.Tpl });
            }
        }

        lock (_gate) _injected[mapKey] = registry;     // 供 GenerateStaticContainers postfix 修复父节点
        return st;
    }

    /// <summary>
    /// 往容器模板的 `Items` 末尾追加 1 件：新 `_id`、`parentId = Root`、
    /// `slotId = <该容器真实 grid 名>`、`location` 留空（由 postfix 按真实占用算显式坐标）、
    /// `upd.StackObjectsCount = <stack>`（默认 1；弹药/卢布等可堆叠物由 `categories.*.stacks` 指定）。
    /// </summary>
    private static string TryAppend(object tmpl, string tpl, string gridName, int stack, out string why)
    {
        why = null;
        try
        {
            var itemsObj = R.Prop(tmpl, "Items");
            if (!(itemsObj is IEnumerable src)) { why = "Template.Items 为空/不可枚举"; return null; }

            var list = itemsObj as IList;
            Type elem = R.ElemType(list) ?? GenericArg(PropType(tmpl, "Items"));
            if (elem == null) { why = "拿不到 Items 的元素类型"; return null; }

            var made = R.NewListOf(elem) as IList;
            if (made == null) { why = "造 Items 列表失败"; return null; }
            foreach (var o in src) made.Add(o);

            var rootId = RootIdOf(tmpl, made);
            if (string.IsNullOrEmpty(rootId)) { why = "找不到 Template.Root 对应的根物品"; return null; }

            var item = Activator.CreateInstance(elem);
            if (item == null) { why = "构造 " + elem.Name + " 失败"; return null; }

            var idHex = NewHex24();
            if (!R.SetProp(item, "Id", idHex)) { why = "设置 _id 失败"; return null; }
            if (!R.SetProp(item, "Template", tpl)) { why = "设置 _tpl 失败"; return null; }
            R.SetProp(item, "ParentId", rootId);
            R.SetProp(item, "SlotId", string.IsNullOrEmpty(gridName) ? "main" : gridName);

            var updType = PropType(item, "Upd");
            if (updType != null)
            {
                var upd = Activator.CreateInstance(updType);
                if (upd != null)
                {
                    // ★ 客户端按 `upd.StackObjectsCount` 建栈；缺了它可堆叠物会被当 0 显示/丢弃。
                    R.SetProp(upd, "StackObjectsCount", (double)(stack > 0 ? stack : 1));
                    R.SetProp(item, "Upd", upd);
                }
            }

            made.Add(item);
            if (!R.SetProp(tmpl, "Items", made)) { why = "写回 Template.Items 失败"; return null; }

            var newId = R.Prop(item, "Id")?.ToString();
            return string.IsNullOrEmpty(newId) ? idHex : newId;
        }
        catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; return null; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ★ 生成期修复 + 补齐（由 Patches/ContainerLootInjectPatch 的 postfix 调用）
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// `LocationLootGenerator.GenerateStaticContainers` 的 postfix 主体。
    ///
    /// ── 为什么这里必须「既修又补」（而不是只修）────────────────────────────────────
    ///   实机v1 事故：数据层日志报「woods 命中 205/205，注入 205 件」，postfix 也挂上了，
    ///   但 `GenerateStaticContainers` 之后的 `A total of: 1822 static items spawned` 与
    ///   chance=0.25 那局**一模一样**，且**一条「生成期修复 N 件」都没打印** ⇒
    ///   预注入件**没有出现在返回给客户端的 `__result` 里**（不是父节点写错，是东西压根不在）。
    ///   根因是 `Location.StaticContainers` 是 `LazyLoad<StaticContainerDetails>`：transformer 只挂在
    ///   「我们拿到的那一个 LazyLoad 实例」上，而每局生成走的是**另一次 `Build()`**（cacheValue=false），
    ///   两条链路能不能撞上同一份对象，不受我们控制。
    ///   ⇒ 结论（也是本文件的最终设计）：**不信预注入，认领不到就现场补**。
    ///      · 认领得到（`_injected` 里有该容器实例、且 `sp.Items` 里真有那件）⇒ 修 parentId/slotId/location；
    ///      · 认领不到（预注入件被覆盖/过滤/压根没注入）⇒ **当场按真实 grid 名追加一件**。
    ///     两条路每局每个容器**只会得到 1 件**，绝不会翻倍。
    ///
    /// ── 客户端为什么能收（逐条有反编译出处，`audit/client_full/Assembly-CSharp.decompiled.cs`）──
    ///   · `570053` `dictionary2.TryGetValue(parentId)` —— parentId 必须是**容器根物品**的 _id
    ///     ⇒ 我们用 `sp.Root`（SPT 已经把根 _id 换成新 MongoId，见 `LocationLootGenerator.cs:311-313`）
    ///   · `570058` `value.TryGetValue(slotId)` —— slotId 必须是**该容器 `_props.Grids[]._name`**
    ///     ⇒ 我们不再硬编码 `"main"`，改成从 items.json 读真名（见 <see cref="GridSpec"/>）
    ///   · `570091` `loc = item2.location != null ? CreateItemLocation(item2.location) : grid.FindFreeSpace(item)`
    ///     ⇒ 客户端**会**处理 null，但 `FindFreeSpace` 在「格子还是空的」那一刻返回 **(0,0)**；
    ///       而 SPT 给官方生成件算坐标用的是**空白装箱图**（`ItemHelper.GetContainerMapping` →
    ///       `GetBlankContainerMap`，完全不算已占格）⇒ 官方件也从 (0,0) 起摆 ⇒ **同格两件**
    ///       （实机 errors.log 的 `is taken by another item` 在 145 个容器上逐一出现）。
    ///     ⇒ 结论：**由我们给显式坐标**（`TryComputeSlot` 逐行扫描 + 允许旋转），
    ///       写回后用 `VerifyNoOverlap` 再算一遍校验；算不出 ⇒ 摘掉这件并计数。
    /// </summary>
    public static void FixUpGeneratedLoot(string mapKey, object spawns)
    {
        if (string.IsNullOrEmpty(mapKey) || spawns == null) return;

        MapState st;
        lock (_gate) { _active.TryGetValue(mapKey, out st); }
        bool firstArrival;
        lock (_gate) { firstArrival = _arrived.Add(mapKey); }

        // ★★ 诊断①：postfix 到底有没有跑（每图一次，与「本图有没有注入计划」无关）
        if (firstArrival)
        {
            int n = 0;
            try { foreach (var _ in (IEnumerable)spawns) n++; } catch { }
            Info($"生成钩子已到达：map={mapKey}，本局静态容器 {n} 个 ⇒ "
               + (st == null
                    ? "本图当前没有容器注入计划（活动没生效？）"
                    : $"注入计划 {st.Plan.Count} 类，chance={st.Chance:0.###}"));
        }

        if (st == null) return;             // 本图没计划：什么都不做（也要保持静默，避免刷屏）

        Dictionary<string, List<InjRef>> pre;
        lock (_gate) { _injected.TryGetValue(mapKey, out pre); }
        if (pre == null) pre = new Dictionary<string, List<InjRef>>(StringComparer.OrdinalIgnoreCase);

        var byContainerTpl = new Dictionary<string, CategoryPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var cp in st.Plan)
            foreach (var ctpl in cp.Containers.Keys) byContainerTpl[ctpl] = cp;

        var rng = new Random(SeedOverride ?? (Environment.TickCount ^ SeedFor(mapKey) ^ 0x5F3A));
        int total = 0, matched = 0, repairedItems = 0, prePresent = 0, topped = 0;
        int sizeSkip = 0, failed = 0, noRoot = 0, already = 0, noSpace = 0;
        int overlapBad = 0, overlapShown = 0;
        var gridUsed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // ★ v3：本局「注入了什么」的按 tpl 明细 + 容器 tpl→注入 tpl 样例（给玩家对着看）
        var byTpl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var samples = new List<(string Ctpl, string Itpl)>();
        var lostSamples = new List<string>();
        // ★ v3「格子分配」日志：容器类型+格子尺寸 → (件数, 首件坐标样例)，最多记 8 组
        var allocs = new Dictionary<string, (int Count, string Ctpl, int H, int V, string Itpl, int W, int IH, int X, int Y, int R)>(
            StringComparer.OrdinalIgnoreCase);
        var droppedSamples = new List<string>();

        try
        {
            foreach (var sp in (IEnumerable)spawns)
            {
                if (sp == null) continue;
                total++;

                var ctpl = RootTemplateOf(sp);
                if (string.IsNullOrEmpty(ctpl) || !byContainerTpl.TryGetValue(ctpl, out var cp)) continue;
                matched++;

                // 先把 Items 物化：它可能是 `AddLootToContainer` 里 `Union` 串出来的惰性链，
                // 边遍历边改会自伤；物化后写成具体 List 最稳（序列化时本来也要全量枚举）。
                var items = Materialize(sp);
                if (items == null) { failed++; continue; }

                var root = R.Prop(sp, "Root")?.ToString();
                if (string.IsNullOrEmpty(root))
                {
                    root = FirstItemId(sp, items);
                    if (string.IsNullOrEmpty(root)) { noRoot++; continue; }
                }

                // ── ① 认领预注入件 ────────────────────────────────────────────
                var cid = R.Prop(sp, "Id")?.ToString();
                List<InjRef> refs = null;
                if (!string.IsNullOrEmpty(cid)) pre.TryGetValue(cid, out refs);

                if (refs != null && refs.Count > 0)
                {
                    int placedHere = 0, droppedHere = 0;
                    var dropIds = new List<string>();
                    foreach (var it in items.ToArray())      // ★ 拷贝：下面可能要从 items 里摘件
                    {
                        if (it == null) continue;
                        var iid = R.Prop(it, "Id")?.ToString();
                        if (string.IsNullOrEmpty(iid)) continue;
                        var match = refs.FirstOrDefault(x => string.Equals(x.Id, iid, StringComparison.OrdinalIgnoreCase));
                        if (match == null) continue;
                        string grid = match.Grid;
                        if (string.IsNullOrEmpty(grid))
                        {
                            var g = PickGridForTpl(cp, ctpl, match.Tpl);
                            grid = g?.Name;
                        }
                        if (string.IsNullOrEmpty(grid)) grid = "main";
                        R.SetProp(it, "ParentId", root);
                        R.SetProp(it, "SlotId", grid);

                        // ★★★ v3：自己按**真实占用**算格子（不再交给客户端 FindFreeSpace）。
                        //   算不出（格子真满）⇒ **放弃注入并计数**：把注入件从 Items 里摘掉。
                        //   绝不留一个 `location=null` 的件 —— 那样客户端 `FindFreeSpace` 返回 null
                        //   ⇒ `AddItemWithoutRestrictions(item, null)` ⇒ `NoFreeSpaceError` ⇒
                        //   服务端日志说「注入了」、局内却一件没有（又是"数据层有、局内没看到"）。
                        var slot = PlaceItemInAnyGrid(it, match.Tpl, grid, cp, ctpl, items, root);
                        if (slot == null)
                        {
                            noSpace++;
                            droppedHere++;
                            dropIds.Add(iid);
                            continue;
                        }
                        R.SetProp(it, "SlotId", slot.Grid.Name);      // 可能落到另一个更空余的格子
                        gridUsed[slot.Grid.Name] =
                            gridUsed.TryGetValue(slot.Grid.Name, out var gn) ? gn + 1 : 1;
                        NoteAlloc(allocs, ctpl, slot, match.Tpl);
                        if (!VerifyNoOverlap(it, match.Tpl, slot.Grid, items, root))
                        {
                            overlapBad++;
                            if (overlapShown++ < 3)
                                Warn($"{mapKey}：注入件 {ItemNames.Short(match.Tpl)} 落格校验发现重叠（已摘掉）："
                                   + $"{ItemNames.Short(ctpl)} {slot.Grid.Name} ({slot.X},{slot.Y})");
                            droppedHere++;
                            dropIds.Add(iid);
                            continue;
                        }
                        Bump(byTpl, match.Tpl);
                        AddSample(samples, ctpl, match.Tpl);
                        placedHere++;
                    }
                    if (dropIds.Count > 0) RemoveItems(sp, items, dropIds);
                    if (placedHere > 0) { repairedItems += placedHere; prePresent++; }
                    if (placedHere + droppedHere > 0)
                    {
                        if (droppedHere > 0 && droppedSamples.Count < 4)
                            droppedSamples.Add($"{ItemNames.Short(ctpl)}×{droppedHere}");
                        continue;              // ★ 本容器本局已处理过 ⇒ 绝不再补（每容器最多 1 件）
                    }
                    // 预注入件登记过却**不在生成结果里**（= v1 事故现场）⇒ 落到下面补齐
                    already++;
                    if (lostSamples.Count < 3) lostSamples.Add($"{cid ?? "?"}({ItemNames.Short(ctpl)})");
                }

                // ── ② 补齐：现场按容器真实 grid 追加 1 件 ──────────────────────
                if (st.Chance <= 0 || rng.NextDouble() >= st.Chance) continue;

                var pick = PickWeighted(cp.Items, rng);
                if (pick == null) { failed++; continue; }

                var grids = cp.Containers.TryGetValue(ctpl, out var g0) ? g0 : null;
                var target = PickGrid(grids, pick.Width, pick.Height);
                if (target == null) { sizeSkip++; continue; }

                var added = AppendToTemplate(sp, items, pick.Tpl, root, target.Name, pick.Stack, out var addedItem, out var why);
                if (string.IsNullOrEmpty(added))
                {
                    failed++;
                    if (failed <= 3) Warn($"{mapKey}：补注入失败（跳过该容器）：{why}");
                    continue;
                }
                var slot2 = PlaceItemInAnyGrid(addedItem, pick.Tpl, target.Name, cp, ctpl, items, root);
                if (slot2 == null || !VerifyNoOverlap(addedItem, pick.Tpl, slot2.Grid, items, root))
                {
                    // ★ 满格（或落格校验失败）⇒ **撤销这次注入**，不留 location=null 的件。
                    noSpace++;
                    overlapBad += slot2 == null ? 0 : 1;
                    WriteItems(sp, items);           // sp.Items 回到「没有这件」的样子
                    if (droppedSamples.Count < 4) droppedSamples.Add($"{ItemNames.Short(ctpl)}×1");
                    continue;
                }
                R.SetProp(addedItem, "SlotId", slot2.Grid.Name);
                gridUsed[slot2.Grid.Name] = gridUsed.TryGetValue(slot2.Grid.Name, out var gn2) ? gn2 + 1 : 1;
                NoteAlloc(allocs, ctpl, slot2, pick.Tpl);
                topped++;
                Bump(byTpl, pick.Tpl);
                AddSample(samples, ctpl, pick.Tpl);
            }
        }
        catch (Exception ex)
        {
            Warn($"生成期修复/补齐异常（不影响官方战利品）：{ex.GetType().Name}: {ex.Message}");
            return;
        }

        // ★★ 诊断②：不管成功与否**每条都打**（v1 就是这里静默才查不出来）
        int injectedThisRaid = repairedItems + topped;
        Info($"{mapKey}：本局容器 {total} 个，含注入物件的容器 {prePresent} 个，改 parentId {repairedItems} 件；"
           + $"补齐补注入 {topped} 件 ⇒ 本局注入合计 {injectedThisRaid} 件"
           + $"（白名单命中 {matched} 个"
           + (sizeSkip > 0 ? $"，尺寸跳过 {sizeSkip}" : "")
           + (failed > 0 ? $"，失败 {failed}" : "")
           + (noRoot > 0 ? $"，无 Root {noRoot}" : "")
           + (noSpace > 0 ? $"，无空位跳过 {noSpace}" : "")
           + (overlapBad > 0 ? $"，落格重叠被拒 {overlapBad}" : "")
           + (gridUsed.Count > 0
                ? $"，grid={string.Join("/", gridUsed.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}×{kv.Value}"))}"
                : "")
           + "）");

        // ★★★ v3：**格子分配**明细（用户指定格式）：
        //   `[容器注入] 格子分配：Duffle 4x3 → 注入件(私酒 1x2) 放 (2,1)；跳过 N 个无空位`
        //   第一行带「跳过 N 个无空位」；之后每个「容器类型+格子尺寸」各一行（最多 6 行，防刷屏）。
        if (injectedThisRaid > 0 || noSpace > 0)
        {
            bool first = true;
            foreach (var kv in allocs.Values.OrderByDescending(v => v.Count).Take(6))
            {
                string tail = first ? $"；跳过 {noSpace} 个无空位" : "";
                Info($"格子分配：{ItemNames.Short(kv.Ctpl)} {kv.H}x{kv.V} → "
                   + $"注入件({ItemNames.Short(kv.Itpl)} {kv.W}x{kv.IH}) 放 ({kv.X},{kv.Y})"
                   + (kv.Count > 1 ? $" ×{kv.Count}" : "")
                   + tail);
                first = false;
            }
            if (first)      // 一件都没放成（全被满格跳过）
                Info($"格子分配：本局没有可放置的注入件；跳过 {noSpace} 个无空位");
            else if (droppedSamples.Count > 0)
                Info($"格子分配：另有满格跳过（{string.Join("、", droppedSamples)}），共 {noSpace} 个无空位");
        }

        // ★★★ v3：**每局**打一行「注入明细」+ 前 5 个「容器 tpl → 注入 tpl」样例。
        //   实机反馈「数据层有、局内没看到」时，这一行 + 样例就是唯一的对照物：
        //   玩家开的箱子类型若压根不在明细里 ⇒ 说明那类容器不在白名单，不是丢件。
        if (injectedThisRaid > 0)
        {
            var top = byTpl.OrderByDescending(kv => kv.Value).ToList();
            const int maxShow = 14;
            var detail = string.Join(" ", top.Take(maxShow).Select(kv => $"{ItemNames.Short(kv.Key)}×{kv.Value}"));
            if (top.Count > maxShow) detail += $" …（还有 {top.Count - maxShow} 种）";
            Info($"{mapKey}：注入明细 {detail}（共 {injectedThisRaid} 件）");
        }
        if (samples.Count > 0)
        {
            Info($"{mapKey}：样例 " + string.Join("；", samples.Take(5).Select(s =>
                $"{ItemNames.Short(s.Ctpl)}({Tpl8(s.Ctpl)}) → {ItemNames.Short(s.Itpl)}")));
        }

        // ★★★ v3：登记表说有、生成结果里没有 ⇒ 明确 warning（v1 就是这一类静默丢件）
        if (already > 0)
            Warn($"{mapKey}：{already} 个容器「预注入件登记了，但生成结果的 Items 里找不到我们的件」"
               + $"⇒ 已改走「当场补注入」分支（实际补成 {topped} 件）；样例：{string.Join("、", lostSamples)}");
    }

    /// <summary>把 `SpawnpointTemplate.Items`（可能是惰性 Union 链）物化成 `List&lt;SptLootItem&gt;` 并写回。</summary>
    private static List<object> Materialize(object sp)
    {
        try
        {
            var src = R.Prop(sp, "Items") as IEnumerable;
            if (src == null) return null;
            var boxed = new List<object>();
            foreach (var o in src) boxed.Add(o);
            if (boxed.Count == 0) return null;
            var elem = boxed[0]?.GetType();
            if (elem == null) return null;
            var list = R.NewListOf(elem) as IList;
            if (list == null) return null;
            foreach (var o in boxed) list.Add(o);
            if (!R.SetProp(sp, "Items", list)) return null;
            return boxed;
        }
        catch { return null; }
    }

    private static string FirstItemId(object sp, List<object> items)
    {
        try { return items.Count > 0 ? R.Prop(items[0], "Id")?.ToString() : null; }
        catch { return null; }
    }

    private static GridSpec PickGridForTpl(CategoryPlan cp, string containerTpl, string itemTpl)
    {
        try
        {
            if (cp == null || string.IsNullOrEmpty(itemTpl)) return null;
            if (!cp.Containers.TryGetValue(containerTpl, out var grids)) return null;
            var pool = cp.Items.FirstOrDefault(x => string.Equals(x.Tpl, itemTpl, StringComparison.OrdinalIgnoreCase));
            if (pool == null) return PickGrid(grids, 1, 1);
            return PickGrid(grids, pool.Width, pool.Height);
        }
        catch { return null; }
    }

    /// <summary>
    /// 直接向**生成结果里的那个** `SpawnpointTemplate` 追加 1 件 `SptLootItem`：
    /// `parentId = 运行期 Root`、`slotId = 真实 grid 名`、`location` 先留空、由
    /// <see cref="PlaceItemInAnyGrid"/> 按真实占用算显式坐标（算不出则整件撤销，见调用方）、
    /// `upd.StackObjectsCount = stack`。
    /// </summary>
    private static string AppendToTemplate(object sp, List<object> items, string tpl, string root,
                                          string gridName, int stack, out object addedItem, out string why)
    {
        why = null;
        addedItem = null;
        try
        {
            var elem = items.Count > 0 ? items[0].GetType() : GenericArg(PropType(sp, "Items"));
            if (elem == null) { why = "拿不到 Items 的元素类型"; return null; }

            var item = Activator.CreateInstance(elem);
            if (item == null) { why = "构造 " + elem.Name + " 失败"; return null; }

            var idHex = NewHex24();
            if (!R.SetProp(item, "Id", idHex)) { why = "设置 _id 失败"; return null; }
            if (!R.SetProp(item, "Template", tpl)) { why = "设置 _tpl 失败"; return null; }
            if (!R.SetProp(item, "ParentId", root)) { why = "设置 parentId 失败"; return null; }
            if (!R.SetProp(item, "SlotId", string.IsNullOrEmpty(gridName) ? "main" : gridName))
            { why = "设置 slotId 失败"; return null; }
            R.SetProp(item, "Location", null);                     // 下一步由 PlaceItemInGrid 填显式坐标

            var updType = PropType(item, "Upd");
            if (updType != null)
            {
                var upd = Activator.CreateInstance(updType);
                if (upd != null)
                {
                    R.SetProp(upd, "StackObjectsCount", (double)(stack > 0 ? stack : 1));
                    R.SetProp(item, "Upd", upd);
                }
            }

            var list = R.NewListOf(elem) as IList;
            if (list == null) { why = "造 Items 列表失败"; return null; }
            foreach (var o in items) list.Add(o);
            list.Add(item);
            if (!R.SetProp(sp, "Items", list)) { why = "写回 Items 失败"; return null; }

            addedItem = item;
            var newId = R.Prop(item, "Id")?.ToString();
            return string.IsNullOrEmpty(newId) ? idHex : newId;
        }
        catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; return null; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ★ v3：显式 location（按容器真实占用算空格）
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 给注入件算一个**真空着**的格子并写进 `item.location`（`{x,y,r}`）。
    ///
    /// ── 为什么必须给显式坐标（不是"客户端会自己找"就完事）──────────────────────────
    ///   客户端 `ItemFactory.FlatItemsToTree`（`Assembly-CSharp.decompiled.cs:570091`）：
    ///     `LocationInGrid location = item2.location != null ? CreateItemLocation(item2.location)
    ///                                                       : grid.FindFreeSpace(value3);`
    ///     `error = grid.AddItemWithoutRestrictions(value3, location).Error;`
    ///   而 `Grid.AddInternal` 第一句就是 `if (location == null) return new NoFreeSpaceError(item, this);`
    ///   ⇒ **`location == null` + 格子已满 = 物品被静默丢弃**（只在客户端 errors.log 留一行
    ///     `Item deserialization error: No free space…`）。
    ///   相反，**给了坐标**时即使撞位也只走 `ignoreRestrictions` 分支（照样放进去，只多一行 error）。
    ///   ⇒ 显式坐标**严格优于** null；算不出（或确实没空格）才退回 null 并计数。
    ///
    /// 占用只统计「父节点 = 容器根、slotId = 本格子」的**直接子物品**（武器配件/弹匣内子弹等
    /// 孙节点不占容器格子），尺寸读 `items.json` 的 `_props.Width/Height`，`r != 0` 视为旋转 90°。
    /// </summary>
    /// <summary>一次落格的结果（哪个格子 + 坐标 + 是否旋转）。</summary>
    private sealed class Alloc
    {
        public GridSpec Grid;
        public int X, Y;
        /// <summary>0 = 不旋转（占 w×h）；1 = 旋转 90°（占 h×w）。</summary>
        public int R;
    }

    /// <summary>
    /// 在容器的**所有格子**里找一个能放下 `item` 的空位（优先调用方点名的那个格子，其次 `main`，
    /// 再其次其余格子），找到就写进 `item.location` 并返回 <see cref="Alloc"/>；一个都放不下 ⇒ null。
    ///
    /// ★ 为什么「换格子」也要试：夹克/背包类容器可能有多个 `_props.Grids[]`（`main` / `hideout` …）。
    ///   只试点名的那一个格子时，`main` 满了就直接放弃，而旁边的格子其实还有空 ⇒ 白扔一次注入。
    /// </summary>
    private static Alloc PlaceItemInAnyGrid(object item, string itemTpl, string preferGrid,
                                            CategoryPlan cp, string containerTpl,
                                            List<object> siblings, string root)
    {
        try
        {
            if (item == null || cp == null) return null;
            if (!cp.Containers.TryGetValue(containerTpl, out var grids) || grids == null || grids.Count == 0) return null;

            var ordered = grids.Where(g => g != null && g.H > 0 && g.V > 0)
                               .OrderBy(g => string.Equals(g.Name, preferGrid, StringComparison.OrdinalIgnoreCase) ? 0
                                           : string.Equals(g.Name, "main", StringComparison.OrdinalIgnoreCase) ? 1
                                           : 2)
                               .ToList();
            foreach (var g in ordered)
            {
                if (!TryComputeSlot(item, itemTpl, g, siblings, root, out var x, out var y, out var rot)) continue;
                if (!WriteLocation(item, siblings, x, y, rot != 0)) return null;
                return new Alloc { Grid = g, X = x, Y = y, R = rot };
            }
            return null;          // 确实没空格（或尺寸问题）⇒ 调用方「放弃注入并计数」
        }
        catch { return null; }
    }

    /// <summary>
    /// 纯计算：在 `grid` 上找第一个能放下 `itemTpl` 的空位（先不旋转，再旋转）。
    /// 占用只统计「父节点 = 容器根、slotId = 本格子」的**直接子物品**（武器配件/弹匣内子弹等孙节点不占格子），
    /// 尺寸读 `items.json` 的 `_props.Width/Height`，`r != 0` 视为旋转 90°（占 h×w）。
    /// </summary>
    private static bool TryComputeSlot(object item, string itemTpl, GridSpec grid,
                                       List<object> siblings, string root,
                                       out int outX, out int outY, out int outR)
    {
        outX = outY = outR = 0;
        if (grid == null || grid.H <= 0 || grid.V <= 0) return false;

        var (okSelf, w, h, _) = ItemSize(itemTpl);
        if (!okSelf || w <= 0 || h <= 0) return false;

        var occ = new bool[grid.H * grid.V];

        // ① 标记已被原生战利品占掉的格子
        foreach (var sib in siblings)
        {
            if (sib == null || ReferenceEquals(sib, item)) continue;
            var pid = R.Prop(sib, "ParentId")?.ToString();
            if (!string.Equals(pid, root, StringComparison.OrdinalIgnoreCase)) continue;      // 只算容器直接子物品
            var sid = R.Prop(sib, "SlotId")?.ToString();
            if (!string.Equals(sid, grid.Name, StringComparison.OrdinalIgnoreCase)) continue;  // 只算本格子
            var (okLoc, x, y, r) = ReadLoc(R.Prop(sib, "Location"));
            if (!okLoc || x < 0 || y < 0) continue;
            var (okSib, sw, sh, _) = ItemSize(R.Prop(sib, "Template")?.ToString());
            if (!okSib) continue;
            if (r != 0) { var tmp = sw; sw = sh; sh = tmp; }
            MarkCells(occ, grid, x, y, sw, sh);
        }

        // ② 逐行扫描找第一个塞得下的位置（横放优先，再试竖放）
        for (int y = 0; y + h <= grid.V; y++)
            for (int x = 0; x + w <= grid.H; x++)
                if (CellsFree(occ, grid, x, y, w, h)) { outX = x; outY = y; outR = 0; return true; }
        if (w != h)
        {
            for (int y = 0; y + w <= grid.V; y++)
                for (int x = 0; x + h <= grid.H; x++)
                    if (CellsFree(occ, grid, x, y, h, w)) { outX = x; outY = y; outR = 1; return true; }
        }
        return false;
    }

    /// <summary>
    /// ★★ 需求 ②「注入后该容器内没有任何两件物品重叠」的**独立校验**：
    ///   **重新读一遍** `item.location` 再说（不复用分配时的结论），把它和其它直接子物品一起
    ///   盖到图上；若它压到了别人（或越界）⇒ 返回 false，调用方必须把这件摘掉。
    ///   这是「自己算一遍写回前验证」的落地：分配算法写错/数据异常时，这里能兜住，不会把重叠写进局内。
    /// </summary>
    private static bool VerifyNoOverlap(object item, string itemTpl, GridSpec grid,
                                        List<object> siblings, string root)
    {
        try
        {
            if (item == null || grid == null || grid.H <= 0 || grid.V <= 0) return false;
            var occ = new bool[grid.H * grid.V];

            // 别人先占
            foreach (var sib in siblings)
            {
                if (sib == null || ReferenceEquals(sib, item)) continue;
                if (!string.Equals(R.Prop(sib, "ParentId")?.ToString(), root, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(R.Prop(sib, "SlotId")?.ToString(), grid.Name, StringComparison.OrdinalIgnoreCase)) continue;
                var (okLoc, x, y, r) = ReadLoc(R.Prop(sib, "Location"));
                if (!okLoc || x < 0 || y < 0) continue;
                var (okSib, sw, sh, _) = ItemSize(R.Prop(sib, "Template")?.ToString());
                if (!okSib) continue;
                if (r != 0) { var tmp = sw; sw = sh; sh = tmp; }
                MarkCells(occ, grid, x, y, sw, sh);
            }

            // 自己按**写进去的**坐标再盖一次；撞到别人 ⇒ 重叠
            var (okSelf, w, h, _) = ItemSize(itemTpl);
            if (!okSelf || w <= 0 || h <= 0) return false;
            var (okMe, mx, my, mr) = ReadLoc(R.Prop(item, "Location"));
            if (!okMe) return false;                       // 没写进去 ⇒ 视为失败（调用方摘件）
            if (mr != 0) { var tmp = w; w = h; h = tmp; }
            if (!CellsFree(occ, grid, mx, my, w, h)) return false;
            MarkCells(occ, grid, mx, my, w, h);
            return true;
        }
        catch { return false; }
    }

    /// <summary>本局「格子分配」日志的一行素材（按 容器tpl+格子尺寸 聚合）。</summary>
    private static void NoteAlloc(
        Dictionary<string, (int Count, string Ctpl, int H, int V, string Itpl, int W, int IH, int X, int Y, int R)> d,
        string ctpl, Alloc slot, string itpl)
    {
        if (d == null || slot == null || slot.Grid == null) return;
        var key = ctpl + "|" + slot.Grid.Name + "|" + slot.Grid.H + "x" + slot.Grid.V;
        var (ok, w, h, _) = ItemSize(itpl);
        if (d.TryGetValue(key, out var cur))
            d[key] = (cur.Count + 1, cur.Ctpl, cur.H, cur.V, cur.Itpl, cur.W, cur.IH, cur.X, cur.Y, cur.R);
        else if (d.Count < 8)
            d[key] = (1, ctpl, slot.Grid.H, slot.Grid.V, itpl, ok ? w : 0, ok ? h : 0, slot.X, slot.Y, slot.R);
    }

    /// <summary>把 `items`（`List&lt;object&gt;`）按元素类型重建强类型 List 写回 `sp.Items`。</summary>
    private static bool WriteItems(object sp, List<object> items)
    {
        try
        {
            if (sp == null || items == null || items.Count == 0) return false;
            var elem = items[0]?.GetType();
            if (elem == null) return false;
            var list = R.NewListOf(elem) as IList;
            if (list == null) return false;
            foreach (var o in items) list.Add(o);
            return R.SetProp(sp, "Items", list);
        }
        catch { return false; }
    }

    /// <summary>从 `items` 里摘掉指定的注入件（`id` 命中）并写回 —— 「放弃注入」的唯一落地方式。</summary>
    private static void RemoveItems(object sp, List<object> items, List<string> ids)
    {
        if (items == null || ids == null || ids.Count == 0) return;
        items.RemoveAll(o => o == null || ids.Any(id =>
            string.Equals(R.Prop(o, "Id")?.ToString(), id, StringComparison.OrdinalIgnoreCase)));
        WriteItems(sp, items);
    }

    private static void MarkCells(bool[] occ, GridSpec g, int x, int y, int w, int h)
    {
        for (int i = 0; i < h; i++)
        {
            int yy = y + i;
            if (yy < 0 || yy >= g.V) continue;
            for (int j = 0; j < w; j++)
            {
                int xx = x + j;
                if (xx < 0 || xx >= g.H) continue;
                occ[yy * g.H + xx] = true;
            }
        }
    }

    private static bool CellsFree(bool[] occ, GridSpec g, int x, int y, int w, int h)
    {
        if (x < 0 || y < 0 || x + w > g.H || y + h > g.V) return false;
        for (int i = 0; i < h; i++)
            for (int j = 0; j < w; j++)
                if (occ[(y + i) * g.H + x + j]) return false;
        return true;
    }

    /// <summary>
    /// 用**同类型**的 `ItemLocation` 造一个 `{x,y,r}`（类型从兄弟物品的 `location` 实例上取，
    /// 这样不硬依赖 `SPTarkov.Server.Core.Models.Eft.Common.Tables.ItemLocation`）。
    /// 用**同类型**的 `ItemLocation` 造一个 `{x,y,r}`：优先照抄兄弟物品上那个真实实例的类型
    /// （生产环境里 SPT 的 `AddLootToContainer` 就是 `new ItemLocation { X, Y, R }`）；
    /// 兄弟物品的 location 是 `JsonElement`（探针/`Item.Location` 声明成 `object` 时）或压根没有 ⇒
    /// 按全名反射取真 `ItemLocation` 类型，保证序列化出来就是 `{"x":…,"y":…,"r":…}`。
    /// </summary>
    private static bool WriteLocation(object item, List<object> siblings, int x, int y, bool rot)
    {
        var loc = NewLocation(siblings, x, y, rot);
        if (loc == null) { R.SetProp(item, "Location", null); return true; }   // 空容器：客户端 FindFreeSpace 必然成功
        if (!R.SetProp(item, "Location", loc)) { R.SetProp(item, "Location", null); return true; }
        return true;
    }

    /// <summary>
    /// 造一个"和 SPT 原生战利品同款"的 `ItemLocation` 对象。
    /// ★ 优先照抄兄弟物品上那个**真实实例**的类型（SPT 代码里是
    /// `source.First().Location = new ItemLocation { X, Y, R }`，见
    /// `LocationLootGenerator.cs:366-371`）；若兄弟物品的 location 只是
    /// 反序列化出来的 `JsonElement`（探针里常见），则按全名反射取真类型。
    /// </summary>
    private static object NewLocation(List<object> siblings, int x, int y, bool rot)
    {
        try
        {
            Type t = null;
            foreach (var s in siblings)
            {
                if (s == null) continue;
                var l = R.Prop(s, "Location");
                if (l == null) continue;
                if (l is System.Text.Json.JsonElement) continue;      // 壳子，不能拿来 new
                t = l.GetType();
                break;
            }
            // ★ 拿不到强类型原型（探针里 `Item.Location` 是 `object` ⇒ 反序列化出来是 `JsonElement`）
            //   ⇒ 按全名从**装 Items 的那个类型所在程序集**里取真 `ItemLocation`：
            //     · 同一份程序集 ⇒ 序列化时 STJ 一定认得它的属性名（`X/Y/R` → `x/y/r`）
            //     · 退一步再扫已加载程序集（SPT 4.1.6 有内存预补丁那份，同名 Type 属性完全一致，
            //       序列化只看属性名与值，所以哪一份都能用）
            if (t == null)
            {
                var itemAsm = siblings.FirstOrDefault(s => s != null)?.GetType().Assembly;
                t = itemAsm?.GetType("SPTarkov.Server.Core.Models.Eft.Common.Tables.ItemLocation");
            }
            if (t == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    t = asm.GetType("SPTarkov.Server.Core.Models.Eft.Common.Tables.ItemLocation");
                    if (t != null) break;
                }
            }
            if (t == null) return null;

            var loc = Activator.CreateInstance(t);
            if (loc == null) return null;
            if (!R.SetProp(loc, "X", x)) return null;
            if (!R.SetProp(loc, "Y", y)) return null;
            R.SetProp(loc, "R", rot ? 1 : 0);              // 0 = ItemRotation.Horizontal
            return loc;
        }
        catch { return null; }
    }

    /// <summary>
    /// 读一个 `location`（可能是 SPT 的 `ItemLocation` 强类型对象，也可能是
    /// `Item.Location` 声明成 `object` 后反序列化出来的 `JsonElement`）→ `(ok, x, y, r)`。
    /// </summary>
    private static (bool ok, int x, int y, int r) ReadLoc(object loc)
    {
        if (loc == null) return (false, 0, 0, 0);
        try
        {
            if (loc is System.Text.Json.JsonElement je)
            {
                if (je.ValueKind != System.Text.Json.JsonValueKind.Object) return (false, 0, 0, 0);
                int jx = 0, jy = 0, jr = 0;
                if (je.TryGetProperty("x", out var px) && px.ValueKind == System.Text.Json.JsonValueKind.Number) jx = px.GetInt32();
                if (je.TryGetProperty("y", out var py) && py.ValueKind == System.Text.Json.JsonValueKind.Number) jy = py.GetInt32();
                if (je.TryGetProperty("r", out var pr))
                    jr = pr.ValueKind == System.Text.Json.JsonValueKind.String
                        ? (string.Equals(pr.GetString(), "Vertical", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                        : (pr.ValueKind == System.Text.Json.JsonValueKind.Number ? pr.GetInt32() : 0);
                return (true, jx, jy, jr);
            }
            var xv = R.Prop(loc, "X") ?? R.Prop(loc, "x");
            var yv = R.Prop(loc, "Y") ?? R.Prop(loc, "y");
            if (xv == null || yv == null) return (false, 0, 0, 0);
            var rv = R.Prop(loc, "R") ?? R.Prop(loc, "r");
            int r = 0;
            if (rv != null)
            {
                var rs = rv.ToString();
                r = string.Equals(rs, "Vertical", StringComparison.OrdinalIgnoreCase) ? 1 : ToInt(rv, 0);
            }
            return (true, ToInt(xv, 0), ToInt(yv, 0), r);
        }
        catch { return (false, 0, 0, 0); }
    }

    private static void Bump(Dictionary<string, int> d, string key)
    {
        if (d == null || string.IsNullOrEmpty(key)) return;
        d[key] = d.TryGetValue(key, out var n) ? n + 1 : 1;
    }

    /// <summary>★ v3 样例：同一对「容器 tpl → 注入 tpl」只记一次（最多记 8 对，日志只打前 5 个）。</summary>
    private static void AddSample(List<(string Ctpl, string Itpl)> samples, string ctpl, string itpl)
    {
        if (samples == null || string.IsNullOrEmpty(ctpl) || string.IsNullOrEmpty(itpl)) return;
        if (samples.Count >= 8) return;
        if (samples.Any(s => string.Equals(s.Ctpl, ctpl, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(s.Itpl, itpl, StringComparison.OrdinalIgnoreCase))) return;
        samples.Add((ctpl, itpl));
    }

    /// <summary>日志里的短 tpl：`5909d50c…`（前 8 位 + 省略号）。</summary>
    private static string Tpl8(string tpl)
        => string.IsNullOrEmpty(tpl) ? "?" : (tpl.Length > 8 ? tpl.Substring(0, 8) + "…" : tpl);

    // ══════════════════════════════════════════════════════════════════════════
    //  配置校验 / 尺寸
    // ══════════════════════════════════════════════════════════════════════════

    private static List<CategoryPlan> BuildPlan(string mapKey, ContainerLootInjectMapOpt opt)
    {
        var plans = new List<CategoryPlan>();
        if (opt?.Categories == null || opt.Categories.Count == 0)
        {
            Warn($"{mapKey}：containerLootInject.categories 为空");
            return plans;
        }

        var order = OrderedCategoryKeys(opt.Categories.Keys);
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in order)
        {
            var cat = opt.Categories[key];
            if (cat == null) continue;
            var cp = new CategoryPlan { Key = key, Label = LabelOf(key) };

            foreach (var ctpl in cat.Containers ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(ctpl)) continue;
                if (!IsHex24(ctpl)) { Warn($"{mapKey}/{cp.Label}：容器 tpl '{ctpl}' 不是 24 位 hex ⇒ 丢弃"); continue; }
                if (claimed.TryGetValue(ctpl, out var other))
                {
                    Warn($"{mapKey}/{cp.Label}：容器 tpl {ctpl} 已被类别 '{other}' 占用 ⇒ 丢弃重复项（先声明者生效）");
                    continue;
                }
                var (ok, grids, tplName) = ContainerGrids(ctpl);
                if (!ok)
                {
                    Warn($"{mapKey}/{cp.Label}：容器 tpl {ctpl} {tplName} ⇒ 丢弃");
                    continue;
                }
                claimed[ctpl] = key;
                cp.Containers[ctpl] = grids;
            }

            foreach (var kv in cat.Items ?? new Dictionary<string, int>())
            {
                var tpl = kv.Key;
                if (string.IsNullOrWhiteSpace(tpl)) continue;
                if (!IsHex24(tpl)) { Warn($"{mapKey}/{cp.Label}：物品 tpl '{tpl}' 不是 24 位 hex ⇒ 丢弃"); continue; }
                var (ok, w, h, tplName) = ItemSize(tpl);
                if (!ok) { Warn($"{mapKey}/{cp.Label}：物品 tpl {tpl} {tplName} ⇒ 丢弃（请核对 items.json）"); continue; }

                // ★ v3：可堆叠物（弹药/卢布）的 `upd.StackObjectsCount` 从 `categories.*.stacks` 取；
                //   配了 1 的话 40 发 M80A1 会变成"1 发"、卢布变成"1 卢布"。
                int stack = 1;
                if (cat.Stacks != null)
                    foreach (var sk in cat.Stacks)
                        if (string.Equals(sk.Key, tpl, StringComparison.OrdinalIgnoreCase)) { stack = sk.Value; break; }
                if (stack < 1) stack = 1;
                if (stack > 1)
                {
                    int max = ItemStackMax(tpl);
                    if (max > 0 && stack > max)
                    {
                        Warn($"{mapKey}/{cp.Label}：{tpl} 的 stack={stack} 超过 StackMaxSize={max} ⇒ 按 {max} 处理");
                        stack = max;
                    }
                }

                cp.Items.Add(new PoolItem { Tpl = tpl, Weight = kv.Value > 0 ? kv.Value : 1, Width = w, Height = h, Stack = stack });
            }

            if (cp.Containers.Count == 0) { Warn($"{mapKey}/{cp.Label}：没有可用的容器 tpl ⇒ 整类跳过"); continue; }
            if (cp.Items.Count == 0) { Warn($"{mapKey}/{cp.Label}：没有可用的注入物 tpl ⇒ 整类跳过"); continue; }
            plans.Add(cp);
        }
        return plans;
    }

    /// <summary>
    /// 选一个「塞得进」的格子（允许旋转 90°）：
    ///   · 多个格子都塞得进时**优先名字叫 `main` 的**（绝大多数容器只有 main，且客户端 UI 默认展开它）；
    ///   · 否则取第一个塞得进的；
    ///   · 一个都塞不进 ⇒ 返回 null（调用方跳过并计数，绝不硬塞）。
    /// </summary>
    private static GridSpec PickGrid(List<GridSpec> grids, int w, int h, bool preferMain = true)
    {
        if (grids == null || grids.Count == 0) return null;
        GridSpec fallback = null;
        foreach (var g in grids)
        {
            if (g == null || g.H <= 0 || g.V <= 0) continue;
            bool fit = (w <= g.H && h <= g.V) || (w <= g.V && h <= g.H);
            if (!fit) continue;
            if (preferMain && string.Equals(g.Name, "main", StringComparison.OrdinalIgnoreCase)) return g;
            if (fallback == null) fallback = g;
        }
        return fallback;
    }

    private static GridSpec TryPickGrid(CategoryPlan cp, string containerTpl, PoolItem item)
    {
        if (!cp.Containers.TryGetValue(containerTpl, out var grids)) return null;
        return PickGrid(grids, item.Width, item.Height);
    }

    private static (bool ok, List<GridSpec> grids, string why) ContainerGrids(string tpl)
    {
        var tmpl = FindTemplate(tpl);
        if (tmpl == null) return (false, null, "不在 TemplateTable.Items 里");
        var props = R.Prop(tmpl, "Properties") ?? R.Prop(tmpl, "_props");
        var gridsObj = R.Prop(props, "Grids") as IEnumerable;
        if (gridsObj == null) return (false, null, "没有 _props.Grids（不是容器？）");
        var grids = new List<GridSpec>();
        foreach (var g in gridsObj)
        {
            var gp = R.Prop(g, "Properties") ?? R.Prop(g, "_props");
            int cellH = ToInt(R.Prop(gp, "CellsH") ?? R.Prop(gp, "cellsH"), 0);
            int cellV = ToInt(R.Prop(gp, "CellsV") ?? R.Prop(gp, "cellsV"), 0);
            if (cellH <= 0 || cellV <= 0) continue;
            // ★ grid 名：JSON 是 `_name`，CLR 是 `Name` —— 两路都试（本项目最致命的坑型）
            var name = (R.Prop(g, "Name") ?? R.Prop(g, "_name"))?.ToString();
            if (string.IsNullOrWhiteSpace(name)) name = "main";       // 兜底：官方数据里没出现过空名
            grids.Add(new GridSpec { Name = name, H = cellH, V = cellV });
        }
        if (grids.Count == 0) return (false, null, "Grids 里没有 cellsH/cellsV（不是容器？）");
        return (true, grids, null);
    }

    private static (bool ok, int w, int h, string why) ItemSize(string tpl)
    {
        var tmpl = FindTemplate(tpl);
        if (tmpl == null) return (false, 0, 0, "不在 TemplateTable.Items 里");
        var props = R.Prop(tmpl, "Properties") ?? R.Prop(tmpl, "_props");
        int w = ToInt(R.Prop(props, "Width") ?? R.Prop(props, "width"), 0);
        int h = ToInt(R.Prop(props, "Height") ?? R.Prop(props, "height"), 0);
        if (w <= 0 || h <= 0) return (false, 0, 0, "没有 _props.Width/Height");
        return (true, w, h, null);
    }

    /// <summary>★ v3：`_props.StackMaxSize`（取不到返回 0 = 未知，调用方不据此拦截）。</summary>
    private static int ItemStackMax(string tpl)
    {
        try
        {
            var tmpl = FindTemplate(tpl);
            if (tmpl == null) return 0;
            var props = R.Prop(tmpl, "Properties") ?? R.Prop(tmpl, "_props");
            return ToInt(R.Prop(props, "StackMaxSize") ?? R.Prop(props, "stackMaxSize"), 0);
        }
        catch { return 0; }
    }

    private static object FindTemplate(string tpl)
    {
        try
        {
            var dict = R.Prop(TemplateTable, "Items") as IDictionary;
            if (dict == null || string.IsNullOrEmpty(tpl)) return null;
            var key = R.Coerce(tpl, R.DictKeyType(dict));
            if (key == null) return null;
            return dict.Contains(key) ? dict[key] : null;
        }
        catch { return null; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  日志
    // ══════════════════════════════════════════════════════════════════════════

    private static void LogStats(string mapKey, Stats st, int run)
    {
        try
        {
            int tAll = 0, hAll = 0, iAll = 0, sAll = 0, fAll = 0;
            foreach (var (cp, cs) in st.Cats)
            {
                tAll += cs.Total; hAll += cs.Hit; iAll += cs.Injected; sAll += cs.SizeSkip; fAll += cs.Fail;
                if (cs.Total == 0) continue;
                string extra = cs.SizeSkip > 0 ? $"（跳过 {cs.SizeSkip} 个尺寸不符）" : "";
                if (cs.Fail > 0) extra += $"（{cs.Fail} 件写入失败）";
                // ★ 用户指定格式：[容器注入] woods 医疗箱：命中 12/48 个容器，注入 12 件（跳过 3 个尺寸不符）
                Info($"{mapKey} {cp.Label}：命中 {cs.Hit}/{cs.Total} 个容器，注入 {cs.Injected} 件{extra}");
            }
            Info($"{mapKey}：容器注入合计 命中 {hAll}/{tAll} 个容器，注入 {iAll} 件"
               + (sAll > 0 ? $"（尺寸跳过 {sAll}）" : "")
               + (fAll > 0 ? $"（失败 {fAll}）" : "")
               + $"；chance={st.Chance:0.###}，每容器最多 1 件"
               + (run <= 1 ? "（以下为**首局**统计；之后每局重新独立掷骰，不再刷屏）" : ""));

            // ★★★ v3【覆盖率】本图多少容器落在白名单里 / 哪些 tpl 还没归类。
            //   这是"扩白名单"这件事唯一的量化验收口径（woods 429 个 ⇒ 现在 420 个 = 97.9%）。
            if (st.AllTotal > 0)
            {
                int covered = st.AllTotal - st.UnmatchedTpl.Values.Sum();
                double pct = 100.0 * covered / st.AllTotal;
                string unmatched = st.UnmatchedTpl.Count == 0
                    ? ""
                    : "；未归类 tpl：" + string.Join("、", st.UnmatchedTpl.OrderByDescending(kv => kv.Value)
                        .Select(kv => $"{ItemNames.Short(kv.Key)}({kv.Value})"));
                Info($"{mapKey}：容器覆盖率 {covered}/{st.AllTotal} 属于注入类别（{pct:0.0}%）{unmatched}");
            }
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  小工具
    // ══════════════════════════════════════════════════════════════════════════

    private static List<string> OrderedCategoryKeys(IEnumerable<string> keys)
    {
        var res = new List<string>();
        var all = keys?.ToList() ?? new List<string>();
        foreach (var (k, _) in CategoryOrder)
        {
            var hit = all.FirstOrDefault(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase));
            if (hit != null) res.Add(hit);
        }
        foreach (var k in all)
            if (!res.Contains(k, StringComparer.OrdinalIgnoreCase)) res.Add(k);
        return res;
    }

    private static string LabelOf(string key)
    {
        foreach (var (k, label) in CategoryOrder)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return label;
        return string.IsNullOrWhiteSpace(key) ? "未命名类别" : key;
    }

    private static PoolItem PickWeighted(List<PoolItem> pool, Random rng)
    {
        if (pool == null || pool.Count == 0) return null;
        long total = 0;
        foreach (var it in pool) total += it.Weight;
        if (total <= 0) return pool[0];
        double r = rng.NextDouble() * total;
        foreach (var it in pool)
        {
            r -= it.Weight;
            if (r < 0) return it;
        }
        return pool[pool.Count - 1];
    }

    /// <summary>取容器模板里的"根物品 tpl"（`Items[]` 中 `_id == Root` 的那件）。</summary>
    private static string RootTemplateOf(object tmpl)
    {
        try
        {
            var items = R.Prop(tmpl, "Items") as IEnumerable;
            if (items == null) return null;
            var root = R.Prop(tmpl, "Root")?.ToString();
            object first = null;
            foreach (var it in items)
            {
                if (it == null) continue;
                if (first == null) first = it;
                if (!string.IsNullOrEmpty(root) &&
                    string.Equals(R.Prop(it, "Id")?.ToString(), root, StringComparison.OrdinalIgnoreCase))
                    return R.Prop(it, "Template")?.ToString();
            }
            // 兜底：Root 字段缺失/对不上时按 SPT 惯例取 Items[0]
            return first == null ? null : R.Prop(first, "Template")?.ToString();
        }
        catch { return null; }
    }

    private static string RootIdOf(object tmpl, IEnumerable items)
    {
        try
        {
            var root = R.Prop(tmpl, "Root")?.ToString();
            object first = null;
            foreach (var it in items)
            {
                if (it == null) continue;
                if (first == null) first = it;
                var id = R.Prop(it, "Id")?.ToString();
                if (!string.IsNullOrEmpty(root) && string.Equals(id, root, StringComparison.OrdinalIgnoreCase)) return id;
            }
            return first == null ? null : R.Prop(first, "Id")?.ToString();
        }
        catch { return null; }
    }

    private static bool AllowedByOnlyOnMap(string onlyOnMap, string mapKey)
    {
        if (string.IsNullOrWhiteSpace(onlyOnMap)) return true;
        foreach (var t in onlyOnMap.Split(new[] { ',', ';', '|', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(t.Trim(), mapKey, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static object FindLocation(IDictionary dict, string mapKey)
    {
        try
        {
            foreach (var k in dict.Keys)
                if (string.Equals(k?.ToString(), mapKey, StringComparison.OrdinalIgnoreCase)) return dict[k];
        }
        catch { }
        return null;
    }

    private static Type GenericArg(Type t)
    {
        try
        {
            if (t == null) return null;
            var u = Nullable.GetUnderlyingType(t) ?? t;
            if (u.IsArray) return u.GetElementType();
            if (u.IsGenericType && u.GetGenericArguments().Length == 1) return u.GetGenericArguments()[0];
        }
        catch { }
        return null;
    }

    private static Type PropType(object o, string name)
    {
        try { return o?.GetType().GetProperty(name, R.Any)?.PropertyType; }
        catch { return null; }
    }

    private static string NewHex24()
    {
        for (int i = 0; i < 16; i++)
        {
            var h = Guid.NewGuid().ToString("N");
            if (h.Length >= 24) return h.Substring(0, 24);
        }
        return Guid.NewGuid().ToString("N").Substring(0, 24);
    }

    private static bool IsHex24(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length != 24) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        }
        return true;
    }

    private static int ToInt(object v, int fallback)
    {
        try { return v == null ? fallback : Convert.ToInt32(v, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    private static int SeedFor(string s)
    {
        unchecked
        {
            int h = 17;
            if (!string.IsNullOrEmpty(s)) foreach (var c in s) h = h * 31 + c;
            return h;
        }
    }
}
