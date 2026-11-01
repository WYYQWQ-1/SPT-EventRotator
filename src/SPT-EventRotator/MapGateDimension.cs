using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Common;

namespace EventRotator;

/// <summary>
/// ★★★ v2.6.37【**只在该地图生效**】—— 按图门控（用户裁决：`june2022_toxin` 只在海关 `bigmap`）。
///
/// <para><b>门控哪两块</b>（其余维度不碰）：</para>
/// <list type="number">
/// <item><b>空投追加</b>（`airdropAppendLoot`）—— 在空投箱**生成那一刻**判当前地图。</item>
/// <item><b>bot 携带物品</b>（`botLootAdd_`）—— 在 **`PrepareAndGenerateBot` 之前**按图决定
///   那几条池子条目**在不在**。</item>
/// </list>
///
/// <para><b>为什么 bot 那侧不用"事后往这一只 bot 里塞物品"</b>：
/// SPT 原生往 bot 背包塞战利品要走
/// <c>BotGeneratorHelper.cs:418-451</c> → <c>InventoryHelper.GetItemSize(...)</c>
/// → <c>BotInventoryContainerService.TryAddItemToBotContainer(...)</c>（那才负责算格子、写
/// <c>ParentId</c>/<c>SlotId</c>/<c>Location.X/Y/R</c>）。
/// 我们自己手工拼一个 <c>Item</c> 塞进 <c>Inventory.Items</c> 极易把物品链路拼错（客户端会显示成坏物品）。
/// <b>⇒ 改成"按图开关池子条目"，其余交给 SPT 自己的战利品生成器</b> ——
/// **玩家可见结果完全一致**（海关有解药、别图没有），但**不碰物品构造**，风险低得多。</para>
///
/// <para><b>为什么可以放在 `PrepareAndGenerateBot` 的 prefix</b>：
/// 该方法是 <c>BotGenerator.cs:111</c> <c>PrepareAndGenerateBot(MongoId, BotGenerationDetails)</c>，
/// 它**先于**读战利品池（内部再调 `GenerateBot` → `GenerateAndAddEquipmentToBot`）。
/// 而 <c>BotGenerationDetails.Location</c>（`BotController.cs:271` 由 <c>raidSettings?.Location</c> 填）
/// **每一只 bot 都带着地图** ⇒ 在 prefix 里把池子调成"该图该有的样子"即可。
/// 这个开关是**地图的纯函数** ⇒ 幂等、并发安全（同一局的每只 bot 都把它设成同一状态）。</para>
///
/// <para><b>可降级</b>：任何异常只 warning，**绝不影响 bot 生成**；拿不到地图 ⇒
/// **照旧生效 + warning**（宁可解药出现在别图，也不能让"8 分钟必死"变成无解）。</para>
/// </summary>
internal static class MapGateDimension
{
    public static ISptLogger<EventRotatorServer> Log;
    /// <summary>`ProfileActivityService`（Plugin 注入）。</summary>
    public static object ProfileActivityServiceRef;

    /// <summary>本活动的"只在该图"过滤（小写）。空 = 不过滤（原行为）。</summary>
    private static string _onlyOnMap;

    /// <summary>空投那侧：`LocationCallbacks.GetAirdropLoot(url, info, sessionID)` 的 prefix 把 sessionId 存下来。</summary>
    private static string _airdropSessionId;

    /// <summary>只提示一次的键集合（防刷屏）。</summary>
    private static readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    // ── bot 池子的"按图开关"登记 ─────────────────────────────────────────────
    /// <summary>一条池子条目 = 池对象 + 键 + 想写的值 + 旧值快照 + **它属于哪个地图的过滤**。
    /// ★ v2.6.38：`Map` 是**逐条**的（不是全局一个）—— 否则"一个带 `onlyOnMap` 的活动"会把
    ///   别的活动的条目也一起改道（实测回归：`hustle`/`instant_hideout_build` 的啤酒被误伤）。</summary>
    private sealed class PoolEntry
    {
        public IDictionary Pool;
        public object Key;
        public object Want;
        public bool HadOld;
        public object Old;
        /// <summary>本条目的地图过滤（非空）。只有本活动自己声明了 `onlyOnMap` 才会有这个值。</summary>
        public string Map;
    }
    private static readonly List<PoolEntry> _poolEntries = new();
    /// <summary>当前池子是按哪个状态写好的（幂等用）。null = 未写；"*" = 全放（兜底）；否则 = 按此图。</summary>
    private static string _appliedFor;

