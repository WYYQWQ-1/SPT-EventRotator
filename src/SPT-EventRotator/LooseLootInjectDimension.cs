using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;

namespace EventRotator;

/// <summary>
/// ★★★ **松散战利品注入维度**（`events.json` 字段 <see cref="EventDef.LooseLootInject"/>）。
///
/// 需求（用户原话）：活动期间改地图**松散战利品** —— ① 增加点位、② 把点位里的物品换成指定的
/// 高价值物（私酒/颜色卡/符号钥匙）或"日常肥物"（纯净水/玩偶）；活动结束/换活动**必须完整还原**。
///
/// ── 落点（真实 DLL 反射 + IL 反编译，不是猜的）─────────────────────────────────────
///   `LocationTable → Location.LooseLoot : LazyLoad&lt;LooseLoot&gt;`
///     → `LooseLoot.Spawnpoints : IEnumerable&lt;Spawnpoint&gt;`（实际实例 `List&lt;Spawnpoint&gt;`）
///       · `Spawnpoint.LocationId : string?`   —— 形如 `"(x, y, z)"`；**刷点去重键**
///       · `Spawnpoint.Probability : double?`  —— 该点抽取权重
///       · `Spawnpoint.Template : SpawnpointTemplate`
///       · `Spawnpoint.ItemDistribution : IEnumerable&lt;LooseLootItemDistribution&gt;`
///     → `SpawnpointTemplate`：`Id`(string，**不是 mongoId**，形如 `loot_alarm_pult (4) [uuid]`) /
///        `Position`/`Rotation`(`readonly struct Vector3`，`X/Y/Z` 为 **float**) /
///        `Root`(string，= 首个 Item 的 `_id`) / `Items : IEnumerable&lt;SptLootItem&gt;`
///     → `LooseLoot.SpawnpointCount : SpawnpointCount`（`required double Mean` / `Std`）
///
/// ── ★★ 为什么必须挂 `AddTransformer` 而不是直接改 `.Value`（本次踩到的坑，IL 铁证）─────────
///   `SPTarkov.Server.Core.Utils.Json/LazyLoad.cs`：
///     `public class LazyLoad<T>(Func<T> deserialize, bool cacheValue = false)`
///     `get => cacheValue ? (cached ?? (cached = Build())) : Build();`
///   而 `SPTarkov.Server.Core.Utils/ImporterUtil.cs:CreateLazyLoadDeserialization`：
///     `bool flag = type.GetCustomAttribute<CacheLazyLoadAttribute>() != null;`
///     `Activator.CreateInstance(propertyType, obj, flag)`      // type = T（= LooseLoot）
///   全库带 `[CacheLazyLoad]` 的类型只有 **1 个**（`GlobalLocaleDictionary`），`LooseLoot` **没有**
///   ⇒ `Location.LooseLoot` 的 **cacheValue = false**
///   ⇒ 每次读 `.Value` 都**重新反序列化 looseLoot.json**（35 MB），"在 .Value 上改完就走"的改法是**无效的**
///     （下一局读到的又是全新对象）。官方正确姿势 = `looseLoot.AddTransformer(Func<LooseLoot,LooseLoot>)`
///     —— 每次 Build 依次套上去（`LazyLoad.Build()` 里的那个 foreach）。
///   ⇒ 本维度：**只挂 transformer，不缓存对象、不写盘**；transformer 里按"当前活动配置"改这一份新对象。
///     还原 = 把 `_active` 里该图的配置摘掉（transformer 变直通）⇒ **数据从头到尾没被写盘，天然逐字节还原**。
///
/// ── ★ 为什么"加点位"还要改 `SpawnpointCount.Mean`（IL 铁证）──────────────────────────
///   `LocationLootGenerator.GenerateDynamicLoot`（反编译）：
///     · `num = Math.Round(GetLooseLootMultiplierForLocation(name)
///                         * randomUtil.GetNormallyDistributedRandomNumber(SpawnpointCount.Mean, SpawnpointCount.Std));`
///     · 塞进 `ProbabilityObjectArray` 后 `DrawAndRemove((int)num)` —— **只抽 num 个**；
///     · 抽完 `group by LocationId`（**同 LocationId 只留一个**）；
///     · 每个点从 `ItemDistribution` 按 `relativeProbability` **抽 1 条**，再把 `Items` **过滤成只剩那条**。
///   ⇒ ① 光加点位不动 Mean，局内数量不变；② 克隆点必须换 LocationId 否则被合并；
///     ③ 只改 `Items[0]._tpl` 在多物品刷点里只有 1/N 概率落地 ⇒ 替换做成"**收敛成单物品**"。
///
/// ── 幂等 / 还原 / 降级 ────────────────────────────────────────────────────────────────
///   · 幂等：`_active[地图]` 记当前活动配置；同一活动重复 Apply 直接跳过；`_hooked` 保证 transformer 只挂一次。
///   · 还原：摘掉 `_active[地图]`（transformer 直通）。transformer 每局都在"新鲜对象"上跑 ⇒ 不累积。
///   · 降级：地图/LazyLoad/AddTransformer/坐标/构造器任一缺失 ⇒ warning + 跳过，绝不抛；
///           transformer 内部异常 ⇒ 打 warning 并原样返回（本局退化成官方行为）。
///   · 坐标：新增点只做 **±[0.4, 1.2] m 的 x/z 圆盘偏移、y 原样不动**（避免悬空/入地）。
///
/// ── regions（地标包围盒 = 圆心 ± 半径）─────────────────────────────────────────────────
///   `min/max` 只用水平面 (x,z)；点位落进盒子即"命中"。
///   · **确定性替换**：命中点按"距圆心由近到远"排，前 `maxPoints` 个**直接**换成 `valuable.tpls` 的物品
///     （不掷骰）⇒ "中心最肥"；超出上限的命中点照旧走 `valuable.chance × valuableBoost`。
///   · **重叠**：一个点位只归优先级最高的那个区（`maxPoints` 小 → 更精确 → 先生效；同 cap 比盒子面积；
///     再按声明顺序）⇒ 大区（如"伐木场大区"）不会把小区（三联房/紫卡房）的点抢走，也不会重复替换。
///   · **上限**：每区最多改 `maxPoints` 个点（防"整张图像素级高价值"）；若合计超过点位的 25% 会打 warning。
///   · **日志**：每区一行"命中 N 个，实际替换 M 个（上限 X）"，命中 0 也会打（= 坐标/半径可能不对）。
///   · 坐标来源 = 用户实机 F9 录制的 `audit/woods-录制点位.json`，离线用 `tools/verify_regions.py` 复核。
///
/// ── regions 的 3D 锚点 / 必刷（2026-10-24，interchange「小黄车」实测逼出来的）──────────────────
///   ★ 事故：interchange `region「小黄车」min(98,278) max(124,304)` 日志写"命中 6 个，实际替换 2 个"，
///     玩家跑到车旁**什么也没有**。离线在这份**实机加载的 ManimalInterchange looseLoot.json** 上复算：
///     盒内 6 个点、最近的 2 个 3D 距车 **4.51 m / 4.75 m**（x/z 4.35/4.61、y 22.54/22.51 vs 车 21.34）
///     ⇒ **坐标不是问题**（若 x/z 被交换，盒内就是 0 个点）。真因两条：
///       ① **模型不对**（prefab 用的是 `item_barter_flyers_strange_case` 传单箱 ⇒ 地上像一张海报）；
///       ② **点位是概率刷的**：那 2 个点的 `probability` 只有 0.437 / 0.411（Σp=578.03），而
///          `num = round(interchange 倍率 2.8 × Normal(621.244, 18.379)) ≈ 1739` 从 3820 点里抽
///          ⇒ 单点落地率 ≈ 73% / 71%，**两个点同时不出的概率 ≈ 8%**。
///          ⇒ 次要因素（8% 不该留给一条寻物任务的关键道具），但"替换了"跟"玩家能看到"确实不是一回事。
///   ⇒ 新增两个字段：
///     · `anchor{x,y,z}` + `anchorName`：按**真实 3D 距离**挑最近的 N 个点，并按 `距{anchorName} d 米`
///       逐点打坐标（最多 5 行，只有写了 anchor 的区才打，避免森林 13 个区刷屏）；
///     · `guaranteedPoints`：前 N 个（= 最近的）把 `Probability` 设成 **1.0** ⇒ 不进 `DrawAndRemove`
///       的概率抽取、每局必刷（`GenerateDynamicLoot` 的 `if (Probability == 1.0) list3.Add(...)`）。
///   两者都**只改本次反序列化出来的对象**（transformer 里），json 全程只读，天然完整还原。
///
/// ── 容器类 tpl 诊断（2026-10-24）────────────────────────────────────────────────────────
///   注入的 tpl 会去 `TemplateTable.Items` 里查一次：若该物品 `_props.Grids` 非空
///   （= 物品"是容器"的等价判据；`SpawnpointTemplate.IsContainer` 是刷点字段，不是物品字段），
///   打 warning「这类物品当 loose loot 刷在地上可能不显示」。只警告、不阻止 ——
///   原版武器箱 `59fb023c86f7746d0d4b423c`（SimpleContainer/5×10 Grids）当 loose loot 落地后
///   玩家看不到，本 mod 已把它换成自造的非容器物品 `6a1f00c70000000000000001`。
///   **⚠ 客户端为什么吞掉容器类 loose loot：未证实**（没反编译到 loose loot 实例化路径）。
/// </summary>
internal static class LooseLootInjectDimension
{
    public static object LocationTable;
    /// <summary>SPT `TemplateTable`（判"注入的 tpl 是不是容器类物品"用；null = 不做该诊断）。</summary>
    public static object TemplateTable;
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    /// <summary>某张图当前生效的注入配置（不存在 = transformer 直通）。</summary>
    private sealed class MapState
    {
        public string EvId;
        public string MapKey;
        public LooseLootInjectMapOpt Opt;
        public int Runs;        // transformer 实际生效次数（= 该活动下来生成过几次战利品）
        public bool Logged;
        /// <summary>本次追加的刷点实例（+ 所属列表），活动关闭时按此移除；原有点不碰。</summary>
        public readonly List<(IList List, object Point)> Added = new();
    }

