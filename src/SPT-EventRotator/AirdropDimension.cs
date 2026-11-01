using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;

using System.Reflection;
namespace EventRotator;

/// <summary>
/// ★ 空投维度（v2.1.6 新增）—— 解决"活动物品根本刷不出来"的问题。
///
/// 背景（实证）：SPT 的 airdrop.json 里
///   airdropTypeWeightings = { mixed:500, weaponArmor:400, foodMedical:100, barter:100, radar:0, toiletPaper:1 }
///   loot.radar.useForcedLoot = true
///   loot.radar.forcedLoot   = { "66d9f7256916142b3b02276e": {min:2,max:4} }   ← 雷达站备用零件
///   customAirdropMapping    = { "66da1b49…":"radar", "66da1b54…":"radar" }
///
///   ⇒ radar 权重 = 0 ⇒ **永不自然出现**，只能靠任务 Building Foundations / Key Partner 给的
///     "Airdrop technical supply crate" 召唤。
///   ⇒ 而 GARY ZONT 电子战装置（66d9f8744827a77e870ecaf1）**不在任何空投、不在任何战利品表、
///     不是任何任务的奖励** ⇒ 完全拿不到 ⇒ 任务 Ours by Right / Before the Rain 做不完。
///
/// 本维度让活动可以：① 打开某类空投的权重 ② 往某类空投里塞物品（含新物品）。
/// 全部可还原（Restore() 时恢复原值）。
///
/// config 用法（events.json）：
/// <code>
/// "airdropWeight": { "radar": 300 },
/// "airdropAddLoot": { "radar": { "66d9f8744827a77e870ecaf1": [1, 2] } }
/// </code>
/// </summary>
internal static class AirdropDimension
{
    public static object Config;                 // AirdropConfig 实例（反射访问，避免硬依赖）
    /// <summary>★ v0.1.3：LocationTable（用来改每张图的 AirdropParameters：频率/人数阈值）。</summary>
    public static object LocationTable;
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][空投] " + m); } catch { } }

    /// <summary>★ v0.1.3：只调一次，把 DI 单例接进来（之前空投维度根本没接上，整条链路是死的）。</summary>
    public static void Init(object airdropConfig, object locationTable, Action<string> log, Action<Action> addRestore)
    {
        if (airdropConfig != null) Config = airdropConfig;
        if (locationTable != null) LocationTable = locationTable;
        if (log != null) Log = log;
        if (addRestore != null) AddRestore = addRestore;
        if (Config == null) Info("诊断：AirdropConfig 注入为空（DI 没给到）");
    }

    /// <summary>
    /// 在 LocationTable 里找某张图的 Location。
    /// ★ v0.1.3 修正：原来只用 <c>locationDictionaryCache</c> / <c>locationMappings</c> 两个**不存在**的名字
    ///   （真实成员是 private <c>_locationDictionaryCache</c> 与 private <c>_locationMappings</c>，都有前导下划线）
    ///   ⇒ <c>FindLocation</c> 恒返回 null ⇒ **空投频率维度一直静默失效**。
    ///   现在与 <see cref="LabsFleaDimension"/> 一致，优先走官方 <c>LocationTable.GetLocation(string)</c>。
    /// </summary>
    private static object FindLocation(string mapKey)
    {
        // ① 官方 API（最稳）
        try
        {
            var m = LocationTable?.GetType().GetMethod("GetLocation", new[] { typeof(string) });
            if (m != null)
            {
                var loc = m.Invoke(LocationTable, new object[] { mapKey });
                if (loc != null) return loc;
            }
        }
        catch (Exception ex) { Info("GetLocation 调用失败: " + ex.Message); }

        // ② 兜底：内部缓存字典（含真实的下划线名字）
        try
        {
            object cache = Prop(LocationTable, "_locationDictionaryCache") ?? Prop(LocationTable, "locationDictionaryCache");
            if (cache is IDictionary d)
            {
                foreach (var k in d.Keys)
                    if (string.Equals(k?.ToString(), mapKey, StringComparison.OrdinalIgnoreCase)) return d[k];
            }
        }
        catch (Exception ex) { Info("找地图失败(" + mapKey + "): " + ex.Message); }
        return null;
    }

    /// <summary>
    /// 反射读一个成员。
    /// ★ P0/P1 修正：原来只用<b>大小写敏感</b>的 <c>GetProperty(n)</c> ⇒ 一旦名字与真实 CLR 名不符就
    ///   **静默返回 null**（本项目通用坑型：JSON 名 ≠ CLR 属性名）。
    ///   实证踩坑：<c>AirdropParameter</c> 的 JSON 名是 <c>MinPlayersCountToSpawnAirdrop</c>，
    ///   而真实 CLR 属性名是 <c>MinimumPlayersCountToSpawnAirdrop</c>（多一个 "mum"）
    ///   ⇒ 本文件第 ③ 段"压低人数门槛"恒 null、恒 no-op（日志里 6 条全是「0 张图压低人数门槛」）。
    ///   现在：精确 → 大小写不敏感 → 精确字段 → 大小写不敏感字段，四段兜底。
    /// </summary>
    /// <summary>★ v1.0.1【性能·第六轮】元数据查找改走 R.GP / R.GF 缓存；控制流与异常语义逐字保留。</summary>
    private static object Prop(object o, string n)
    {
        if (o == null) return null;
        var t = o.GetType();
        var p = R.GP(t, n, BindingFlags.Public | BindingFlags.Instance) ?? R.GP(t, n, AnyInstance);
        if (p != null) { try { return p.GetValue(o); } catch { } }
        var f = R.GF(t, n, BindingFlags.Public | BindingFlags.Instance) ?? R.GF(t, n, AnyInstance);
        if (f != null) { try { return f.GetValue(o); } catch { } }
        return null;
    }

    /// <summary>大小写不敏感的实例成员查找（P0/P1 新增，修掉上面的静默 null 坑型）。</summary>
    private const System.Reflection.BindingFlags AnyInstance =
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase;

    /// <summary>
    /// ★ v0.1.3 空投频率（空投潮）：改每张图的 AirdropParameters。
    ///   关键实证：本体 <c>MinPlayersCountToSpawnAirdrop = 6</c>，单人局**永远不会**触发飞机空投，
    ///   所以不把这个压到 1，"空投潮"在单机 SPT 上等于没做。
    /// </summary>
    /// <param name="onlyMaps">
    /// ★ v0.1.5 新增：只对这几张图生效（官方 Lighthouse Airdrops 只提升灯塔）。
    /// 传 null/空 = 用默认户外图列表。
    /// </param>
    public static void ApplyFrequency(double mult, ISptLogger<EventRotatorServer> logger,
                                      IEnumerable<string> onlyMaps = null)
    {
        if (mult <= 0 || Math.Abs(mult - 1.0) < 0.000001) return;
        if (LocationTable == null) { logger?.Warning("[EventRotator][空投] 拿不到 LocationTable，空投频率未改"); return; }

        // ★ v0.1.5：默认列表去掉 labyrinth —— 它没有 AirdropParameters（改了也无效），
        //   且官方 2026-03-19 的空投活动明确把迷宫排除在外。
        var def = new[] { "bigmap", "interchange", "lighthouse", "rezervbase", "sandbox",
                          "sandbox_high", "shoreline", "tarkovstreets", "woods" };
        var only = onlyMaps?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        var maps = (only != null && only.Length > 0) ? only : def;
        if (only != null && only.Length > 0)
            logger?.Info($"[EventRotator][空投] 频率 ×{mult} **限图**生效：{string.Join(",", maps)}（官方 Lighthouse Airdrops 只提升灯塔）");
        int changed = 0, single = 0, alreadyOk = 0, missing = 0;
        foreach (var mk in maps)
        {
            var loc = FindLocation(mk);
            if (loc == null) continue;
            var baseObj = Prop(loc, "Base") ?? Prop(loc, "base");
            if (baseObj == null) continue;
            var apObj = Prop(baseObj, "AirdropParameters") ?? Prop(baseObj, "airdropParameters");

            // JSON 里 AirdropParameters 是**数组**（[ {...} ]），属性名是 AirdropParameters（list）
            IEnumerable items = apObj as IEnumerable;
            if (apObj is string || apObj == null) continue;
            foreach (var one in items)
            {
                if (one == null) continue;

                // ① 概率
                var chance = Prop(one, "PlaneAirdropChance");
                if (chance != null && SetPropIfWritable(one, "PlaneAirdropChance",
                        Math.Min(1.0, Convert.ToDouble(chance) * mult)))
                    changed++;

                // ② 单局最多几架
                var maxN = Prop(one, "PlaneAirdropMax");
                if (maxN != null && SetPropIfWritable(one, "PlaneAirdropMax",
                        (int)Math.Min(20, Math.Round(Convert.ToDouble(maxN) * mult))))
                    changed++;

                // ③ ★ 单人局也能来（本体要求 6 人）
                // ★ P0/P1 修正：真名是 MinimumPlayersCountToSpawnAirdrop（JSON 名 MinPlayersCount…）。
                //   改名前这里恒 null ⇒ single 恒 0 ⇒ 日志永远写「0 张图压低人数门槛」，
                //   看日志的人会以为空投没生效（实际 SPT 本体在启动阶段已压到 1，见下方日志）。
                var minP = Prop(one, "MinimumPlayersCountToSpawnAirdrop");
                if (minP != null && Convert.ToDouble(minP) > 1)
                {
                    if (SetPropIfWritable(one, "MinimumPlayersCountToSpawnAirdrop", 1)) single++;
                }
                else if (minP != null) alreadyOk++;
                else missing++;   // 属性读不到（SPT 又改名了）→ 必须可见，不许再静默
            }
        }
        // ★ P0/P1：日志必须如实反映"我们到底改了什么"，否则每次看都像空投没生效。
        //   背景实证（反编译）：SPT 本体自己的 `PostDbLoadService.SetLocationAirdropMinimumPlayers()`
        //   在启动阶段（GameCallbacks 200000，早于本 mod）已把所有 `> 1` 的人数门槛改成 1
        //   ⇒ 我们**正常情况下读到就是 1**，那是"SPT 已经做好了"，不是"我们没生效"。
        var gate = single > 0
            ? $"已压低 {single} 处人数门槛 → 1（本体默认要求 6 人，单人局才会来飞机）"
            : "人数门槛无需处理（SPT 本体已在启动阶段统一压到 1，单人局本来就会来飞机）";
        if (missing > 0)
            gate += $"；⚠ {missing} 处读不到 MinimumPlayersCountToSpawnAirdrop（SPT 可能又改名了，该项未生效）";
        Info($"★空投潮：{changed} 项概率/架次 ×{mult}，{gate}（可还原）");
        if (alreadyOk > 0 && single == 0 && missing == 0)
            Info($"  自证：{alreadyOk} 处 AirdropParameters 的 MinimumPlayersCountToSpawnAirdrop 已 = 1（读取成功，非 no-op）");
    }

    /// <summary>
    /// ★★★ v2.6.42【常驻 · 用户裁决「常驻b」】**单局空投上限 ×3 + 概率下限**（**无活动时也生效**）。
    ///
    /// 与 <see cref="ApplyFrequency"/> 的关系（**必须能叠加、且不能互相弄坏**）：
    ///   · 本方法改的是**基线**（`PlaneAirdropMax ×3`、`PlaneAirdropChance = max(现值, 概率下限)`），
    ///     **一次性、幂等、不登记还原**（常驻）⇒ 调用点必须**早于**任何活动的 <see cref="ApplyFrequency"/>；
    ///   · 活动的 `ApplyFrequency(mult)` 会在这条基线上**再乘一次**（它自己登记还原 ⇒ 活动结束时回到基线值）✔
    ///   · ⇒ 关键不变量：**每次套用先 <see cref="AirdropBaseline.Ensure"/>，再进活动循环**（`Plugin.cs` 的调用点保证）
    ///
    /// ⚠ 反编译实证：`LocationTable` 在启动早期可能是 `null` ⇒ **返回 false 表示"这次没生效"**，
    ///   调用方要**留待下次重试**并打明确的未生效警告（**不许静默放弃**）。
    /// </summary>
    public static bool ApplyBaseline(double maxMultiplier, double chanceFloor, int maxCap,
                                     ISptLogger<EventRotatorServer> logger, out string detail)
    {
        detail = null;
        if (LocationTable == null) return false;                       // ★ 未就绪 ⇒ 交给调用方重试
        var maps = DefaultAirdropMaps;
        var lines = new List<string>();
        int maxChanged = 0, chanceChanged = 0, zeroKept = 0, ready = 0;
        foreach (var mk in maps)
        {
            var loc = FindLocation(mk);
            if (loc == null) continue;
            var baseObj = Prop(loc, "Base") ?? Prop(loc, "base");
            if (baseObj == null) continue;
            var apObj = Prop(baseObj, "AirdropParameters") ?? Prop(baseObj, "airdropParameters");
            if (apObj == null || apObj is string) continue;
            foreach (var one in (IEnumerable)apObj)
            {
                if (one == null) continue;
                ready++;
                // ① 单局上限 ×3（0 保持 0；与 ApplyFrequency 同款上限 20）
                var maxProp = Prop(one, "PlaneAirdropMax");
                if (maxProp != null)
                {
                    double cur = Convert.ToDouble(maxProp);
                    int target = (int)Math.Min(maxCap, Math.Round(cur * maxMultiplier));   // ★v2.6.69 封顶由参数给（用户裁决 = 6）
                    if (Math.Abs(cur - target) > 0.000001)
                    {
                        if (SetPropNoRestore(one, "PlaneAirdropMax", target)) { maxChanged++; lines.Add($"{mk} 上限 {cur:0}→{target}"); }
                    }
                    else if (cur == 0) zeroKept++;
                }
                // ② 概率下限（**只升不降**）
                var chProp = Prop(one, "PlaneAirdropChance");
                if (chProp != null)
                {
                    double cur = Convert.ToDouble(chProp);
                    double target = Math.Min(1.0, Math.Max(cur, chanceFloor));
                    if (target - cur > 0.000001)
                    {
                        if (SetPropNoRestore(one, "PlaneAirdropChance", target)) { chanceChanged++; lines.Add($"{mk} 概率 {cur:0.###}→{target:0.###}"); }
                    }
                }
            }
        }
        detail = $"{maps.Length} 张图 / {ready} 处 AirdropParameters：上限改了 {maxChanged} 处、概率抬了 {chanceChanged} 处"
               + (zeroKept > 0 ? $"、{zeroKept} 处本来就是 0（保持 0）" : "");
        if (lines.Count > 0) Info("[空投·常驻] " + string.Join("；", lines));
        return ready > 0;
    }

    /// <summary>改一个属性，**不登记还原**（常驻基线用；与 <see cref="SetPropIfWritable"/> 同款大小写兜底）。</summary>
    private static bool SetPropNoRestore(object obj, string name, object value)
    {
        try
        {
            var t = obj.GetType();
            var p = t.GetProperty(name) ?? t.GetProperty(name, AnyInstance);
            if (p != null && p.CanWrite) { p.SetValue(obj, Coerce(value, p.PropertyType)); return true; }
            var f = t.GetField(name) ?? t.GetField(name, AnyInstance);
            if (f != null) { f.SetValue(obj, Coerce(value, f.FieldType)); return true; }
        }
        catch { }
        return false;
    }

    /// <summary>常驻/活动共同使用的"空投地图清单"（v0.1.5 起去掉 labyrinth：它没有 AirdropParameters）。</summary>
    private static readonly string[] DefaultAirdropMaps =
    {
        "bigmap", "interchange", "lighthouse", "rezervbase", "sandbox",
        "sandbox_high", "shoreline", "tarkovstreets", "woods"
    };

    /// <summary>改一个属性，并登记还原闭包（★ P0/P1：同样走大小写不敏感兜底，否则 Prop 读到了却写不进去）。</summary>
    private static bool SetPropIfWritable(object obj, string name, object value)
    {
        try
        {
            var t = obj.GetType();
            // ① 属性（精确 → 大小写不敏感）
            var p = t.GetProperty(name) ?? t.GetProperty(name, AnyInstance);
            object old = null; bool ok = false;
            if (p != null && p.CanWrite)
            {
                old = p.GetValue(obj);
                var capO = obj; var capP = p; var capOld = old;
                AddRestore?.Invoke(() => { try { capP.SetValue(capO, capOld); } catch { } });
                p.SetValue(obj, Coerce(value, p.PropertyType));
                ok = true;
            }
            else
            {
                // ② 字段（精确 → 大小写不敏感）
                var f = t.GetField(name) ?? t.GetField(name, AnyInstance);
                if (f != null)
                {
                    old = f.GetValue(obj);
                    var capO = obj; var capF = f; var capOld = old;
                    AddRestore?.Invoke(() => { try { capF.SetValue(capO, capOld); } catch { } });
                    f.SetValue(obj, Coerce(value, f.FieldType));
                    ok = true;
                }
            }
            if (!ok) Info($"'{name}' 不可写，跳过");
            return ok;
        }
        catch (Exception ex) { Info($"改 '{name}' 失败: {ex.Message}"); return false; }
    }

    private static object Coerce(object v, Type t)
    {
        try
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            if (u == typeof(double)) return Convert.ToDouble(v);
            if (u == typeof(float)) return Convert.ToSingle(v);
            if (u == typeof(int)) return Convert.ToInt32(v);
            if (u == typeof(long)) return Convert.ToInt64(v);
            return Convert.ChangeType(v, u);
        }
        catch { return v; }
    }

    /// <summary>把某个字符串名解析成字典里真实存在的枚举键。</summary>
    private static object FindKey(IDictionary d, string name)
    {
        foreach (var k in d.Keys)
            if (string.Equals(k?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                return k;
        return null;
    }

    /// <summary>用 JSON 构造 MinMax&lt;int&gt;（避开构造签名差异）。</summary>
    private static object MakeMinMax(Type minMaxType, int min, int max)
    {
        try
        {
            var json = "{\"min\":" + min + ",\"max\":" + max + "}";
            return JsonSerializer.Deserialize(json, minMaxType, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex) { Info($"构造 MinMax 失败({min},{max}): {ex.Message}"); return null; }
    }

    /// <summary>应用一个活动的空投配置。</summary>
    public static void Apply(EventDef ev, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            if (Config == null) { logger?.Warning("[EventRotator][空投] 拿不到 AirdropConfig"); return; }
            var t = Config.GetType();

            // ── ⓪ ★ v0.1.3 强制空投类型（把目标拉满、其它压到 0.01，只用权重表达偏好）──
            if (!string.IsNullOrWhiteSpace(ev.AirdropForceType))
            {
                var wp = t.GetProperty("AirdropTypeWeightings")?.GetValue(Config) as IDictionary;
                if (wp != null)
                {
                    var target = FindKey(wp, ev.AirdropForceType.Trim());
                    if (target == null) logger?.Warning($"[EventRotator][空投] 没有空投类型 '{ev.AirdropForceType}'");
                    else
                    {
                        foreach (var k in new List<object>(wp.Keys.Cast<object>()))
                        {
                            double oldV = 0; try { oldV = Convert.ToDouble(wp[k]); } catch { }
                            double newV = string.Equals(k?.ToString(), target?.ToString(), StringComparison.OrdinalIgnoreCase) ? 10000 : 0.01;
                            if (Math.Abs(oldV - newV) < 0.001) continue;
                            var capK = k;
                            AddRestore?.Invoke(() => { try { wp[capK] = oldV; } catch { } });
                            wp[k] = newV;
                        }
                        Info($"★强制空投类型 = {ev.AirdropForceType}（该类型权重 → 10000，其它 → 0.01，可还原）");
                    }
                }
            }

            // ── ① 权重 airdropWeight: { "radar": 300 } ──
            var weightsProp = t.GetProperty("AirdropTypeWeightings");
            var weights = weightsProp?.GetValue(Config) as IDictionary;
            if (weights == null) logger?.Warning("[EventRotator][空投] AirdropTypeWeightings 读取失败");

            if (weights != null && ev.AirdropWeight != null && ev.AirdropWeight.Count > 0)
            {
                foreach (var kv in ev.AirdropWeight)
                {
                    var key = FindKey(weights, kv.Key);
                    if (key == null) { logger?.Warning($"[EventRotator][空投] 没有空投类型 '{kv.Key}'"); continue; }
                    double old;
                    try { old = Convert.ToDouble(weights[key]); } catch { continue; }
                    if (Math.Abs(old - kv.Value) < 0.001) continue;
                    var capKey = key;
                    AddRestore?.Invoke(() => { try { weights[capKey] = old; } catch { } });
                    weights[key] = (double)kv.Value;
                    Info($"权重 '{kv.Key}': {old} → {kv.Value}（可还原）");
                }
            }

            // ── ② 塞物品 airdropAddLoot: { "radar": { "<itemId>": [min,max] } } ──
            var lootProp = t.GetProperty("Loot");
            var loot = lootProp?.GetValue(Config) as IDictionary;
            if (loot != null && ev.AirdropAddLoot != null && ev.AirdropAddLoot.Count > 0)
            {
                foreach (var kv in ev.AirdropAddLoot)
                {
                    var lootKey = FindKey(loot, kv.Key);
                    if (lootKey == null) { logger?.Warning($"[EventRotator][空投] 没有空投类型 '{kv.Key}'（塞物品跳过）"); continue; }
                    var lootCfg = loot[lootKey];
                    if (lootCfg == null) continue;

                    // ★★★ v2.6.41【P2 · 用户批准「做个补丁」】**分两条路走**：
                    //   · **原生 forced 类型**（airdrop.json 里 `useForcedLoot` 本来就是 true：radar / toiletPaper）
                    //     ⇒ **保持原逻辑**（往 `ForcedLoot` 里塞）—— 雷达空投"2~4 个雷达站备用零件"的实战效果不能回归 ✔
                    //   · **非 forced 类型**（mixed / foodMedical …）⇒ **不再强制 `useForcedLoot`**（那会把随机内容整箱替换掉），
                    //     改为登记到 `AirdropAppendPatch`，由它在**官方生成完之后追加**物品 ⇒ **随机池保留 + 我们的物品照加** ✔
                    //   ⚠ 旧行为实证：`airdrops_2026` 的 foodMedical 从"25~45 件随机"变成"只有 3 件"、
                    //     `tarkov_mystery` 的 mixed 从"15~35 件随机 + 3~5 武器预设"变成"只有加密闪存卡 1~2 件" ✗
                    var uflProp = lootCfg.GetType().GetProperty("UseForcedLoot");
                    var curUfl = uflProp?.CanWrite == true ? uflProp.GetValue(lootCfg) : null;
                    bool nativelyForced = curUfl is bool ub && ub;
                    bool forceAndAppend = nativelyForced;     // 原生 forced ⇒ 走老路（写 ForcedLoot）

                    if (!forceAndAppend)
                    {
                        // 非 forced：**不改 useForcedLoot**，登记"追加"（可还原：注销）
                        foreach (var item in kv.Value)
                        {
                            var itemId = item.Key;
                            var mm = item.Value ?? new List<int> { 1, 2 };
                            int mn = mm.Count > 0 ? mm[0] : 1;
                            int mx = mm.Count > 1 ? mm[1] : mn;
                            var capType = kv.Key; var capTpl = itemId;
                            var srcTag = AirdropAppendPatch.Register(kv.Key, itemId, mn, mx);
                            AddRestore?.Invoke(() => { try { AirdropAppendPatch.Unregister(capType, capTpl, srcTag); } catch { } });
                            Info($"'{kv.Key}' 空投★追加物品 {itemId} ×{mn}~{mx}"
                               + "（随机内容保留，生成后追加；**不再强制 useForcedLoot**）");
                        }
                        continue;                              // ★ 本类型到此为止，不碰 ForcedLoot
                    }

                    Info($"'{kv.Key}' 是**原生强制掉落**类型（useForcedLoot=true）⇒ 沿用官方 forced 通道（雷达零件等不受影响）");
                    var flProp = lootCfg.GetType().GetProperty("ForcedLoot");
                    var fl = flProp?.GetValue(lootCfg) as IDictionary;
                    if (fl == null)
                    {
                        // 原来是 null → 造一个
                        var flType = flProp?.PropertyType;
                        if (flType == null) { logger?.Warning("[EventRotator][空投] ForcedLoot 类型未知"); continue; }
                        var created = Activator.CreateInstance(flType) as IDictionary;
                        if (created == null) continue;
                        var capLoot = lootCfg;
                        AddRestore?.Invoke(() => { try { flProp.SetValue(capLoot, null); } catch { } });
                        flProp.SetValue(lootCfg, created);
                        fl = created;
                    }
                    var keyType = fl.GetType().GetGenericArguments()[0];      // MongoId
                    var valType = fl.GetType().GetGenericArguments()[1];      // MinMax<int>

                    foreach (var item in kv.Value)
                    {
                        var itemId = item.Key;
                        var mm = item.Value ?? new List<int> { 1, 2 };
                        int mn = mm.Count > 0 ? mm[0] : 1;
                        int mx = mm.Count > 1 ? mm[1] : mn;

                        object mid;
                        try { mid = Convert.ChangeType(itemId, keyType); }
                        catch { try { mid = Activator.CreateInstance(keyType, itemId); } catch { Info($"MongoId 构造失败 {itemId}"); continue; } }

                        if (fl.Contains(mid)) { Info($"'{kv.Key}' 里已有 {itemId}，跳过"); continue; }
                        var mmVal = MakeMinMax(valType, mn, mx);
                        if (mmVal == null) continue;
                        var capFl = fl; var capMid = mid;
                        AddRestore?.Invoke(() => { try { if (capFl.Contains(capMid)) capFl.Remove(capMid); } catch { } });
                        fl[mid] = mmVal;
                        Info($"'{kv.Key}' 空投新增物品 {itemId} ×{mn}~{mx}（可还原）");
                    }
                }
            }
        }
        catch (Exception ex) { logger?.Error($"[EventRotator][空投] 应用失败: {ex.Message}"); }
    }
}