    /// <summary>只提示一次的封装。</summary>
    private static void WarnOnce(string key, string msg)
    {
        try { if (_warned.Add(key)) Log?.Warning(msg); } catch { }
    }
    private static void Info(string m) { try { Log?.Info(m); } catch { } }

    // ─────────────────────────── 登记（由 Plugin 在套用时调用）───────────────────────────

    /// <summary>
    /// 设置本轮的"只在该图"过滤。<paramref name="mapId"/> 空 ⇒ **什么都不做**。
    ///
    /// <para><b>⚠ 为什么空值不清除</b>：本方法是**按活动**调用的（`Plugin` 套用循环里每个活动一次）。
    /// 若空值就清，那么"`june2022_toxin`（声明了 bigmap）+ 另一个没声明的活动"同时生效时，
    /// **后者的空值会把前者的过滤清掉** ⇒ 解药又变成全图生效（静默失效）。
    /// ⇒ 清除**只**发生在 <see cref="Reset"/>（每个套用周期一次）。</para>
    ///
    /// <para>两个活动声明了**不同**地图 ⇒ 保留先到的 + warning（该字段设计上只服务单个活动）。</para>
    /// </summary>
    public static void SetOnlyOnMap(string mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId)) return;
        var m = mapId.Trim();
        if (_onlyOnMap != null)
        {
            if (!string.Equals(_onlyOnMap, m, StringComparison.OrdinalIgnoreCase))
                WarnOnce("conflict",
                    $"[EventRotator][按图] ⚠两个活动声明了不同的 `onlyOnMap`（'{_onlyOnMap}' vs '{m}'）"
                  + $"⇒ 采用先到的 '{_onlyOnMap}'，忽略 '{m}'。（该字段设计上只服务单个活动）");
            return;
        }
        _onlyOnMap = m;
        Info($"[按图] ★已登记「只在该地图生效」：'{_onlyOnMap}'"
           + "（门控 `airdropAppendLoot` 与 `botLootAdd_` 两处；`globals` 是真全局、不受影响）");
    }

    /// <summary>本轮是否有**任何**活动声明了按图过滤。⚠ 仅供日志/统计 ——
    /// **路由判据一律用"本活动自己的 `OnlyOnMap`"**，绝不能用这个全局值（v2.6.38 回归的根因）。</summary>
    public static bool HasFilter => !string.IsNullOrEmpty(_onlyOnMap);

    /// <summary>
    /// 把一条 bot 池子条目交给按图开关（**由 BotEquipmentDimension 在**本活动**声明了 `onlyOnMap` 时调用**）。
    /// <paramref name="map"/> 为空 ⇒ **不登记**并返回 false（调用方应走"当场写池"的原路）——
    /// 这是防"目标图为 null ⇒ 条目被永久移出池子"的最后一道闸。
    /// </summary>
    public static bool RegisterPoolEntry(IDictionary pool, object key, object want, string map)
    {
        if (pool == null || key == null) return false;
        if (string.IsNullOrWhiteSpace(map))
        {
            // ★ 绝不能带着"没有地图"登记：那样 ApplyForMap 永远判不命中 ⇒ 条目被永久移出池子 ✗
            WarnOnce("reg-nomap",
                "[EventRotator][按图] ⚠`RegisterPoolEntry` 收到空地图 ⇒ **不登记**（改为按原路当场写池）。"
              + "带着空地图登记会让该条目在所有地图上都被移出池子。");
            return false;
        }
        try
        {
            bool had = pool.Contains(key);
            object old = had ? pool[key] : null;
            _poolEntries.Add(new PoolEntry
            { Pool = pool, Key = key, Want = want, HadOld = had, Old = old, Map = map.Trim() });
            _appliedFor = null;                  // 有新登记 ⇒ 让下一只 bot 重新按图写
            return true;
        }
        catch (Exception ex) { WarnOnce("reg", "[EventRotator][按图] 登记池子条目失败（已跳过）: " + ex.Message); }
        return false;
    }

    /// <summary>换活动/还原时清空登记，并把已写进池子的条目**还原**（把池子交回 SPT 的原值）。</summary>
    public static void Reset()
    {
        try { RestorePool(); } catch { }
        _poolEntries.Clear();
        _appliedFor = null;
        _onlyOnMap = null;        _airdropSessionId = null;
        _warned.Clear();
    }

    /// <summary>把池子恢复成登记时的快照（有旧值写回、无旧值删键）。</summary>
    private static void RestorePool()
    {
        for (int i = _poolEntries.Count - 1; i >= 0; i--)
        {
            try
            {
                var e = _poolEntries[i];
                if (e.HadOld) e.Pool[e.Key] = e.Old;
                else if (e.Pool.Contains(e.Key)) e.Pool.Remove(e.Key);
            }
            catch { }
        }
    }

    // ─────────────────────────── bot 那一侧：按图开关池子 ───────────────────────────

    /// <summary>
    /// `BotGenerator.PrepareAndGenerateBot` 的 **prefix**（`__1` = `BotGenerationDetails`）。
    /// 在 SPT 读战利品池之前，把登记过的条目调成"这张图该有的样子"。**永远返回 true（不拦截）**。
    /// </summary>
    public static bool OnBotGeneratingPrefix(object __1)
    {
        if (_poolEntries.Count == 0) return true;
        long t0 = PerfTimer.Start();          // ★ v2.6.70【Y2】仅计时（logLevel=off 时恒为 0 ⇒ 零开销、零行为变化）
        try
        {
            var map = Prop(__1, "Location")?.ToString();
            if (string.IsNullOrWhiteSpace(map))
            {
                // ★ 兜底（见 class 注释）：拿不到地图 ⇒ **照旧生效**（条目留在池子里）+ warning。
                //   ⚠ 这里必须是 `present: true` —— 早期写成"按 null 处理"会**把条目清掉**，
                //     那就等于本该"照旧生效"却变成"不生效"（探针已抓到这个 bug）。
                WarnOnce("noloc-bot",
                    "[EventRotator][按图] ⚠拿不到 `BotGenerationDetails.Location` ⇒ **按旧行为照常生效**（不做按图过滤）。"
                  + "「只在该地图」本次未生效——漏掉解药会让事件无解，比解药出现在别图严重，故选此兜底。");
                ApplyForMap(null);           // ★ 地图未知 ⇒ 全部条目照常进池（不是"全清"！）
                return true;
            }

            bool matched = false;
            foreach (var e in _poolEntries) if (Matches(map, e.Map)) { matched = true; break; }
            if (!matched) LogInfoSkip("bot", map, __1);
            ApplyForMap(map);
        }
        catch (Exception ex)
        {
            WarnOnce("bot-ex", "[EventRotator][按图] bot 侧按图判定失败（已忽略，不影响 bot 生成）: " + ex.Message);
        }
        PerfTimer.End("按图.BotPrefix(每bot)", t0);      // ★ v2.6.70【Y2】
        return true;
    }

    /// <summary>
    /// 按本局地图把**每条登记**调成"该有的样子"：命中自己那条 `Map` ⇒ 进池，否则 ⇒ 还原/移出。
    /// <paramref name="map"/> 为空 ⇒ 地图未知的兜底 ⇒ **所有条目一律进池**（= 照旧生效）。
    /// **幂等**：同一状态重复调用直接返回。
    /// ⚠ 逐条判 `e.Map` 而不是全局一个地图 —— 这样"带 filter 的活动"与"不带的"互不干扰。
    /// </summary>
    private static void ApplyForMap(string map)
    {
        string key = string.IsNullOrWhiteSpace(map) ? "*" : map.Trim().ToLowerInvariant();
        if (_appliedFor == key) return;
        try
        {
            // ★ v2.6.70【Y1】换图时先把上一张图的"跳过计数"补一条（信息量保留），再清计数
            if (_appliedFor != null) { FlushSkipSummary("bot"); _skipCount.Clear(); _skipLogged.Clear(); }
            bool all = key == "*";
            foreach (var e in _poolEntries)
            {
                bool present = all || Matches(map, e.Map);
                if (present) e.Pool[e.Key] = e.Want;
                else if (e.HadOld) e.Pool[e.Key] = e.Old;                    // 还原成原值
                else if (e.Pool.Contains(e.Key)) e.Pool.Remove(e.Key);       // 原本没有 ⇒ 删掉
            }
            _appliedFor = key;
        }
        catch (Exception ex)
        {
            WarnOnce("apply", "[EventRotator][按图] 写/清池子失败（已忽略）: " + ex.Message);
        }
    }

    /// <summary>
    /// ★ v2.6.70【性能 · Y1（Lead 已批准）】原来**每个 bot 一条**日志（含 2 次反射 + 字符串插值 + **一次日志写**），
    /// 在一局几十~上百只 bot 时是明显 I/O ——现在改成 **每张图一条 + 末尾计数**：
    /// · 首次仍然打**与原来逐字相同**的那条（保留可见性）；
    /// · 之后的跳过只计数；换图/收尾时再打一条「本图共跳过 N 只」。
    /// ⚠ 这是**日志行为**的变化（条数变少，信息量保留），**判定与池子写入逻辑一行未动**。
    /// </summary>
    private static void LogInfoSkip(string side, string map, object details)
    {
        try
        {
            var key = side + "|" + (map ?? "");
            _skipCount[key] = _skipCount.TryGetValue(key, out var c) ? c + 1 : 1;
            if (_skipLogged.Add(key))
            {
                var role = Prop(details, "Role")?.ToString() ?? Prop(details, "RoleLowercase")?.ToString();
                Info($"[按图] 非目标地图 ⇒ **跳过**（{side} 侧；本局地图='{map}'"
                   + (role != null ? $"，bot='{role}'" : "") + $"）；目标地图='{_onlyOnMap}'"
                   + "　（★ 本图后续跳过只计数，不再逐只打日志 （性能考虑））");
            }
        }
        catch { }
    }

    /// <summary>换图/收尾时把"这张图一共跳过多少只"补一条（Y1 的信息量保留）。</summary>
    private static void FlushSkipSummary(string side)
    {
        try
        {
            foreach (var kv in _skipCount)
            {
                if (!kv.Key.StartsWith(side + "|", StringComparison.Ordinal)) continue;
                if (kv.Value <= 1) continue;
                Info($"[按图] 本图共**跳过 {kv.Value} 只 {side}**（非目标地图='{kv.Key.Substring(side.Length + 1)}'，目标='{_onlyOnMap}'）");
            }
        }
        catch { }
    }

    /// <summary>Y1 的计数/已打标记（键 = `side|地图`）。</summary>
    private static readonly Dictionary<string, int> _skipCount = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _skipLogged = new(StringComparer.OrdinalIgnoreCase);

    // ─────────────────────────── 空投那一侧 ───────────────────────────

    /// <summary>`LocationCallbacks.GetAirdropLoot(url, info, sessionID)` 的 prefix —— 只为把 sessionId 记下来。</summary>
    public static void OnAirdropLootRequestPrefix(object __2)
    {
        try { _airdropSessionId = __2?.ToString(); } catch { }
    }

    /// <summary>
    /// 空投追加**要不要生效**（⚠ 判据是**调用方自己那条** <paramref name="onlyOnMap"/>，不是全局值 ——
    /// v2.6.38 回归的根因就是"用了全局过滤，误伤没声明 `onlyOnMap` 的活动"）。
    /// 返回 true = 追加（= 原行为）。
    /// 判定顺序：① 本活动无过滤 ⇒ true；② 有 sessionId ⇒ `GetProfileActivityRaidData(sessionId)`；
    /// ③ 回落 `GetFirstProfileActivityRaidData()`；④ 都拿不到 ⇒ **true + warning**（兜底）。
    /// </summary>
    public static bool AllowedFor(string onlyOnMap)
    {
        if (string.IsNullOrWhiteSpace(onlyOnMap)) return true;
        try
        {
            string map = CurrentRaidMap(out var how);
            if (string.IsNullOrWhiteSpace(map))
            {
                WarnOnce("noloc-air",
                    "[EventRotator][按图] ⚠空投侧拿不到本局地图 ⇒ **按旧行为照常追加**（不做按图过滤）。"
                  + "「只在该地图」本次未生效——漏掉解药会让事件无解，比解药出现在别图严重，故选此兜底。");
                return true;
            }
            bool ok = Matches(map, onlyOnMap);
            if (!ok)
                Info($"[按图] 非目标地图 ⇒ **跳过空投追加**（本局地图='{map}'[{how}]，目标地图='{onlyOnMap}'）");
            else
                Info($"[按图] 目标地图命中 ⇒ 空投正常追加（本局地图='{map}'[{how}]）");
            return ok;
        }
        catch (Exception ex)
        {
            WarnOnce("air-ex", "[EventRotator][按图] 空投侧判定失败（已忽略，按旧行为追加）: " + ex.Message);
            return true;
        }
    }

    /// <summary>取本局地图 id。<paramref name="how"/> 说明用的哪个取值口（写日志用）。</summary>
    private static string CurrentRaidMap(out string how)
    {
        how = "?";
        var svc = ProfileActivityServiceRef;
        if (svc == null) { how = "ProfileActivityService 未注入"; return null; }

        // ① 优先用 sessionId 精确取（多档案更准）
        if (!string.IsNullOrEmpty(_airdropSessionId))
        {
            var raid = CallRaidData(svc, "GetProfileActivityRaidData", _airdropSessionId);
            var m = Prop(Prop(raid, "RaidConfiguration"), "Location")?.ToString();
            if (!string.IsNullOrWhiteSpace(m)) { how = "GetProfileActivityRaidData(sessionId)"; return m; }
        }

        // ② 回落"第一个活跃档案"（SPT 官方自己在 BotGeneratorHelper.cs:42 就用这个口）
        var first = CallRaidData(svc, "GetFirstProfileActivityRaidData", null);
        var m2 = Prop(Prop(first, "RaidConfiguration"), "Location")?.ToString();
        if (!string.IsNullOrWhiteSpace(m2)) { how = "GetFirstProfileActivityRaidData()"; return m2; }

        how = "两个取值口都拿不到";
        return null;
    }

    private static object CallRaidData(object svc, string method, string sessionId)
    {
        try
        {
            var ms = svc.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(x => x.Name == method).ToArray();
            if (ms.Length == 0) return null;
            foreach (var mi in ms)
            {
                var ps = mi.GetParameters();
                object[] args;
                if (ps.Length == 0) args = Array.Empty<object>();
                else if (ps.Length == 1 && !string.IsNullOrEmpty(sessionId))
                {
                    // MongoId 是 struct，参数得先转成它的真实类型
                    object sid;
                    try { sid = CoerceValue(sessionId, ps[0].ParameterType); } catch { continue; }
                    if (sid == null) continue;
                    args = new[] { sid };
                }
                else continue;
                try { return mi.Invoke(svc, args); } catch { }
            }
        }
        catch { }
        return null;
    }

    private static object CoerceValue(string s, Type t)
    {
        if (t == typeof(string)) return s;
        try { return Activator.CreateInstance(t, s); } catch { }
        try { return Convert.ChangeType(s, t); } catch { }
        return null;
    }

    /// <summary>地图名比较：**必须忽略大小写**（`LocationInfection` 的键是混合大小写：`Interchange`/`bigmap`）。</summary>
    private static bool Matches(string map, string want) =>
        !string.IsNullOrEmpty(want) && string.Equals(map?.Trim(), want, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ★ v2.6.70【性能 · G1 反射成员缓存】**零行为变化**：同一 `Type`+名字 ⇒ 每次返回同一个 `MemberInfo`
    /// ⇒ `GetValue` 结果逐字相同。解析顺序 / `BindingFlags` / 索引器判断 / null 兜底**逐条照抄**原实现。
    /// 命中点在**每个 bot** 的路径上（`Prop(__1,"Location")`）⇒ 把"每次解析成员"降为"查一次字典"。
    /// ⚠ 缓存键含 `ignoreCase`：本文件恒为 true（与原实现一致）。
    /// ⚠ 类型热重载才会让缓存过期 —— SPT 运行期不重载程序集 ⇒ 不适用。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string, bool), (PropertyInfo P, FieldInfo F)> _memberCache = new();

    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        // ⚠ 与原实现逐条等价：① 先属性（带 IgnoreCase）② 有索引器参数则视为不可用 ⇒ 落到字段 ③ 都拿不到 ⇒ null
        var m = _memberCache.GetOrAdd((t, name, true), static k =>
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;
            var p = k.Item1.GetProperty(k.Item2, F);
            if (p != null && p.GetIndexParameters().Length == 0) return (p, (FieldInfo)null);
            return ((PropertyInfo)null, k.Item1.GetField(k.Item2, F));
        });
        return m.P != null ? m.P.GetValue(o) : m.F?.GetValue(o);
    }

    // ─────────────────────────── 挂载 ───────────────────────────

    /// <summary>
    /// 挂两处 prefix：
    /// ① `BotGenerator.PrepareAndGenerateBot` —— 按图开关 bot 战利品池；
    /// ② `LocationCallbacks.GetAirdropLoot` —— 只为记住 sessionId（空投判定要用）。
    /// 任何一处找不到都只 warning（其余功能照旧）。
    /// </summary>
    public static void Enable(Harmony harmony, ISptLogger<EventRotatorServer> log)
    {
        Log = log;
        try
        {
            var gen = RuntimeTypes.Find("SPTarkov.Server.Core.Generators.Bot.BotGenerator");
            var prep = gen != null ? AccessTools.Method(gen, "PrepareAndGenerateBot") : null;
            if (prep != null)
            {
                harmony.Patch(prep, new HarmonyMethod(typeof(MapGateDimension), nameof(OnBotGeneratingPrefix)), null, null, null, null);
                log?.Info("[EventRotator][按图] bot 战利品池「按图开关」已挂载（PrepareAndGenerateBot prefix）");
            }
            else log?.Warning("[EventRotator][按图] 找不到 BotGenerator.PrepareAndGenerateBot ⇒ bot 侧按图未挂载");

            var cbs = RuntimeTypes.Find("SPTarkov.Server.Core.Callbacks.LocationCallbacks");
            var gal = cbs != null ? AccessTools.Method(cbs, "GetAirdropLoot") : null;
            if (gal != null)
            {
                harmony.Patch(gal, new HarmonyMethod(typeof(MapGateDimension), nameof(OnAirdropLootRequestPrefix)), null, null, null, null);
                log?.Info("[EventRotator][按图] 空投 sessionId 捕获已挂载（LocationCallbacks.GetAirdropLoot prefix）");
            }
            else log?.Warning("[EventRotator][按图] 找不到 LocationCallbacks.GetAirdropLoot ⇒ 空投侧将只能用 GetFirstProfileActivityRaidData()");
        }
        catch (Exception ex) { log?.Warning("[EventRotator][按图] 挂载失败（已忽略）: " + ex.Message); }
    }
}