    private sealed class Stats
    {
        public int Orig, Added, Valuable, Decor, Miss;
        public int AddAdded, AddSkipped;
        public double Mult = 1, MeanOld, MeanNew, ChanceV, ChanceD;
        public bool MeanTouched, HasRegions;
        public int RegionsCount;
        public Dictionary<string, int> Tpls = new(StringComparer.OrdinalIgnoreCase);
        // regions：命中（盒内点位数，未按重叠去重）/ 实际确定性替换数 / 其中被设成必刷(Probability=1.0)的数量 / 合计
        public List<LooseLootRegionOpt> Regions;
        public int[] RegionRaw = Array.Empty<int>();
        public int[] RegionRepl = Array.Empty<int>();
        public int[] RegionGuaranteed = Array.Empty<int>();
        public int RegionTotal;
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<string, MapState> _active = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _hooked = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>已经警告过的"容器类 tpl"（同一个 tpl 只打一次，避免 chance 写错时刷屏）。</summary>
    private static readonly HashSet<string> _warnedContainerTpl = new(StringComparer.OrdinalIgnoreCase);

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][地面战利品注入] " + m); } catch { } }
    private static void Warn(string m) { try { Log?.Invoke("[EventRotator][地面战利品注入] ⚠ " + m); } catch { } }

    /// <summary>供 Plugin 在"还原+套用"开头调用：清掉"当前生效配置"（transformer 随即直通）。</summary>
    public static void ResetApplied() { try { lock (_gate) _active.Clear(); } catch { } }

    // ══════════════════════════════════════════════════════════════════════════
    //  入口
    // ══════════════════════════════════════════════════════════════════════════

    public static void Apply(EventDef ev)
    {
        if (ev?.LooseLootInject == null || ev.LooseLootInject.Count == 0) return;
        if (LocationTable == null) { Warn("拿不到 LocationTable，本维度跳过"); return; }
        try
        {
            var dict = R.LocationDict(LocationTable);
            if (dict == null) { Warn("LocationTable 取不到地图字典，本维度跳过"); return; }

            foreach (var mapKv in ev.LooseLootInject)
            {
                try { ApplyMap(ev, dict, mapKv.Key, mapKv.Value); }
                catch (Exception ex) { Warn($"地图 '{mapKv.Key}' 注入失败（已降级，其余地图照常）：{ex.GetType().Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Warn("失败（已降级，不影响服务端）：" + ex.Message); }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  单图：登记配置 + 挂 transformer + 立即自证一次
    // ══════════════════════════════════════════════════════════════════════════

    private static void ApplyMap(EventDef ev, IDictionary locTable, string mapKey, LooseLootInjectMapOpt opt)
    {
        if (opt == null || string.IsNullOrWhiteSpace(mapKey)) return;

        if (!AllowedByOnlyOnMap(ev.OnlyOnMap, mapKey))
        {
            Info($"地图 '{mapKey}' 不在 onlyOnMap='{ev.OnlyOnMap}' 允许范围内 ⇒ 跳过");
            return;
        }

        var loc = FindLocation(locTable, mapKey);
        if (loc == null) { Warn($"找不到地图 '{mapKey}'，跳过"); return; }

        // ★ 关键：拿的是 LazyLoad<LooseLoot> **本体**，不读 .Value（cacheValue=false，读了也只是白跑一次）
        var lazy = R.Prop(loc, "LooseLoot");
        if (lazy == null) { Warn($"地图 '{mapKey}' 没有 LooseLoot，跳过"); return; }

        var state = new MapState { EvId = ev.Id, MapKey = mapKey, Opt = opt };
        bool needHook;
        lock (_gate)
        {
            if (_active.TryGetValue(mapKey, out var cur) && string.Equals(cur.EvId, ev.Id, StringComparison.OrdinalIgnoreCase))
            {
                Info($"地图 '{mapKey}' 的同一活动已登记过（幂等跳过；transformer 已在位）");
                return;
            }
            _active[mapKey] = state;
            needHook = !_hooked.Contains(mapKey);
        }

        if (needHook && !Hook(lazy, mapKey))
        {
            lock (_gate) { if (_active.TryGetValue(mapKey, out var s) && ReferenceEquals(s, state)) _active.Remove(mapKey); }
            Warn($"地图 '{mapKey}' 挂 AddTransformer 失败 ⇒ 本维度对该图不生效");
            return;
        }

        // 自证一次：读 .Value 会触发 Build → transformer → 打印点位/替换统计
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
                }
                int removed = 0;
                List<(IList List, object Point)> added;
                lock (_gate) { added = capState.Added.ToList(); capState.Added.Clear(); }
                foreach (var (lst, pt) in added)
                {
                    try { if (lst != null && pt != null && lst.Contains(pt)) { lst.Remove(pt); removed++; } }
                    catch { }
                }
                Info($"✔ {mapKey} 已还原：transformer 转为直通（该活动期间共按注入规则生成过 {capState.Runs} 次；"
                   + (added.Count > 0 ? $"按坐标追加的 {added.Count} 个点已移除（实际仍在列表里的 {removed} 个）" : "无按坐标追加的点")
                   + "；looseLoot.json 全程只读、从未写盘 ⇒ 下一次读取即原始数据）");
            }
            catch (Exception ex) { Warn($"{mapKey} 还原异常：{ex.Message}"); }
        });
    }

    /// <summary>把"直通 → 注入"的 `Func&lt;LooseLoot,LooseLoot&gt;` 挂到官方 `LazyLoad.AddTransformer`（反射 + 表达式）。</summary>
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

            var mi = typeof(LooseLootInjectDimension).GetMethod(nameof(TransformerEntry),
                                                               System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (mi == null) { Warn("找不到 TransformerEntry"); return false; }

            var p = Expression.Parameter(t, "v");
            var body = Expression.Block(
                Expression.Call(mi, Expression.Constant(mapKey), Expression.Convert(p, typeof(object))),
                p);                                  // 改完把同一个对象原样返回（官方 transformer 约定）
            var del = Expression.Lambda(funcType, body, p).Compile();
            add.Invoke(lazy, new object[] { del });

            lock (_gate) _hooked.Add(mapKey);
            Info($"{mapKey}：已挂上官方 LazyLoad.AddTransformer（LooseLoot 无 [CacheLazyLoad] ⇒ cacheValue=false，"
               + "直接改 .Value 会被下一次读取丢弃）");
            return true;
        }
        catch (Exception ex) { Warn("挂 transformer 失败：" + ex.Message); return false; }
    }

