using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Utils;

namespace EventRotator;

/// <summary>
/// ★★★ A1 修复（task-26，**最终版**）：压住某图的普通 SCAV —— **完全不碰 ABPS**。
///
/// <para><b>硬约束（用户拍板）</b>：<i>"我们的目标是不动别的 mod，只改我们自己的"</i> ⇒
/// <b>不改 ABPS 的配置文件、不改它的运行期对象、不给它打新补丁</b>。
/// 本类因此**只写 SPT 自己的 DB 对象**（<c>locationTable</c> 里的 <c>LocationBase</c>），
/// 与我们补首领条目（D2）走的是完全同一条路。</para>
///
/// <para><b>为什么必须"在 ABPS 写完之后"压</b>（ABPS 源码实证
/// <c>Server/Controllers/MapSpawns.cs:170-171</c> 与 <c>:223-224</c>）：
/// <code>
/// _locationData[actualKey].Base.BossLocationSpawn = cloner.Clone(_botMapCache[map]);
/// _locationData[actualKey].Base.Waves             = cloner.Clone(_scavMapCache[map]);   // ★ 整体替换
/// </code>
/// 而 <c>_scavMapCache</c> 由 <c>BuildStartingScavs()</c> → <c>ScavSpawns.GetCustomMapData(map)</c> 填充，
/// 后者按 <c>maxBotSpawns[map]</c> 生成 —— 所以**提前写 <c>Base.Waves</c> 一定会被 ABPS 覆盖**。
/// 我们的 D2 postfix 正好挂在它之后（<c>ReplaceOriginalLocationForMap</c> / <c>ReplaceOriginalLocations</c>），
/// 所以复用它即可：<b>ABPS 写 → 我们改</b>，每次都重压 ⇒ 天然不被冲掉。</para>
///
/// <para><b>三条 SCAV 路径与本类的覆盖情况</b>（前两条由 <c>Base.Waves</c> 承载，第三条不是）：</para>
/// <list type="number">
///   <item><b>开局固定 SCAV</b>（<c>ScavSpawns.GenerateStartingScavs</c> → <c>Base.Waves</c>）⇒ ✅ 清掉</item>
///   <item><b>迟到入局波次</b>（<c>GetLateStartMapData</c> → <c>Base.Waves</c>）⇒ ✅ 清掉</item>
///   <item><b>非波次持续生成</b>（客户端 <c>NonWavesSpawnScenario</c>，被 ABPS 的客户端补丁
///     <c>NonWavesSpawnSystemPatch</c> 改写）⇒ 它**不读 <c>Base.Waves</c>**，
///     但它的准入判据里有一项是 <c>BotLocationModifier.NonWaveSpawnBotsLimitPerPlayer(PvE)</c>
///     —— 那是 **SPT 自己的 DB 字段**（`BotLocationModifier.cs` 里有 `[JsonPropertyName]`），
///     所以本类**同时把它压 0**。⚠ 仍有残余，见本注释末尾。</item>
/// </list>
///
/// <para><b>幂等</b>：只做"删条目 / 写 0"，重复执行结果相同（ABPS 每次重建后我们再压一次）。</para>
///
/// <para><b>可还原</b>：<b>不需要我们登记还原闭包</b> —— 理由：
/// ① 活动关闭/切换后我们**不再压**（<see cref="ClearRound"/> 清空 <c>_want</c>）；
/// ② 下一次 ABPS 重建（每局结束 / 启动 / PBS 保存）会用
/// <c>cloner.Clone(_scavMapCache[map])</c> **整体替换** <c>Base.Waves</c>；
/// ③ <c>BotLocationModifier</c> 的两个 0 由我们**自己**登记原值并每轮回写
///    （<see cref="RestoreNonWaveLimits"/>，由 <c>Plugin.RestoreDbLayer()</c> 挂进"先还原再套用"的链上）。
///    ⚠ 旧实现这一条是**坏的**：`RestoreNonWaveLimits` 全库零调用、注释说"由 ApplyToDb 快照复位"
///    但那段永不可达 ⇒ 压过 0 之后该图的非波次 SCAV 永久消失。
/// 这与 D2 首领补写的"可还原"语义完全一致。</para>
///
/// <para>⚠ <b>残余（已知、明说）</b>：ABPS 客户端补丁用
/// <c>Utility.BotsSpawnedPerPlayer &gt;= ____botsController.BotLocationModifier.NonWaveSpawnBotsLimitPerPlayerPvE</c>
/// 做准入 —— 正常情况下我们压 0 就够；但**若 ABPS 客户端插件在 BepInEx 配置里覆盖了该上限**，
/// 那属于 ABPS 客户端配置，本 mod 按约束不碰。详见 <c>audit/A1-ABPS-修法.md</c> §⑧。</para>
/// </summary>
public static class EventScavSuppress
{
    private static ISptLogger<EventRotatorServer> _log;