    /// <summary>官方 transformer 回调：每局反序列化出一份**新** LooseLoot 后由 SPT 调用。</summary>
    private static void TransformerEntry(string mapKey, object loose)
    {
        MapState st = null;
        lock (_gate) { if (!_active.TryGetValue(mapKey, out st)) return; }
        if (st == null || loose == null) return;
        try
        {
            // ★ 逐点坐标日志只在**本活动第一次**生成时打（= 与 LogStats 同一策略：每活动一次，
            //   避免每次读 .Value（每局都会重新反序列化 + 过 transformer）都刷 3 行）。
            var stats = Inject(loose, mapKey, st.Opt, logPoints: !st.Logged);
            st.Runs++;
            if (!st.Logged) { st.Logged = true; LogStats(mapKey, stats); }
        }
        catch (Exception ex)
        {
            Warn($"{mapKey} 注入异常（本局退化为官方原样）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  真正的注入（作用在"本次反序列化出来的那个" LooseLoot 上）
    // ══════════════════════════════════════════════════════════════════════════

    private static Stats Inject(object value, string mapKey, LooseLootInjectMapOpt opt, bool logPoints = false)
    {
        var st = new Stats
        {
            Mult = opt.DensityMultiplier ?? 1.0,
            HasRegions = opt.Regions != null && opt.Regions.Count > 0,
            RegionsCount = opt.Regions?.Count ?? 0,
            ChanceV = opt.Valuable?.Chance ?? 0,
            ChanceD = opt.Decor?.Chance ?? 0
        };

        var spawnpoints = R.Prop(value, "Spawnpoints");
        if (spawnpoints == null) { Warn($"地图 '{mapKey}' 的 LooseLoot 没有 Spawnpoints，跳过"); return st; }

        IList list = spawnpoints as IList;
        if (list == null)
        {
            var made = Materialize(value, spawnpoints);
            if (made == null) { Warn($"地图 '{mapKey}' 的 Spawnpoints 既不是 IList 也无法物化，跳过"); return st; }
            list = made;
        }

        st.Orig = list.Count;
        if (st.Orig <= 0) { Warn($"地图 '{mapKey}' 的 Spawnpoints 为空，跳过"); return st; }

        // ---- ① 点位克隆（densityMultiplier）----
        if (st.Mult > 0 && Math.Abs(st.Mult - 1.0) > 1e-9)
        {
            int target = (int)Math.Round(st.Orig * st.Mult);
            int need = Math.Max(0, target - st.Orig);
            if (need == 0)
            {
                Info($"地图 '{mapKey}'：densityMultiplier={st.Mult:0.###} ⇒ 目标 {target} 点，无需克隆");
            }
            else
            {
                var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var usedLocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sources = new List<object>();
                foreach (var sp in list)
                {
                    if (sp == null) continue;
                    sources.Add(sp);
                    try
                    {
                        var t = R.Prop(sp, "Template");
                        var id = R.Prop(t, "Id")?.ToString(); if (!string.IsNullOrEmpty(id)) usedIds.Add(id);
                        var l = R.Prop(sp, "LocationId")?.ToString(); if (!string.IsNullOrEmpty(l)) usedLocs.Add(l);
                    }
                    catch { }
                }
                var rng = new Random(SeedFor(mapKey));
                var order = ShuffledIndices(sources.Count, rng);
                int cursor = 0, fail = 0;
                for (int i = 0; i < need && sources.Count > 0; i++)
                {
                    var src = sources[order[cursor % order.Count]];
                    cursor++;
                    var clone = CloneSpawnpoint(src, rng, usedIds, usedLocs);
                    if (clone == null) { fail++; continue; }
                    try { list.Add(clone); st.Added++; }
                    catch (Exception ex) { Warn($"{mapKey} 克隆点加入失败：{ex.Message}"); break; }
                }
                if (fail > 0) Warn($"地图 '{mapKey}'：有 {fail} 个克隆点因缺 Position/构造失败被跳过");
            }
        }

        // ---- ② SpawnpointCount.Mean（IL 铁证：局内数量 = Mean × location.json 倍率 的正态抽样）----
        var meanOwner = R.Prop(value, "SpawnpointCount");
        st.MeanOld = ToDouble(R.Prop(meanOwner, "Mean"), double.NaN);
        if (opt.ScaleSpawnpointCount != false && st.Mult > 0 && Math.Abs(st.Mult - 1.0) > 1e-9
            && meanOwner != null && !double.IsNaN(st.MeanOld))
        {
            try
            {
                st.MeanNew = st.MeanOld * st.Mult;
                st.MeanTouched = R.SetProp(meanOwner, "Mean", st.MeanNew);
            }
            catch (Exception ex) { Warn($"{mapKey} 缩放 SpawnpointCount.Mean 失败：{ex.Message}"); }
        }

        // ---- ③ 物品替换（regions 确定性替换 → valuable / decor 概率）----
        var vPool = BuildPool(opt.Valuable, mapKey, "valuable");
        var dPool = BuildPool(opt.Decor, mapKey, "decor");

        // ③-0 命中判定：每个点位只归**优先级最高**的那个 region
        //      优先级 = maxPoints 小者先（更精确的区先生效）→ 盒子小者先 → 声明顺序
        var regOrder = RegionOrder(opt.Regions);
        int rc = opt.Regions?.Count ?? 0;
        st.Regions = opt.Regions;
        st.RegionRaw = new int[rc];
        st.RegionRepl = new int[rc];
        st.RegionGuaranteed = new int[rc];

        var hitRegion = new int[list.Count];       // -1 = 不在任何 region
        var hitDist = new double[list.Count];      // 到 region **锚点**（无锚点 ⇒ 盒心）的距离 —— 排序用
        for (int i = 0; i < hitRegion.Length; i++) hitRegion[i] = -1;

        var eligible = new List<int>();            // 可替换点位（排除必刷/christmas/无坐标）
        var pts = new double[list.Count * 3];      // [i*3]=x, [i*3+1]=y, [i*3+2]=z（只为 eligible 填）
        for (int i = 0; i < list.Count; i++)
        {
            var sp0 = list[i];
            if (sp0 == null || !IsReplaceable(sp0)) continue;
            if (!TryPointXYZ(sp0, out var px0, out var py0, out var pz0)) continue;
            eligible.Add(i);
            pts[i * 3] = px0;
            pts[i * 3 + 1] = py0;
            pts[i * 3 + 2] = pz0;
        }

        if (rc > 0)
        {
            foreach (int ei in eligible)
            {
                double px = pts[ei * 3], py = pts[ei * 3 + 1], pz = pts[ei * 3 + 2];
                foreach (int ri in regOrder)
                {
                    if (!InBox(opt.Regions[ri], px, pz)) continue;
                    hitRegion[ei] = ri;
                    hitDist[ei] = RegionDist(opt.Regions[ri], px, py, pz);
                    break;                          // 只认优先级最高的那个区 ⇒ 同一位置只替换一次
                }
            }
            // 原始命中数（盒内点位，**不做重叠去重** —— 命中 0 就说明坐标/半径可能不对，要能看出来）
            for (int ri = 0; ri < rc; ri++)
            {
                if (opt.Regions[ri]?.Min == null || opt.Regions[ri].Max == null) continue;
                int n = 0;
                foreach (int ei in eligible)
                    if (InBox(opt.Regions[ri], pts[ei * 3], pts[ei * 3 + 2])) n++;
                st.RegionRaw[ri] = n;
            }
        }

        // ③-a 区内**确定性**替换：每区按"距离由近到远"（有锚点 ⇒ 3D 到锚点；无锚点 ⇒ 2D 到盒心）
        //      取前 maxPoints 个（中心最肥），不掷骰；其中前 guaranteedPoints 个再设 Probability=1.0 ⇒ 局内必刷
        var replaced = new HashSet<int>();
        if (regOrder.Count > 0 && vPool.Count > 0)
        {
            var buckets = new Dictionary<int, List<int>>();
            foreach (int ri in regOrder) buckets[ri] = new List<int>();
            foreach (int ei in eligible)
            {
                int ri = hitRegion[ei];
                if (ri < 0) continue;
                if (RegionCap(opt.Regions[ri]) == int.MaxValue) continue;   // 没写 maxPoints ⇒ 不做确定性替换
                buckets[ri].Add(ei);
            }
            var rngR = new Random(SeedFor(mapKey + "|regions"));
            foreach (int ri in regOrder)
            {
                var rg = opt.Regions[ri];
                int cap = RegionCap(rg);
                if (cap == int.MaxValue) continue;
                var cand = buckets[ri];
                cand.Sort((a, b) =>
                {
                    int c = hitDist[a].CompareTo(hitDist[b]);
                    return c != 0 ? c : a.CompareTo(b);      // 同距离按点位顺序，保证可复现
                });
                int want = Math.Min(cap, cand.Count), got = 0;
                int guaranteeWant = Math.Max(0, rg.GuaranteedPoints ?? 0);
                for (int k = 0; k < want; k++)
                {
                    var tpl = PickWeighted(vPool, rngR);
                    if (string.IsNullOrEmpty(tpl)) break;
                    if (!ReplaceOne(list[cand[k]], tpl, rngR, mapKey, out var why))
                    {
                        if (k < 3) Warn($"{mapKey}/region「{RegionName(rg, ri)}」替换失败：{why}");
                        continue;
                    }
                    replaced.Add(cand[k]);
                    got++;
                    st.Tpls[tpl] = (st.Tpls.TryGetValue(tpl, out var c2) ? c2 : 0) + 1;

                    // ★ 必刷：前 guaranteeWant 个（= 距离最近的）把 Probability 设成 1.0
                    if (got <= guaranteeWant && SetMustSpawn(list[cand[k]]))
                        st.RegionGuaranteed[ri]++;

                    if (logPoints) LogReplacedPoint(mapKey, rg, ri, list[cand[k]], got);
                }
                st.RegionRepl[ri] = got;
                st.RegionTotal += got;
            }
            if (st.RegionTotal * 4 > st.Orig)
                Warn($"{mapKey}：regions 确定性替换 {st.RegionTotal} 个 > 点位 {st.Orig} 的 25%"
                   + "（森林会变成全高价值，请检查各 region 的 maxPoints）");
        }

        // ③-b 全局概率替换：valuable → decor（区内**超限**的点位在这里维持全局 chance）
        if ((vPool.Count > 0 && st.ChanceV > 0) || (dPool.Count > 0 && st.ChanceD > 0))
        {
            var rng = new Random(SeedFor(mapKey + "|items"));
            int scanned = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var sp = list[i];
                if (sp == null) continue;
                scanned++;
                var tmpl = R.Prop(sp, "Template");
                if (tmpl == null) { st.Miss++; continue; }
                if (R.Prop(tmpl, "IsAlwaysSpawn") is bool always && always) continue;   // 必刷点（任务/固定）不动
                var tId = R.Prop(tmpl, "Id")?.ToString();
                if (!string.IsNullOrEmpty(tId) && tId.StartsWith("christmas", StringComparison.OrdinalIgnoreCase)) continue;
                if (replaced.Contains(i)) continue;                                       // 已被 ③-a 确定性替换过

                double boost = 1.0;
                int hri = hitRegion[i];
                if (hri >= 0) boost = opt.Regions[hri].ValuableBoost ?? 1.0;
                double pV = st.ChanceV * boost;
                if (pV > 1.0) pV = 1.0;

                string pool = null;
                if (pV > 0 && vPool.Count > 0 && rng.NextDouble() < pV) pool = "valuable";
                else if (st.ChanceD > 0 && dPool.Count > 0 && rng.NextDouble() < st.ChanceD) pool = "decor";
                if (pool == null) continue;

                var tpl = PickWeighted(pool == "valuable" ? vPool : dPool, rng);
                if (string.IsNullOrEmpty(tpl)) continue;
                if (!ReplaceOne(sp, tpl, rng, mapKey, out var why))
                {
                    st.Miss++;
                    if (st.Miss <= 3) Warn($"{mapKey} 有一处替换失败：{why}");
                    continue;
                }
                if (pool == "valuable") st.Valuable++; else st.Decor++;
                st.Tpls[tpl] = (st.Tpls.TryGetValue(tpl, out var c) ? c : 0) + 1;
            }
            if (scanned > 0 && (st.Valuable + st.Decor) == 0) Warn($"{mapKey}：扫了 {scanned} 个点位但一处都没替换（chance 太低或池子为空）");
        }

        // ---- ④ 按精确坐标追加刷点 ----
        AddExactPoints(list, mapKey, opt, st, logPoints);

        // ④-b SpawnpointCount.Mean 补偿：局内数量 = Mean × location 倍率 的正态抽样（见文件头 IL 铁证）
        //    ⇒ 只加点不抬 Mean 等于"候选池变大、局内数量不变"；这里按追加点数线性补上（不乘倍率，避免叠加过头）。
        if (st.AddAdded > 0 && opt.ScaleSpawnpointCount != false
            && meanOwner != null && !double.IsNaN(st.MeanOld))
        {
            try
            {
                double cur = ToDouble(R.Prop(meanOwner, "Mean"), st.MeanOld);
                st.MeanNew = cur + st.AddAdded;
                if (R.SetProp(meanOwner, "Mean", st.MeanNew)) st.MeanTouched = true;
            }
            catch (Exception ex) { Warn($"{mapKey} 按追加点补偿 SpawnpointCount.Mean 失败：{ex.Message}"); }
        }

        return st;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  按精确坐标追加刷点（addPoints）
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 在本次反序列化出来的 LooseLoot 上追加 <see cref="LooseLootInjectMapOpt.AddPoints"/>。
    /// <list type="bullet">
    /// <item>tpl 模式：造"单物品 SpawnpointTemplate"（Id/Root 新 uuid、Items 一条 SptLootItem、位置=坐标、旋转 0）；</item>
    /// <item>itemsJson 模式：把 JSON 反序列化成 SpawnpointTemplate（项目 JsonUtil 同一套往返），位置照坐标覆盖；</item>
    /// <item>去重：列表里已有同坐标（数值容差 1e-3）则跳过；</item>
    /// <item>坏坐标 / tpl 不合法 / 反序列化失败 ⇒ 只 warning 跳过这一条，其它点照常加。</item>
    /// </list>
    /// </summary>
    private static void AddExactPoints(IList list, string mapKey, LooseLootInjectMapOpt opt, Stats st, bool logPoints)
    {
        var pts = opt?.AddPoints;
        if (pts == null || pts.Count == 0) return;

        // 只记"最近一次反序列化"的那批实例（每次 .Value 都是新对象，旧列表已无引用 ⇒ 不必累积）
        MapState curState;
        lock (_gate) _active.TryGetValue(mapKey, out curState);
        if (curState != null) { lock (_gate) curState.Added.Clear(); }

        // 现有坐标（数值比较：源数据的小数位/格式不完全统一）
        var existing = new List<(double X, double Y, double Z)>();
        foreach (var sp0 in list)
        {
            if (sp0 == null) continue;
            var lid = R.Prop(sp0, "LocationId")?.ToString();
            if (TryParseLocation(lid, out var ex, out var ey, out var ez)) existing.Add((ex, ey, ez));
        }

        object sample = null;
        foreach (var sp0 in list) { if (sp0 != null) { sample = sp0; break; } }
        Type tmplType = sample == null ? null : PropType(sample, "Template");
        if (tmplType == null) { Warn($"{mapKey}：addPoints 需要至少一个原有点位来取模板类型 ⇒ 跳过"); return; }

        var distType = DistElemType(sample);

        foreach (var p in pts)
        {
            if (p == null) continue;
            try
            {
                if (!TryParseLocation(p.LocationId, out var x, out var y, out var z))
                {
                    Warn($"{mapKey}/addPoints：坐标无法解析 '{p.LocationId}'（支持 \"(x, y, z)\"、\".\" 与俄式 \",\" 小数）⇒ 跳过");
                    st.AddSkipped++; continue;
                }

                bool dup = false;
                foreach (var (ex, ey, ez) in existing)
                {
                    if (Math.Abs(ex - x) < 1e-3 && Math.Abs(ey - y) < 1e-3 && Math.Abs(ez - z) < 1e-3) { dup = true; break; }
                }
                if (dup) { st.AddSkipped++; continue; }

                var sp = MakeSpawnpoint(list, sample, tmplType, distType, p, x, y, z);
                if (sp == null) { st.AddSkipped++; continue; }

                list.Add(sp);
                existing.Add((x, y, z));
                st.AddAdded++;
                if (curState != null) { lock (_gate) curState.Added.Add((list, sp)); }
            }
            catch (Exception ex)
            {
                st.AddSkipped++;
                Warn($"{mapKey}/addPoints：追加失败（跳过）：{ex.GetType().Name}: {ex.Message}");
            }
        }
        if (logPoints && (st.AddAdded > 0 || st.AddSkipped > 0))
            Info($"{mapKey}：按坐标追加 {st.AddAdded} 个刷点（跳过 {st.AddSkipped} 个已存在/解析失败）");
    }

    /// <summary>构造一个 Spawnpoint（tpl 模式造单物品模板；itemsJson 模式反序列化完整模板）。返回 null = 该条跳过。</summary>
    private static object MakeSpawnpoint(IList list, object sample, Type tmplType, Type distType,
                                        LooseLootAddPointOpt p, float x, float y, float z)
    {
        object tmpl;
        var composedKeys = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.ItemsJson))
        {
            tmpl = R.FromJson(tmplType, p.ItemsJson, m => Warn(m));
            if (tmpl == null) { Warn("itemsJson 反序列化失败 ⇒ 跳过该点"); return null; }
            // 位置照 locationId 覆盖；Rotation 保留 JSON 里给的（高级用法里可能是精确朝向）
            R.SetProp(tmpl, "Position", MakeVector3(PropType(tmpl, "Position"), x, y, z));
            // Template.Id 是 ProbabilityObjectArray 的 key ⇒ 必须全局唯一，否则会被合并成一个刷点
            var oldTid = StripTrailingBracket(R.Prop(tmpl, "Id")?.ToString());
            R.SetProp(tmpl, "Id", (string.IsNullOrEmpty(oldTid) ? "loot_inject" : oldTid) + " [" + Guid.NewGuid().ToString("D") + "]");
            if (R.Prop(tmpl, "Items") is IEnumerable its)
            {
                object firstItem = null;
                foreach (var it in its)
                {
                    if (it == null) continue;
                    firstItem ??= it;
                    // 外部数据（如 Scourge questLoot.json）的 Items 通常**没有** composedKey
                    // ⇒ 这里补一个并写回，否则 ItemDistribution 对不上、局内抽不到物品。
                    var ck = R.Prop(it, "ComposedKey")?.ToString();
                    if (string.IsNullOrEmpty(ck))
                    {
                        ck = Guid.NewGuid().ToString("N").Substring(0, 9);
                        R.SetProp(it, "ComposedKey", ck);
                    }
                    composedKeys.Add(ck);
                }
                if (firstItem != null && string.IsNullOrEmpty(R.Prop(tmpl, "Root")?.ToString()))
                    R.SetProp(tmpl, "Root", R.Prop(firstItem, "Id")?.ToString());
            }
            if (composedKeys.Count == 0)
                Warn("itemsJson 里没有任何物品（Items 为空）⇒ 该点进游戏刷不出东西（仍按原样加入）");
        }
        else
        {
            var tpl = (p.Tpl ?? "").Trim();
            if (!IsHex24(tpl)) { Warn($"addPoints：tpl '{p.Tpl}' 不是 24 位 hex ⇒ 跳过该点"); return null; }
            if (TemplateTable != null)
            {
                var items = R.Prop(TemplateTable, "Items") as IDictionary;
                if (items != null && !items.Contains(tpl))
                { Warn($"addPoints：tpl '{tpl}' 不在 items 表里（拼错或 mod 未加载）⇒ 跳过该点"); return null; }
            }
            var ck = Guid.NewGuid().ToString("N").Substring(0, 9);
            var itemId = Guid.NewGuid().ToString("N").Substring(0, 24);
            var json = "{"
                + "\"Id\":\"loot_inject [" + Guid.NewGuid().ToString("D") + "]\","
                + "\"IsContainer\":false,\"useGravity\":false,\"randomRotation\":false,"
                + "\"Position\":{\"x\":" + x.ToString(CultureInfo.InvariantCulture)
                + ",\"y\":" + y.ToString(CultureInfo.InvariantCulture)
                + ",\"z\":" + z.ToString(CultureInfo.InvariantCulture) + "},"
                + "\"Rotation\":{\"x\":0,\"y\":0,\"z\":0},"
                + "\"IsGroupPosition\":false,\"GroupPositions\":[],\"IsAlwaysSpawn\":false,"
                + "\"Root\":\"" + itemId + "\","
                + "\"Items\":[{\"composedKey\":\"" + ck + "\",\"_id\":\"" + itemId
                + "\",\"_tpl\":\"" + tpl + "\",\"upd\":{\"StackObjectsCount\":1}}]"
                + "}";
            tmpl = R.FromJson(tmplType, json, m => Warn(m));
            if (tmpl == null) { Warn("tpl 模式造 SpawnpointTemplate 失败 ⇒ 跳过该点"); return null; }
            composedKeys.Add(ck);
        }

        object sp;
        try { sp = Activator.CreateInstance(list.Count > 0 && list[0] != null ? list[0].GetType() : sample.GetType()); }
        catch { sp = null; }
        if (sp == null) { Warn("造 Spawnpoint 实例失败 ⇒ 跳过该点"); return null; }

        var locId = FormatLocationId(x, y, z);
        if (!R.SetProp(sp, "LocationId", locId)) { Warn("设置 Spawnpoint.LocationId 失败 ⇒ 跳过该点"); return null; }
        R.SetProp(sp, "Probability", p.Probability ?? 1.0);
        R.SetProp(sp, "Template", tmpl);

        if (distType != null && composedKeys.Count > 0)
        {
            var newDist = R.NewListOf(distType);
            if (newDist is IList nd)
            {
                foreach (var ck in composedKeys)
                {
                    var entry = R.FromJson(distType, "{\"composedKey\":{\"key\":\"" + ck + "\"},\"relativeProbability\":1}", m => Warn(m));
                    if (entry != null) nd.Add(entry);
                }
                R.SetProp(sp, "ItemDistribution", nd);
            }
        }
        return sp;
    }

    /// <summary>从原有刷点取 `ItemDistribution` 的元素类型（没有就按属性泛型参数推）。</summary>
    private static Type DistElemType(object sample)
    {
        try
        {
            if (R.Prop(sample, "ItemDistribution") is IEnumerable e)
            {
                foreach (var o in e) if (o != null) return o.GetType();
            }
        }
        catch { }
        return GenericArg(PropType(sample, "ItemDistribution"));
    }

    /// <summary>解析坐标字符串：`"(x, y, z)"`；支持 `.` 与俄式 `,` 小数（`-167,97823` = `-167.97823`）。</summary>
    private static bool TryParseLocation(string s, out float x, out float y, out float z)
    {
        x = y = z = float.NaN;
        try
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = s.Trim();
            if (t.StartsWith("(")) t = t.Substring(1);
            if (t.EndsWith(")")) t = t.Substring(0, t.Length - 1);
            var parts = t.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            string[] three;
            if (parts.Length == 3) three = parts;
            else if (parts.Length == 6) three = new[] { parts[0] + "." + parts[1], parts[2] + "." + parts[3], parts[4] + "." + parts[5] };
            else return false;

            var vals = new float[3];
            for (int i = 0; i < 3; i++)
            {
                var v = three[i].Replace(',', '.').Replace(" ", "");
                if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out vals[i])) return false;
            }
            x = vals[0]; y = vals[1]; z = vals[2];
            return true;
        }
        catch { return false; }
    }

    private static void LogStats(string mapKey, Stats st)
    {
        try
        {
            string mean = st.MeanTouched ? $"，SpawnpointCount.Mean {st.MeanOld:0.###} → {st.MeanNew:0.###}" : "";
            Info($"★ {mapKey}：点位 {st.Orig} → {st.Orig + st.Added}"
               + $"（densityMultiplier={st.Mult:0.###}，克隆 +{st.Added}{mean}）"
               + $"；物品替换 valuable {st.Valuable} / decor {st.Decor}"
               + (st.RegionTotal > 0 ? $"（另有 regions 确定性替换 {st.RegionTotal}）" : "")
               + (st.Miss > 0 ? $"（{st.Miss} 处跳过）" : "")
               + $"；valuable 基础概率 {st.ChanceV:0.###}、decor {st.ChanceD:0.###}"
               + (st.HasRegions ? $"（regions 生效 {st.RegionsCount} 个包围盒）" : "（regions 未启用：无地标坐标，走纯全局）"));

            // ★ 每个 region 一行：命中 N 个，实际替换 M 个（上限 X）—— 命中 0 也要打出来（说明坐标/半径不对）
            if (st.Regions != null && st.Regions.Count > 0)
            {
                for (int ri = 0; ri < st.Regions.Count; ri++)
                {
                    var rg = st.Regions[ri];
                    if (rg?.Min == null || rg.Max == null)
                    {
                        Info($"★ {mapKey}：region[{ri}]「{rg?.Name}」缺 min/max ⇒ 跳过");
                        continue;
                    }
                    int cap = RegionCap(rg);
                    string capTxt = cap == int.MaxValue ? "上限未设" : $"上限 {cap}";
                    Info($"★ {mapKey}：region「{RegionName(rg, ri)}」命中 {st.RegionRaw[ri]} 个"
                       + $"，实际替换 {st.RegionRepl[ri]} 个（{capTxt}）");
                    if (st.RegionGuaranteed != null && st.RegionGuaranteed.Length > ri && st.RegionGuaranteed[ri] > 0)
                        Info($"★ {mapKey}：region「{RegionName(rg, ri)}」其中 {st.RegionGuaranteed[ri]} 个点位 "
                           + "Probability=1.0 ⇒ **每局必刷**（SPT GenerateDynamicLoot：Probability==1.0 不进概率抽取，"
                           + "直接进 list3/list4）");
                }
                Info($"★ {mapKey}：regions 合计确定性替换 {st.RegionTotal} 个"
                   + $"（占点位 {st.Orig} 的 {(st.Orig > 0 ? 100.0 * st.RegionTotal / st.Orig : 0):0.0}%）");
            }

            if (st.AddAdded > 0 || st.AddSkipped > 0)
                Info($"{mapKey}：按坐标追加 {st.AddAdded} 个刷点（跳过 {st.AddSkipped} 个已存在/解析失败）");

            if (st.Tpls.Count > 0)
                Info($"★ {mapKey}：替换分布 " + string.Join("、",
                    st.Tpls.OrderByDescending(k => k.Value).Select(k => $"{k.Key}×{k.Value}")));
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  克隆 / 替换 / 区域
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 克隆一个刷点：JSON 往返造**独立对象图** ⇒ 重算 Template.Id / Root / 每个 Item._id /
    /// LocationId，并把 Position 做 ±[0.4,1.2]m 的 x-z 圆盘偏移（**y 不变**）。
    /// 返回 null = 该点缺 Position 或往返失败（跳过，不抛）。
    /// </summary>
    private static object CloneSpawnpoint(object src, Random rng, HashSet<string> usedIds, HashSet<string> usedLocs)
    {
        try
        {
            var json = R.ToJson(src, m => Warn(m));
            if (string.IsNullOrEmpty(json)) return null;
            var clone = R.FromJson(src.GetType(), json, m => Warn(m));
            if (clone == null) return null;

            var tmpl = R.Prop(clone, "Template");
            if (tmpl == null) return null;

            // ① Template.Id（string；ProbabilityObjectArray 的 key ⇒ 必须唯一）
            var oldId = R.Prop(tmpl, "Id")?.ToString();
            var baseId = StripTrailingBracket(oldId);
            if (string.IsNullOrEmpty(baseId)) baseId = "loot_inject";
            string newId = baseId + " [" + Guid.NewGuid().ToString("D") + "]";
            int guard = 0;
            while (!usedIds.Add(newId) && guard++ < 16) newId = baseId + " [" + Guid.NewGuid().ToString("D") + "]";
            R.SetProp(tmpl, "Id", newId);

            // ② 坐标：y 不变，x/z 做圆盘偏移（r ∈ [0.4,1.2]）
            var pos = R.Prop(tmpl, "Position");
            if (pos == null) return null;
            float x = (float)ToDouble(R.Prop(pos, "X"), double.NaN);
            float y = (float)ToDouble(R.Prop(pos, "Y"), double.NaN);
            float z = (float)ToDouble(R.Prop(pos, "Z"), double.NaN);
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return null;

            float nx = x, nz = z;
            string locId = null;
            bool ok = false;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                double r = 0.4 + rng.NextDouble() * 0.8;          // [0.4, 1.2]
                double a = rng.NextDouble() * Math.PI * 2.0;
                nx = (float)(x + r * Math.Cos(a));
                nz = (float)(z + r * Math.Sin(a));
                locId = FormatLocationId(nx, y, nz);
                if (usedLocs.Add(locId)) { ok = true; break; }
            }
            if (!ok) return null;                                  // 12 次都撞位置 ⇒ 罕见，跳过
            var vec = MakeVector3(PropType(tmpl, "Position"), nx, y, nz);
            if (vec == null || !R.SetProp(tmpl, "Position", vec)) return null;
            R.SetProp(clone, "LocationId", locId);

            // ③ Root + 每个 Item._id（同 Root 的子件 parentId 一起改，避免悬挂引用）
            var items = R.Prop(tmpl, "Items") as IList;
            string oldRoot = R.Prop(tmpl, "Root")?.ToString();
            string newRoot = null;
            if (items != null && items.Count > 0)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (it == null) continue;
                    string nid = NewHex24(usedIds);
                    R.SetProp(it, "Id", CoerceMongoId(nid, it, "Id"));
                    if (!string.IsNullOrEmpty(oldRoot)
                        && string.Equals(R.Prop(it, "ParentId")?.ToString(), oldRoot, StringComparison.OrdinalIgnoreCase))
                        R.SetProp(it, "ParentId", nid);
                    if (i == 0) newRoot = nid;
                }
            }
            if (newRoot != null) R.SetProp(tmpl, "Root", newRoot);

            return clone;
        }
        catch (Exception ex) { Warn("克隆点位失败（跳过该点）：" + ex.Message); return null; }
    }

    /// <summary>
    /// 把一个点位**收敛成单物品**并替换成 <paramref name="tpl"/>：
    /// `Items` = 1 条新物品（新 `_id`、新 `composedKey`、`_tpl`=目标、`upd.StackObjectsCount`=1），
    /// `ItemDistribution` = 1 条 rel=1 的对应键 ⇒ 局内**必定**落这一件（见类注释 IL 铁证 ③）。
    /// 对象是"本局反序列化出来的新鲜对象"，所以不需要登记还原。
    /// </summary>
    private static bool ReplaceOne(object sp, string tpl, Random rng, string mapKey, out string why)
    {
        why = null;
        try
        {
            var tmpl = R.Prop(sp, "Template");
            if (tmpl == null) { why = "无 Template"; return false; }
            var items = R.Prop(tmpl, "Items") as IList;
            if (items == null || items.Count == 0) { why = "Template.Items 空/非 IList"; return false; }
            var src = items[0];
            if (src == null) { why = "Items[0] 为空"; return false; }

            var itemType = src.GetType();
            var distList = R.Prop(sp, "ItemDistribution") as IList;
            var distType = (distList != null && distList.Count > 0 ? distList[0]?.GetType() : null)
                           ?? GenericArg(PropType(sp, "ItemDistribution"));
            if (distType == null) { why = "拿不到 LooseLootItemDistribution 类型"; return false; }

            var composedKey = rng.Next(100000000, 999999999).ToString(CultureInfo.InvariantCulture);
            int guard = 0;
            while (distList != null && guard++ < 16 && distList.Cast<object>().Any(d => string.Equals(
                       R.Prop(R.Prop(d, "ComposedKey"), "Key")?.ToString(), composedKey, StringComparison.Ordinal)))
                composedKey = rng.Next(100000000, 999999999).ToString(CultureInfo.InvariantCulture);

            // ① 新物品
            var item = Activator.CreateInstance(itemType);
            if (item == null) { why = "构造 " + itemType.Name + " 失败"; return false; }
            if (!R.SetProp(item, "Id", CoerceMongoId(NewHex24(null), item, "Id"))) { why = "设置 _id 失败"; return false; }
            if (!R.SetProp(item, "Template", CoerceMongoId(tpl, item, "Template"))) { why = "设置 _tpl 失败"; return false; }
            R.SetProp(item, "ComposedKey", composedKey);
            var updType = PropType(item, "Upd");
            if (updType != null)
            {
                var upd = Activator.CreateInstance(updType);
                R.SetProp(upd, "StackObjectsCount", 1.0);          // 保持合理：非弹药一律 1
                R.SetProp(item, "Upd", upd);
            }
            var newItems = R.NewListOf(itemType);
            if (newItems is IList ni) ni.Add(item);
            else { why = "造 Items 列表失败"; return false; }

            // ② 新 ItemDistribution
            var entry = Activator.CreateInstance(distType);
            var ckType = PropType(entry, "ComposedKey");
            var ck = ckType != null ? Activator.CreateInstance(ckType) : null;
            if (ck == null || !R.SetProp(ck, "Key", composedKey)) { why = "造 composedKey 失败"; return false; }
            R.SetProp(entry, "ComposedKey", ck);
            R.SetProp(entry, "RelativeProbability", 1.0);
            var newDist = R.NewListOf(distType);
            if (newDist is IList nd) nd.Add(entry);
            else { why = "造 ItemDistribution 列表失败"; return false; }

            // ③ 覆盖（Root 跟着新物品的 _id 走，否则"Root 悬空"）
            if (!R.SetProp(tmpl, "Items", newItems)) { why = "写回 Template.Items 失败"; return false; }
            R.SetProp(sp, "ItemDistribution", newDist);
            R.SetProp(tmpl, "Root", R.Prop(item, "Id")?.ToString());
            // ★ 诊断：容器类物品当 loose loot 有"客户端不显示"的风险（原版武器箱踩过）
            WarnIfContainerTpl(mapKey, tpl);
            return true;
        }
        catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; return false; }
    }

    // ── 诊断：注入的 tpl 是不是容器类物品 ──────────────────────────────────────
    //
    //   `SpawnpointTemplate.IsContainer` 是**刷点自身**的字段（loose loot 全是 false），
    //   不是物品模板的字段；物品"是不是容器"的等价判据 = `_props.Grids` 非空
    //   （原版武器箱 59fb023c86f7746d0d4b423c 的 Grids 有 1 个 5×10 格子）。
    //   若 SPT 模型将来真的暴露 `IsContainer` 布尔，这里也一并认。
    //   ⚠ 只警告、不阻止：用户可能确实想放容器；但"当 loose loot 不显示"的风险要能看见。
    private static void WarnIfContainerTpl(string mapKey, string tpl)
    {
        if (TemplateTable == null || string.IsNullOrEmpty(tpl)) return;
        lock (_gate) { if (!_warnedContainerTpl.Add(tpl)) return; }
        try
        {
            var items = R.Prop(TemplateTable, "Items");
            if (items == null) { lock (_gate) _warnedContainerTpl.Remove(tpl); return; }
            object item = null;
            if (items is IDictionary dict)
            {
                var keyType = dict.Keys.Cast<object>().FirstOrDefault()?.GetType();
                item = dict[R.Coerce(tpl, keyType ?? typeof(string))];
            }
            else
            {
                // 非字典（理论上不会）⇒ 逐个找 Id
                foreach (var o in (IEnumerable)items)
                {
                    var id = R.Prop(o, "Id") ?? R.Prop(o, "_id");
                    if (id != null && string.Equals(id.ToString(), tpl, StringComparison.OrdinalIgnoreCase)) { item = o; break; }
                }
            }
            if (item == null) { lock (_gate) _warnedContainerTpl.Remove(tpl); return; }

            var props = R.Prop(item, "Properties") ?? R.Prop(item, "_props");
            bool isContainer = false;
            object flag = R.Prop(item, "IsContainer") ?? R.Prop(props, "IsContainer");
            if (flag is bool b) isContainer = b;
            var grids = R.Prop(props, "Grids") ?? R.Prop(item, "Grids");
            if (!isContainer && grids is ICollection c && c.Count > 0) isContainer = true;

            if (isContainer)
                Warn($"{mapKey}：注入的 tpl {tpl} 是**容器类物品**（`IsContainer` / `_props.Grids` 非空）⇒ "
                   + "这类物品当 loose loot **可能不显示**，风险较高。⚠ 未证实：vanilla loose loot 里背包 "
                   + "56e335e4d2720b6c058b456d / 文件包 590c60fc86f77412b13fddcf 等带 Grids 的物品本来也能正常落地，"
                   + "所以**不是**「容器一律不显示」；客户端唯一显式分支是刷点 `IsContainer=true` 时按静态容器 Id 匹配，"
                   + "找不到就 `lootId is missing` 跳过（Assembly-CSharp.decompiled.cs:552171/552201）——"
                   + "我们替换后该字段仍是 false。原版武器箱 59fb023c86f7746d0d4b423c 这次看不到的具体原因仍未证实。"
                   + "建议改用非容器物品。");
        }
        catch { }
    }

    // ── regions 小工具 ──────────────────────────────────────────────────────────

    /// <summary>
    /// region 优先级顺序：**maxPoints 小者先**（更精确的区先生效），同 cap 时盒子面积小者先，再按声明顺序。
    /// 没写 maxPoints 的盒子排最后（它不做确定性替换，只给 `valuableBoost`）。
    /// </summary>
    private static List<int> RegionOrder(List<LooseLootRegionOpt> regions)
    {
        var idx = new List<int>();
        if (regions == null || regions.Count == 0) return idx;
        for (int i = 0; i < regions.Count; i++)
            if (regions[i]?.Min != null && regions[i].Max != null) idx.Add(i);
        idx.Sort((a, b) =>
        {
            int ca = RegionCap(regions[a]), cb = RegionCap(regions[b]);
            if (ca != cb) return ca.CompareTo(cb);
            int c = RegionArea(regions[a]).CompareTo(RegionArea(regions[b]));
            return c != 0 ? c : a.CompareTo(b);
        });
        return idx;
    }

    /// <summary>区内确定性替换上限；未设 / ≤0 ⇒ <see cref="int.MaxValue"/>（= 不做确定性替换）。</summary>
    private static int RegionCap(LooseLootRegionOpt rg)
        => rg?.MaxPoints is int m && m > 0 ? m : int.MaxValue;

    private static double RegionArea(LooseLootRegionOpt rg)
    {
        try { return Math.Abs(rg.Max.X - rg.Min.X) * Math.Abs(rg.Max.Z - rg.Min.Z); }
        catch { return double.MaxValue; }
    }

    /// <summary>包围盒命中判定（只用水平面 x/z；y 不参与，避免地形高低差漏判）。</summary>
    private static bool InBox(LooseLootRegionOpt rg, double x, double z)
    {
        if (rg?.Min == null || rg.Max == null) return false;
        double x0 = Math.Min(rg.Min.X, rg.Max.X), x1 = Math.Max(rg.Min.X, rg.Max.X);
        double z0 = Math.Min(rg.Min.Z, rg.Max.Z), z1 = Math.Max(rg.Min.Z, rg.Max.Z);
        return x >= x0 && x <= x1 && z >= z0 && z <= z1;
    }

    /// <summary>到**盒心**（= 圆心）的水平距离 —— 没写锚点时的排序用（近的优先替换）。</summary>
    private static double DistToCenter(LooseLootRegionOpt rg, double x, double z)
    {
        double cx = (rg.Min.X + rg.Max.X) * 0.5, cz = (rg.Min.Z + rg.Max.Z) * 0.5;
        double dx = x - cx, dz = z - cz;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// 排序距离：**有锚点 ⇒ 到锚点的真实 3D 距离**（水平 + 垂直都算，能排掉"同一 x/z 但站在二层/地下"的点）；
    /// 没锚点 ⇒ 老行为（到盒心的水平 2D 距离，森林 13 个区都走这条，行为不变）。
    /// </summary>
    private static double RegionDist(LooseLootRegionOpt rg, double x, double y, double z)
    {
        if (rg?.Anchor != null)
            return Dist3(x, y, z, rg.Anchor.X, rg.Anchor.Y, rg.Anchor.Z);
        return DistToCenter(rg, x, z);
    }

    private static double Dist3(double x1, double y1, double z1, double x2, double y2, double z2)
    {
        double dx = x1 - x2, dy = y1 - y2, dz = z1 - z2;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static string AnchorLabel(LooseLootRegionOpt rg)
        => string.IsNullOrWhiteSpace(rg?.AnchorName) ? "锚点" : rg.AnchorName.Trim();

    /// <summary>每个 region 最多打几行"选区替换点"（用户要求：每个点一行，最多 5 行）。</summary>
    private const int MaxRegionPointLogs = 5;

    /// <summary>
    /// ★ 逐点打实测坐标（**只有写了 anchor 的区才打** —— 森林 13 个区都打会刷屏 65 行/局）。
    /// 逐字格式（用户要求）：`interchange：小黄车 选区替换点 (111.29, 22.54, 286.69) 距车 4.51 米`
    /// </summary>
    private static void LogReplacedPoint(string mapKey, LooseLootRegionOpt rg, int ri, object sp, int ordinal)
    {
        try
        {
            if (rg?.Anchor == null) return;                 // 没锚点 ⇒ 不打（老行为，避免刷屏）
            if (ordinal > MaxRegionPointLogs) return;       // 最多 5 行
            if (!TryPointXYZ(sp, out var x, out var y, out var z)) return;
            double d = Dist3(x, y, z, rg.Anchor.X, rg.Anchor.Y, rg.Anchor.Z);
            Info($"{mapKey}：{RegionName(rg, ri)} 选区替换点 ({x:0.##}, {y:0.##}, {z:0.##})"
               + $" 距{AnchorLabel(rg)} {d:0.##} 米");
        }
        catch { }
    }

    /// <summary>
    /// 把点位 `Probability` 设成 **1.0** ⇒ SPT `GenerateDynamicLoot` 里
    /// `if (spawnpoint.Probability == 1.0) list3.Add(spawnpoint)` ⇒ 不进概率抽取、每局必刷。
    /// 依据与理由见 <see cref="LooseLootRegionOpt.GuaranteedPoints"/>。
    /// 返回是否**确实**写成了 1.0（写失败/被类型吞掉 ⇒ false，调用方计数就不会虚高）。
    /// </summary>
    private static bool SetMustSpawn(object sp)
    {
        try
        {
            if (sp == null) return false;
            if (!R.SetProp(sp, "Probability", 1.0)) return false;
            return Math.Abs(ToDouble(R.Prop(sp, "Probability"), 0) - 1.0) < 1e-9;
        }
        catch { return false; }
    }

    private static string RegionName(LooseLootRegionOpt rg, int idx)
        => string.IsNullOrWhiteSpace(rg?.Name) ? ("#" + (idx + 1)) : rg.Name.Trim();

    /// <summary>点位是不是"可以动"的松散战利品点：排除必刷点（任务/固定）与 christmas 点。</summary>
    private static bool IsReplaceable(object sp)
    {
        try
        {
            var tmpl = R.Prop(sp, "Template");
            if (tmpl == null) return false;
            if (R.Prop(tmpl, "IsAlwaysSpawn") is bool always && always) return false;
            var id = R.Prop(tmpl, "Id")?.ToString();
            if (!string.IsNullOrEmpty(id) && id.StartsWith("christmas", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>取点位的世界 (x,z)；缺 Position 返回 false。</summary>
    private static bool TryPointXZ(object sp, out double x, out double z)
    {
        bool ok = TryPointXYZ(sp, out x, out _, out z);
        return ok;
    }

    /// <summary>取点位的世界 (x,y,z)；缺 Position 返回 false。
    /// ★ y 也要：region 写了 anchor 时按**真实 3D 距离**挑点（能排掉同 x/z 但不同楼层的点）。</summary>
    private static bool TryPointXYZ(object sp, out double x, out double y, out double z)
    {
        x = y = z = double.NaN;
        try
        {
            var pos = R.Prop(R.Prop(sp, "Template"), "Position");
            if (pos == null) return false;
            x = ToDouble(R.Prop(pos, "X"), double.NaN);
            y = ToDouble(R.Prop(pos, "Y"), double.NaN);
            z = ToDouble(R.Prop(pos, "Z"), double.NaN);
            return !double.IsNaN(x) && !double.IsNaN(y) && !double.IsNaN(z);
        }
        catch { return false; }
    }

    /// <summary>把 `{tpl: weight}` 校验成可用池（tpl 必须是 24 位 hex，否则 warning 丢弃——绝不瞎填）。</summary>
    private static List<KeyValuePair<string, int>> BuildPool(LooseLootPoolOpt pool, string mapKey, string kind)
    {
        var res = new List<KeyValuePair<string, int>>();
        if (pool?.Tpls == null || pool.Tpls.Count == 0) return res;
        foreach (var kv in pool.Tpls)
        {
            if (!IsHex24(kv.Key))
            {
                Warn($"{mapKey}/{kind}：tpl '{kv.Key}' 不是 24 位 hex ⇒ 丢弃（请用 tools/check_loose_loot_tpls.py 核对）");
                continue;
            }
            res.Add(new KeyValuePair<string, int>(kv.Key, kv.Value > 0 ? kv.Value : 1));
        }
        return res;
    }

    private static string PickWeighted(List<KeyValuePair<string, int>> pool, Random rng)
    {
        if (pool == null || pool.Count == 0) return null;
        long total = 0;
        foreach (var kv in pool) total += kv.Value;
        if (total <= 0) return pool[0].Key;
        double r = rng.NextDouble() * total;
        foreach (var kv in pool)
        {
            r -= kv.Value;
            if (r < 0) return kv.Key;
        }
        return pool[pool.Count - 1].Key;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  小工具
    // ══════════════════════════════════════════════════════════════════════════

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

    /// <summary>把惰性 `IEnumerable` 物化成 `List&lt;元素类型&gt;` 并写回（返回 IList）。</summary>
    private static IList Materialize(object owner, object enumerable)
    {
        try
        {
            var elem = GenericArg(enumerable.GetType()) ?? R.ElemType(enumerable as IList);
            if (elem == null) elem = (enumerable as IEnumerable)?.Cast<object>().FirstOrDefault()?.GetType();
            if (elem == null) return null;
            var made = R.NewListOf(elem) as IList;
            if (made == null) return null;
            foreach (var o in (IEnumerable)enumerable) made.Add(o);
            return R.SetProp(owner, "Spawnpoints", made) ? made : null;
        }
        catch { return null; }
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

    private static object CoerceMongoId(string hex, object owner, string propName)
    {
        try
        {
            var t = PropType(owner, propName);
            var u = t == null ? null : (Nullable.GetUnderlyingType(t) ?? t);
            return R.Coerce(hex, u ?? typeof(string));
        }
        catch { return hex; }
    }

    private static object MakeVector3(Type vecType, float x, float y, float z)
    {
        try
        {
            var u = vecType == null ? null : (Nullable.GetUnderlyingType(vecType) ?? vecType);
            if (u == null) return null;
            var ctor = u.GetConstructor(new[] { typeof(float), typeof(float), typeof(float) });
            if (ctor != null) return ctor.Invoke(new object[] { x, y, z });
            ctor = u.GetConstructor(new[] { typeof(double), typeof(double), typeof(double) });
            if (ctor != null) return ctor.Invoke(new object[] { (double)x, (double)y, (double)z });
            var boxed = Activator.CreateInstance(u);
            bool ok = R.SetProp(boxed, "X", x);
            ok &= R.SetProp(boxed, "Y", y);
            ok &= R.SetProp(boxed, "Z", z);
            return ok ? boxed : null;
        }
        catch { return null; }
    }

    /// <summary>与本体 JSON 同形态的坐标键：`"(x, y, z)"`（float 最短往返表示，invariant）。</summary>
    private static string FormatLocationId(float x, float y, float z)
        => "(" + x.ToString(CultureInfo.InvariantCulture) + ", "
               + y.ToString(CultureInfo.InvariantCulture) + ", "
               + z.ToString(CultureInfo.InvariantCulture) + ")";

    private static string NewHex24(HashSet<string> used)
    {
        for (int i = 0; i < 16; i++)
        {
            var h = Guid.NewGuid().ToString("N");
            if (h.Length >= 24) h = h.Substring(0, 24);
            if (used == null || used.Add(h)) return h;
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

    private static string StripTrailingBracket(string id)
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

    private static int SeedFor(string s)
    {
        unchecked
        {
            int h = 17;
            if (!string.IsNullOrEmpty(s)) foreach (var c in s) h = h * 31 + c;
            return h;
        }
    }

    private static List<int> ShuffledIndices(int n, Random rng)
    {
        var a = new List<int>(n);
        for (int i = 0; i < n; i++) a.Add(i);
        for (int i = n - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
        return a;
    }

    private static double ToDouble(object v, double fallback)
    {
        try { return v == null ? fallback : Convert.ToDouble(v, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }
}