    /// <summary>当前轮次要压的 mapKey（小写）。由 Plugin 在套用时设置，<see cref="ClearRound"/> 清空。</summary>
    private static readonly HashSet<string> _want = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本轮实际压制动作的去重（避免 postfix 多次触发刷屏；不影响幂等）。</summary>
    private static readonly HashSet<string> _logged = new(StringComparer.OrdinalIgnoreCase);

    public static void Init(ISptLogger<EventRotatorServer> log) => _log = log;

    /// <summary>给"无 ABPS 时自己压"的路径用（<see cref="SuppressAllDbBases"/>）。</summary>
    private static object _locationTable;

    public static void Init(ISptLogger<EventRotatorServer> log, object locationTable)
    {
        _log = log;
        _locationTable = locationTable;
    }

    /// <summary>本轮没有任何活动要压 ⇒ 保持 ABPS 原样（这就是"可还原"）。</summary>
    public static bool IsEmpty => _want.Count == 0;

    /// <summary>
    /// ★★★ 键名规范化：**小写 + 去掉下划线**。
    ///
    /// <para><b>为什么必须有它（真 bug 的修复）</b>：SPT 有**两套地图键空间**：
    ///   · <c>LocationTable.GetDictionary()</c> 的键 = **CLR 属性名**
    ///     （<c>LocationTable.cs:412-419</c> 的 <c>HydrateDictionary()</c>：<c>source.ToDictionary(p =&gt; p.Name, …)</c>）
    ///     ⇒ <c>Factory4Day</c> / <c>SandboxHigh</c> / <c>Woods</c> / <c>RezervBase</c>
    ///   · <c>LocationBase.Id</c> = 配置里的 id
    ///     ⇒ <c>factory4_day</c> / <c>Sandbox_high</c> / <c>Woods</c> / <c>RezervBase</c></para>
    ///
    /// <para>两者对 <c>factory4_day</c> 与 <c>sandbox_high</c> **不是同一个字符串**
    /// （<c>factory4day</c> vs <c>factory4_day</c>）⇒ 早先"把字典键 <c>ToLowerInvariant()</c> 后直接比"
    /// 一次都匹配不上，<c>SuppressMap</c> 从未被调用（Lead 实跑日志只有登记行、没有清除行）。
    /// 规范化后三者（<c>factory4_day</c> / <c>Factory4Day</c> / <c>factory4day</c>）统一成
    /// <c>factory4day</c> ⇒ 用哪套键空间都能命中。</para>
    /// </summary>
    public static string NormKey(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace("_", "").Trim().ToLowerInvariant();

    /// <summary>两张图是否是同一张（规范化后比较）。供 D2 的 <c>onlyMap</c> 过滤用，避免两套键空间对不上。</summary>
    public static bool SameMap(string a, string b)
    {
        var na = NormKey(a);
        return na.Length > 0 && na == NormKey(b);
    }

    /// <summary>某图是否需要压制（**规范化比较**，对两套键空间都成立）。</summary>
    public static bool Wants(string mapKey) =>
        !string.IsNullOrEmpty(mapKey) && _want.Contains(NormKey(mapKey));

    /// <summary>活动套用阶段：登记要压的图（可多次调用，累加；内部按规范化键存储）。</summary>
    public static void AddWanted(IEnumerable<string> maps)
    {
        if (maps == null) return;
        foreach (var m in maps)
        {
            var k = NormKey(m);
            if (k.Length > 0) _want.Add(k);
        }
    }

    /// <summary>每轮 restore 之后调用：清空"要压的图"，下一轮由活动数据重新填充。</summary>
    public static void ClearRound()
    {
        try { _want.Clear(); _logged.Clear(); _matchedThisRound.Clear(); } catch { }
    }

    /// <summary>本轮是否至少成功匹配过一张图 —— 用于"登记了却一张都没匹配上"的诊断告警。</summary>
    private static readonly HashSet<string> _matchedThisRound = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★ 在 D2 postfix 里调用（ABPS 写完 <c>Base.Waves</c> 之后）。
    /// <paramref name="dbBase"/> = DB 的 <c>LocationBase</c>（不是进图克隆）——
    /// 因为它就是 <c>/client/locations</c> 发给客户端的那份。
    /// </summary>
    public static void SuppressMap(string mapKey, object dbBase)
    {
        if (dbBase == null) return;
        var key = (mapKey ?? "").Trim().ToLowerInvariant();
        if (key.Length == 0 || !Wants(key)) return;
        try
        {
            int removed = SuppressWaves(dbBase);
            int nw = SuppressNonWaveLimits(dbBase, key);

            if (_logged.Add(key))
                _log?.Info($"[EventRotator][SCAV抑制] {key}: Waves {removed} 条 assault/marksman 已清除"
                         + $"（ABPS 写完后压制）；NonWaveSpawnBotsLimitPerPlayer(PvE) → 0 = {nw > 0}");
            else if (removed > 0 || nw > 0)
                _log?.Info($"[EventRotator][SCAV抑制] {key}: 再次压制 Waves −{removed} 条 / NonWave 置 0（幂等重压）");
        }
        catch (Exception ex)
        {
            SafeWarn($"[EventRotator][SCAV抑制] {key} 压制失败（已跳过）: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// **无 ABPS 时的兜底路径**：直接遍历 <c>locationTable</c> 的所有 <c>Location.Base</c>，
    /// 对 <c>_want</c> 里的图做同样的压制。
    ///
    /// <para>为什么需要：<see cref="SuppressMap"/> 依赖我们 D2 的 postfix（挂在 ABPS 的
    /// <c>ReplaceOriginalLocationForMap</c> / <c>ReplaceOriginalLocations</c> 上）——
    /// **没装 ABPS 时那个 hook 根本挂不上**（D2 会打"未发现 MapSpawns 类型"的降级日志）。
    /// 但没装 ABPS 时 SPT 就用原版 <c>Base.Waves</c> 出 SCAV ⇒ 更要压。
    /// 所以这条路径自己遍历表，不依赖任何第三方 mod。</para>
    ///
    /// <para>幂等：与 <see cref="SuppressMap"/> 共用同一套"删 assault/marksman + NonWave 置 0"，
    /// 重复执行结果相同。</para>
    /// </summary>
    public static void SuppressAllDbBases()
    {
        if (IsEmpty || _locationTable == null) return;
        try
        {
            var dict = _locationTable.GetType().GetMethod("GetDictionary")?.Invoke(_locationTable, null)
                       as IDictionary;
            if (dict == null)
            {
                SafeWarn("[EventRotator][SCAV抑制] 兜底路径拿不到 LocationTable.GetDictionary()（已跳过）");
                return;
            }
            // id → CLR 属性名 的官方映射（"factory4_day" / "Factory4Day" → "Factory4Day"）
            var mappedKeyMethod = _locationTable.GetType().GetMethod("GetMappedKey", new[] { typeof(string) });

            var seenIds = new List<string>();
            foreach (DictionaryEntry e in dict)
            {
                var baseObj = Prop(e.Value, "Base") ?? e.Value;
                if (baseObj == null) continue;

                var id = (Prop(baseObj, "Id")?.ToString() ?? "").Trim();          // ★ 与 D2 同键空间
                var dictKey = (e.Key?.ToString() ?? "").Trim();                   // CLR 属性名（Factory4Day）
                if (id.Length > 0) seenIds.Add(id);
                if (id.Length == 0 && dictKey.Length == 0) continue;

                // ★ 三套候选键都试（都走 NormKey ⇒ 两套键空间通用），任一命中就压：
                //   ① Base.Id          —— 与 D2 的 _dbBaseByMap / events.json 同键空间（`factory4_day`）
                //   ② 字典键           —— CLR 属性名（`Factory4Day`）
                //   ③ GetMappedKey(id) —— 官方 id→属性名 映射（补 ① 与 ② 之间的第三态）
                string hit = null;
                foreach (var cand in new[]
                {
                    id,
                    dictKey,
                    mappedKeyMethod == null ? null : mappedKeyMethod.Invoke(_locationTable, new object[] { id }) as string,
                })
                {
                    if (string.IsNullOrEmpty(cand)) continue;
                    if (!Wants(cand)) continue;
                    hit = cand;
                    break;
                }
                if (hit == null) continue;

                // 交给 SuppressMap 的 key 用 **Base.Id 小写**（与 D2 一致，日志里就是配置里写的那个形态）
                var logKey = id.Length > 0 ? id.ToLowerInvariant() : hit.Trim().ToLowerInvariant();
                _matchedThisRound.Add(NormKey(logKey));
                SuppressMap(logKey, baseObj);
            }

            // ★ 诊断（回读自证）：登记了却一张都没匹配上 ⇒ 打 warning 并列出真实 id，这类键空间 bug 下次一眼可见
            if (_matchedThisRound.Count == 0 && _logged.Count == 0)
            {
                SafeWarn($"[EventRotator][SCAV抑制] 兜底路径：登记了 [{string.Join(", ", _want)}] 但 LocationTable 里"
                       + $"一张都没匹配上（已跳过）—— 现有 id 样例：{string.Join(", ", seenIds.Take(12))}…"
                       + "（若地图 id 形态又变了，请把上面这份清单发回来）");
            }
        }
        catch (Exception ex)
        {
            SafeWarn($"[EventRotator][SCAV抑制] 兜底路径压制失败（已跳过）: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 删掉 <c>Base.Waves</c> 里 <c>WildSpawnType</c> 为 <c>assault</c> / <c>marksman</c> 的条目，返回删除数。
    ///
    /// <para><b>为什么"删"而不是"把 slots 压 0"</b>：ABPS 生成的 assault 波次是
    /// <c>SlotsMin=0 / SlotsMax=1</c>（源码 <c>ScavSpawns.cs:124-125</c>）—— 本来就是"0~1"，
    /// 再压 0 没有额外效果；只有**把条目删掉**才能让这张表的 assault 计数归零。
    /// 而客户端 <c>InitMinMax</c>（<c>Assembly-CSharp.decompiled.cs:563238</c>）在表里找不到该类型时
    /// 写入 <c>minCounts[type] = 0</c>，<c>CheckMinCounts</c>（<c>:563298</c>）随即 <c>num &gt;= num2</c> 直接 return
    /// ⇒ **不会把删掉的波次补回来**。</para>
    ///
    /// <para>其他 <c>WildSpawnType</c>（pmcBot / bossXxx / sectant* / infected* …）**一律不动** ——
    /// 我们只压"普通 SCAV"，不清场。</para>
    /// </summary>
    private static int SuppressWaves(object dbBase)
    {
        if (Prop(dbBase, "Waves") is not IList list) return 0;
        var doomed = new List<int>();
        for (int i = 0; i < list.Count; i++)
        {
            var t = WildTypeName(list[i]);
            if (t == null) continue;
            if (t.Equals("assault", StringComparison.OrdinalIgnoreCase)
             || t.Equals("marksman", StringComparison.OrdinalIgnoreCase))
                doomed.Add(i);
        }
        // 从后往前删，避免下标漂移
        for (int k = doomed.Count - 1; k >= 0; k--) list.RemoveAt(doomed[k]);
        return doomed.Count;
    }

    /// <summary>两个"非波次 SCAV 上限"字段（PvE 与普通各一份，原值分别登记）。</summary>
    private static readonly string[] NonWaveFields =
        { "NonWaveSpawnBotsLimitPerPlayer", "NonWaveSpawnBotsLimitPerPlayerPvE" };

    /// <summary>
    /// ★ P0 修复：<c>NonWaveSpawnBotsLimitPerPlayer(PvE)</c> 的**真实原值**登记表。
    /// 键 = <c>NormKey(mapKey)|字段名</c>；值 = (DB 的 LocationBase 引用, 首次写入前的原值)。
    ///
    /// <para>旧实现把它压 0 之后**永不复位** —— <see cref="RestoreNonWaveLimits"/> 全库零调用
    /// （注释里写着"由 ApplyToDb 快照复位"，但 ApplyToDb 每进程只跑一次、还原分支不可达）
    /// ⇒ 玩过 <c>reshala_vs_glukhar</c>(bigmap) / <c>factory_cleanup_crew</c>(factory4_day) 之后，
    /// 海关/工厂的**非波次 SCAV 永久消失**。现在：首次写入前登记原值，每轮还原链回写。</para>
    /// <para>只登记一次（用现有键判重）⇒ 不会把"已经被我们压成 0 的值"当成原值。</para>
    /// </summary>
    private static readonly Dictionary<string, (object DbBase, int Original)> _nonWaveOrig =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 第三条路径（客户端 <c>NonWavesSpawnScenario</c>）的准入上限 —— 写 **SPT 自己的** DB 字段，
    /// <b>不是</b> ABPS 的配置。返回被真正改动的个数（用于日志）。
    /// <para>★ P0 修复：写入前先把原值登记进 <see cref="_nonWaveOrig"/>（供
    /// <see cref="RestoreNonWaveLimits"/> 回写）。原值只在**首次**登记，重复压制不会覆盖基线。</para>
    /// </summary>
    private static int SuppressNonWaveLimits(object dbBase, string mapKey)
    {
        if (Prop(dbBase, "BotLocationModifier") is not object blm) return 0;
        int n = 0;
        foreach (var f in NonWaveFields)
        {
            var v = Prop(blm, f);
            if (v == null) continue;                       // 该版本没有此字段 ⇒ 跳过
            int cur;
            try { cur = Convert.ToInt32(v); } catch { continue; }
            var rk = NormKey(mapKey) + "|" + f;
            if (!_nonWaveOrig.ContainsKey(rk)) _nonWaveOrig[rk] = (dbBase, cur);   // ★ 首次写入前登记原值
            if (cur == 0) continue;                       // 已经是 0 ⇒ 幂等，不动
            if (SetProp(blm, f, 0)) n++;
        }
        return n;
    }

    /// <summary>
    /// ★ P0 修复：把 <c>BotLocationModifier.NonWaveSpawnBotsLimitPerPlayer(PvE)</c> 复位到**真实原值**。
    /// 由 <c>Plugin.RestoreDbLayer()</c> 挂进每轮"先还原、再套用"的链上。
    ///
    /// <para><b>幂等 + 安全</b>：只在"当前值仍是 0（= 我们压的那个值）"时才回写原值；
    /// 别人（ABPS / 用户手改）已经改成非 0 的一律不动 ⇒ 重复调用结果相同、不会写坏别人的值。
    /// 返回实际回写的字段数（用于日志）。</para>
    /// <paramref name="locationTable"/> 仅为接口对称保留（记录里已持有 DB 的 LocationBase 引用）。
    /// </summary>
    public static int RestoreNonWaveLimits(object locationTable = null)
    {
        if (_nonWaveOrig.Count == 0) return 0;
        int restored = 0;
        try
        {
            foreach (var kv in _nonWaveOrig.ToList())
            {
                var dbBase = kv.Value.DbBase;
                var orig = kv.Value.Original;
                if (dbBase == null) continue;
                if (Prop(dbBase, "BotLocationModifier") is not object blm) continue;
                var f = kv.Key.Substring(kv.Key.LastIndexOf('|') + 1);
                var v = Prop(blm, f);
                if (v == null) continue;
                int cur;
                try { cur = Convert.ToInt32(v); } catch { continue; }
                if (cur != 0) continue;                    // ★ 不是我们压的 0（或已还原）⇒ 保持不动
                if (SetProp(blm, f, orig)) restored++;
            }
        }
        catch (Exception ex)
        {
            SafeWarn($"[EventRotator][SCAV抑制] 还原 NonWave 上限失败（已跳过）: {ex.GetType().Name}");
        }
        if (restored > 0)
            SafeInfo($"[EventRotator][SCAV抑制] 已还原 NonWaveSpawnBotsLimitPerPlayer(PvE)：{restored} 个字段回写原值");
        return restored;
    }

    // ─────────────── 反射小工具（只读本 mod 的目标 = SPT 的 DB 对象）───────────────

    /// <summary>取 <c>Wave.WildSpawnType</c> 的**名字**（<c>WildSpawnType</c> 是枚举，名字跨版本更稳）。</summary>
    private static string WildTypeName(object wave)
    {
        try
        {
            var v = Prop(wave, "WildSpawnType");
            if (v == null) return null;
            return v is string s ? s : v.ToString();
        }
        catch { return null; }
    }

    /// <summary>★ v1.0.1【性能·第六轮】PropertyInfo 走 R.GP 缓存；外层 try/catch 语义逐字保留
    /// （`GetValue` 抛异常仍然返回 null）。</summary>
    private static object Prop(object o, string name)
    {
        try { return o == null ? null : R.GP(o.GetType(), name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o); }
        catch { return null; }
    }

    private static bool SetProp(object o, string name, object val)
    {
        try
        {
            var p = o?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) return false;
            var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (pt == typeof(int)) p.SetValue(o, Convert.ToInt32(val));
            else if (pt == typeof(double)) p.SetValue(o, Convert.ToDouble(val));
            else if (pt == typeof(float)) p.SetValue(o, Convert.ToSingle(val));
            else p.SetValue(o, val);
            return true;
        }
        catch { return false; }
    }

    /// <summary>连日志都不许抛（沿用 BossWavePatch.SafeWarn 的教训：ISptLogger 实现链可能解析失败）。</summary>
    private static void SafeWarn(string msg)
    {
        try { _log?.Warning(msg); } catch { }
    }

    /// <summary>同 <see cref="SafeWarn"/>，用于"还原成功"这类 Info 级自证日志。</summary>
    private static void SafeInfo(string msg)
    {
        try { _log?.Info(msg); } catch { }
    }
}
