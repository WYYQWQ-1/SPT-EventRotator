using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★★★ v0.1.5【SCAV 接管 · 路费机制】官方「Scav Takeover Event」(2023-05-20) 的**主体机制**。
///
/// 官方原文（events_wiki.txt:1391+）：
///   · 「all known roads, paths and passages, as well as the city have been blocked off by his people (Scavs)」
///   · 「**Only one location can be entered freely** without needing a specific item to enter.
///      **Players under level 16 are not affected** by this.」
///   · 「After a certain amount of combined PMC and Scav kills ("number of victims") is reached, the location
///      that is free switches to another one, and **the required items for the blocked regions change**.」
///   · 免费图顺序：Customs → Reserve → Woods → Factory → Shoreline → Interchange → Lighthouse → Streets → **The Lab**
///   · 原始 wikitext：「they demand pay, but not with money, **but goods**.」
///
/// ⚠ 各图**具体物品清单官方从未公布**（当年挂在游戏官网，现已下线；本机数据也无残留）
///   ⇒ 本维度**完全可配置**（`itemsPerMap`），默认给一组**示例**并在日志里标明"非官方"。
/// ⚠ 官方免费图是**按全服击杀数**切换的（官网计数器）⇒ 服务端无法观测 ✗
///   ⇒ 本实现用**时间**近似（`rotationMode`：`day` = 每天推进一格 / `hours` = 每 N 小时，默认 day），
///     并在日志里说明这是近似。
///
/// 行为（进图时，即 MatchController.ConfigureOfflineRaid 的 prefix）：
///   ① 等级 &lt; exemptLevel（默认 16）⇒ 豁免，直接放行
///   ② 目标是当期免费图 ⇒ 放行
///   ③ 否则按 itemsPerMap 查找所需物品：找到 ⇒ **扣除**（改存档）；找不到 ⇒ 记警告
///      （`strict=true` 时**拦下进图**；默认 false = 只警告，避免把玩家挡在门外 ✗ 风险）
/// </summary>
internal static class TollDimension
{
    public static ISptLogger<EventRotatorServer> Log;
    public static object SaveServer;

    private static string _lastLogKey;
    private static int _probed;
    private static int _probedDlg;
    private static int _probedProf;

    /// <summary>按 rotationMode 取当期免费图。</summary>
    public static string CurrentFreeMap(TollDef t)
    {
        if (t == null) return null;
        if (!string.IsNullOrEmpty(t.FreeMap)) return t.FreeMap;
        var rot = t.Rotation;
        if (rot == null || rot.Count == 0) return null;
        int idx;
        if (string.Equals(t.RotationMode, "hours", StringComparison.OrdinalIgnoreCase))
        {
            var h = Math.Max(1, t.RotationHours <= 0 ? 6 : t.RotationHours);
            idx = (int)((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalHours / h);
        }
        else
        {
            idx = (int)(DateTime.UtcNow.Date - new DateTime(1970, 1, 1)).TotalDays;   // 每天推进一格
        }
        idx = ((idx % rot.Count) + rot.Count) % rot.Count;
        return rot[idx];
    }

    /// <summary>取玩家存档等级（读不到返回 0 = 放行，避免误伤）。</summary>
    private static int LevelOf(object profile)
    {
        try
        {
            var info = Prop(profile, "Info") ?? Prop(profile, "ProfileInfo");
            var lvl = Prop(info, "Level") ?? Prop(profile, "Level");
            return lvl == null ? 0 : Convert.ToInt32(lvl);
        }
        catch { return 0; }
    }

    /// <summary>
    /// 解开 `LazyLoad&lt;T&gt;` 之类的包装（实测：LocaleTable.Global 的值是 `LazyLoad`，
    /// 直接 ToString() 只会得到包装对象 ⇒ 必须取它的 Value/Get()）。
    /// </summary>
    private static string Unwrap(object o)
    {
        try
        {
            for (int depth = 0; depth < 3 && o != null; depth++)
            {
                if (o is string s0) return s0;
                var ty = o.GetType();
                object inner = null;
                var pv = ty.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (pv != null && pv.CanRead) { try { inner = pv.GetValue(o); } catch { } }
                if (inner == null)
                {
                    foreach (var mn in new[] { "Get", "Load", "GetValue" })
                    {
                        var m = ty.GetMethod(mn, Type.EmptyTypes);
                        if (m != null) { try { inner = m.Invoke(o, null); } catch { } }
                        if (inner != null) break;
                    }
                }
                if (inner == null) break;
                o = inner;
            }
        }
        catch { }
        return o?.ToString();
    }

    // ─────────── 物品名（直接读本地化 JSON，保证可用）───────────
    private static Dictionary<string, string> _localeMap;
    private static string _localeLang;

    /// <summary>语言（默认 ch；可在 config 里用 tollLocale 覆盖）。</summary>
    public static string LocaleLang = "ch";

    private static void EnsureLocale()
    {
        try
        {
            if (_localeMap != null && _localeLang == LocaleLang) return;
            var dir = EventEngine.ModPath;
            if (string.IsNullOrEmpty(dir)) return;
            var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, "..", "..", ".."));
            var f = System.IO.Path.Combine(root, "SPT_Data", "database", "locales", "global", LocaleLang + ".json");
            if (!System.IO.File.Exists(f))
                f = System.IO.Path.Combine(root, "SPT_Data", "database", "locales", "global", "en.json");
            if (!System.IO.File.Exists(f)) { Log?.Warning($"[EventRotator][路费] 本地化文件不存在: {f}"); return; }
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(f));
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pr in doc.RootElement.EnumerateObject())
            {
                if (pr.Value.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var s = pr.Value.GetString();
                if (!string.IsNullOrEmpty(s)) map[pr.Name] = s;
            }
            _localeMap = map; _localeLang = LocaleLang;
            Log?.Info($"[EventRotator][路费] 本地化表已加载：{map.Count} 条（{System.IO.Path.GetFileName(f)}）");
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][路费] 读本地化失败: {ex.Message}"); }
    }

    /// <summary>
    /// 通用解包：`LazyLoad&lt;T&gt;` 之类的包装（实测 SptProfile.Dialogues / LocaleTable.Global 都是懒加载包装）
    /// ⇒ 逐层取 Value/Get()/Load()/GetValue()，最多 4 层。
    /// </summary>
    private static object UnwrapObj(object o)
    {
        try
        {
            for (int d = 0; d < 4 && o != null; d++)
            {
                var ty = o.GetType();
                if (o is IDictionary || (o is System.Collections.IEnumerable && !(o is string) && ty.IsGenericType
                    && ty.GetGenericArguments().Length == 2)) return o;   // 已经是字典
                object inner = null;
                var pv = ty.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (pv != null && pv.CanRead && pv.PropertyType != typeof(object)) { try { inner = pv.GetValue(o); } catch { } }
                if (inner == null || ReferenceEquals(inner, o))
                {
                    foreach (var mn in new[] { "Get", "Load", "GetValue", "GetData" })
                    {
                        var m = ty.GetMethod(mn, Type.EmptyTypes);
                        if (m != null) { try { inner = m.Invoke(o, null); } catch { } }
                        if (inner != null && !ReferenceEquals(inner, o)) break;
                    }
                }
                if (inner == null || ReferenceEquals(inner, o)) break;
                o = inner;
            }
        }
        catch { }
        return o;
    }

    /// <summary>tpl → 本地化名称（读 `<lang>.json` 的 `<tpl> Name` / `<tpl> ShortName`；取不到退化）。</summary>
    public static string ItemName(string tpl)
    {
        if (string.IsNullOrEmpty(tpl)) return "?";
        try
        {
            EnsureLocale();
            if (_localeMap != null)
            {
                if (_localeMap.TryGetValue(tpl + " Name", out var n0) && !string.IsNullOrEmpty(n0)) return n0;
                if (_localeMap.TryGetValue(tpl + " ShortName", out var n1) && !string.IsNullOrEmpty(n1)) return n1;
            }
        }
        catch { }
        return tpl;
    }

    /// <summary>★ v1.0.1【性能·第六轮】元数据查找改走 R.GP / R.GF 缓存（属性/字段 flags 不同 ⇒ 分别缓存）。</summary>
    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        return R.GP(t, name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(o)
            ?? R.GF(t, name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(o);
    }

    /// <summary>在存档里找一件该 tpl 的物品并扣 1（优先可拆分的堆叠）。返回是否扣成功。</summary>
    private static bool TakeOne(object profile, string tpl, int count)
    {
        try
        {
            var inv = Prop(profile, "Inventory");
            var items = Prop(inv, "Items") as IList;
            if (items == null) return false;
            int need = Math.Max(1, count);
            int taken = 0;
            for (int i = items.Count - 1; i >= 0 && taken < need; i--)
            {
                var x = items[i];
                if (!string.Equals(Prop(x, "Template")?.ToString() ?? Prop(x, "_tpl")?.ToString(), tpl, StringComparison.OrdinalIgnoreCase)) continue;
                var upd = Prop(x, "Upd") ?? Prop(x, "upd");
                var so = Prop(upd, "StackObjectsCount");
                int have = so == null ? 1 : Convert.ToInt32(so);
                if (have <= need - taken)
                {
                    items.RemoveAt(i); taken += have;
                }
                else
                {
                    var pi = upd?.GetType().GetProperty("StackObjectsCount");
                    if (pi != null && pi.CanWrite) { pi.SetValue(upd, have - (need - taken)); taken = need; }
                    else { items.RemoveAt(i); taken += have; }
                }
            }
            if (taken >= need)
            {
                Log?.Info($"[EventRotator][路费] 已从存档扣除 {need}× {tpl}（SCAV 接管：进图路费）");
                return true;
            }
            return false;
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][路费] 扣物品失败: {ex.Message}"); return false; }
    }

    // ─────────── 每日任务「上交物品」池（用户指定：路费从这里抽）───────────
    private static List<(string Tpl, int MinLevel)> _pool;
    private static DateTime _poolDay = DateTime.MinValue;
    private static (int Min, int Max) _countRange = (1, 4);

    private static string PoolPath()
    {
        try
        {
            var dir = EventEngine.ModPath;                       // user/mods/SPT-EventRotator
            if (string.IsNullOrEmpty(dir)) return null;
            var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, "..", "..", ".."));  // SPT_Runtime
            return System.IO.Path.Combine(root, "SPT_Data", "database", "templates", "repeatableQuests.json");
        }
        catch { return null; }
    }

    /// <summary>加载每日任务上交物品池（含每项的最低等级）。文件读不到就返回空。</summary>
    private static List<(string Tpl, int MinLevel)> LoadDailyQuestPool()
    {
        var list = new List<(string, int)>();
        try
        {
            var path = PoolPath();
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return list;
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data)) return list;
            var comp = data.TryGetProperty("completion", out var c1) ? c1
                     : (data.TryGetProperty("Completion", out var c2) ? c2 : default);
            if (comp.ValueKind != System.Text.Json.JsonValueKind.Object) return list;
            var wl = comp.TryGetProperty("itemsWhitelist", out var w1) ? w1
                   : (comp.TryGetProperty("ItemsWhitelist", out var w2) ? w2 : default);
            if (wl.ValueKind != System.Text.Json.JsonValueKind.Array) return list;
            foreach (var g in wl.EnumerateArray())
            {
                int lv = 0;
                if (g.TryGetProperty("minPlayerLevel", out var lv1) && lv1.ValueKind == System.Text.Json.JsonValueKind.Number) lv = lv1.GetInt32();
                else if (g.TryGetProperty("MinPlayerLevel", out var lv2) && lv2.ValueKind == System.Text.Json.JsonValueKind.Number) lv = lv2.GetInt32();
                var ids = g.TryGetProperty("itemIds", out var i1) ? i1
                        : (g.TryGetProperty("ItemIds", out var i2) ? i2 : default);
                if (ids.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                foreach (var id in ids.EnumerateArray())
                {
                    var s = id.GetString();
                    if (!string.IsNullOrEmpty(s)) list.Add((s, lv));
                }
            }
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][路费] 读取每日任务物品池失败: {ex.Message}"); }
        return list;
    }

    /// <summary>按 (地图, 日期) 稳定地抽一件路费物品；count 取每日任务的 requestedItemCount 范围。</summary>
    private static TollItemDef PickFromDailyPool(string mapKey, int level)
    {
        try
        {
            if (_pool == null || _poolDay != DateTime.UtcNow.Date)
            {
                _pool = LoadDailyQuestPool();
                _poolDay = DateTime.UtcNow.Date;
                Log?.Info($"[EventRotator][路费] 已加载每日任务上交物品池：{_pool.Count} 项"
                        + (_pool.Count > 1 ? $"（示例：{_pool[0].Tpl}…）" : ""));
            }
            // ★ 用**整池**（不做等级过滤）——保证"预先公布的清单"与"进图实际要交的"完全一致
            var cand = _pool;
            if (cand.Count == 0) return null;
            int h = mapKey.Aggregate(17, (acc, ch) => acc * 31 + ch) ^ DateTime.UtcNow.Date.GetHashCode();
            h = Math.Abs(h);
            var pick = cand[h % cand.Count];
            int count = _countRange.Min + (h / 7) % Math.Max(1, _countRange.Max - _countRange.Min + 1);
            return new TollItemDef { Tpl = pick.Tpl, Count = count };
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][路费] 抽物品失败: {ex.Message}"); return null; }
    }

    /// <summary>本地图键 → 中文名（写清单用）。</summary>
    private static readonly Dictionary<string, string> MapZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bigmap"] = "海关", ["woods"] = "森林", ["shoreline"] = "海岸线", ["interchange"] = "立交桥",
        ["rezervbase"] = "储备站", ["lighthouse"] = "灯塔", ["tarkovstreets"] = "塔科夫街区",
        ["factory4_day"] = "工厂（白天）", ["factory4_night"] = "工厂（夜间）", ["laboratory"] = "实验室",
        ["sandbox"] = "中心区", ["sandbox_high"] = "中心区（高难度）", ["labyrinth"] = "迷宫",
    };

    /// <summary>
    /// ★ 把"今天各图要交什么路费"预先算好并写出（`本期路费.txt` + 日志），
    /// 让玩家**进图前**就知道 —— 抽取按（地图, 日期）固定 ⇒ 预公布与实际扣除必然一致。
    /// </summary>
    public static void PublishAll(EventDef ev)
    {
        try
        {
            var t = ev?.Toll;
            if (t == null) return;
            if (_pool == null || _poolDay != DateTime.UtcNow.Date)
            {
                _pool = LoadDailyQuestPool();
                _poolDay = DateTime.UtcNow.Date;
            }
            var free = CurrentFreeMap(t);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== SCAV 接管 · 本期路费（" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "）===");
            sb.AppendLine("免费图（今天不收路费）：" + (MapZh.TryGetValue(free ?? "", out var fz) ? fz + "（" + free + "）" : free ?? "?"));
            sb.AppendLine("规则：**和实验室门卡一样** —— 进图前把下列物品带在身上，进图后会自动消失（16 级以下官方豁免）。");
            sb.AppendLine("物品来源：每日任务「上交物品」池（repeatableQuests.json → data.completion.itemsWhitelist）。");
            sb.AppendLine();
            sb.AppendLine("地图".PadRight(18) + "需要上交");
            sb.AppendLine(new string('-', 52));
            var testTpls0 = (t.TestItemTpls ?? new List<string>()).Where(x => !string.IsNullOrEmpty(x)).ToList();
            foreach (var mk in t.Rotation ?? new List<string>())
            {
                var label = MapZh.TryGetValue(mk, out var zh) ? zh + "（" + mk + "）" : mk;
                if (string.Equals(free, mk, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine(label.PadRight(30) + "★ 免费（今天不收）");
                    continue;
                }
                if (testTpls0.Count > 0)
                {
                    sb.AppendLine(label.PadRight(30) + "1 × " + string.Join(" 或 ", testTpls0.Select(x => ItemName(x))));
                    continue;
                }
                var need = PickFromDailyPool(mk, 1);
                sb.AppendLine(label.PadRight(30) + (need == null ? "（池子为空 ⇒ 不收费）"
                                                                 : need.Count + " × " + ItemName(need.Tpl)));
            }
            sb.AppendLine();
            sb.AppendLine("（同一张图的物品当天固定；跨天会重新抽。想固定某图的物品：在活动 toll.itemsPerMap 里写。）");

            var dir = EventEngine.ModPath;
            if (!string.IsNullOrEmpty(dir))
            {
                var path = System.IO.Path.Combine(dir, "本期路费.txt");
                System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
                Log?.Info($"[EventRotator][路费] 已写出「本期路费.txt」：{path}");
            }
            Log?.Info("[EventRotator][路费] 本期免费图 = " + (free ?? "?")
                    + "；各图路费："
                    + string.Join("、", (t.Rotation ?? new List<string>()).Take(4).Select(mk =>
                        {
                            var n0 = PickFromDailyPool(mk, 1);
                            var lbl = MapZh.TryGetValue(mk, out var z0) ? z0 : mk;
                            return lbl + (n0 == null ? "=无" : "=" + n0.Count + "×" + ItemName(n0.Tpl));
                        })));
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][路费] 写清单失败: {ex.Message}"); }
    }

    // ─────────── 游戏内消息：以 Fence 名义把本期路费发给所有玩家 ───────────
    private static object _notifyHelper;
    private static Type _userDialogInfoT, _userDialogDetailsT, _messageTypeT;

    private static Type FindTypeByName(string fullName)
    {
        try
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var x = a.GetType(fullName, false); if (x != null) return x; } catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>本图需要交什么（供消息文本用）。</summary>
    private static string LineFor(string mk, Dictionary<string, List<TollItemDef>> fixedItems, List<string> testTpls = null)
    {
        var label = MapZh.TryGetValue(mk, out var zh) ? zh : mk;
        // ★ 优先级与实际 Require 一致：测试信物 > itemsPerMap 指定 > 池子抽
        if (testTpls != null && testTpls.Count > 0)
            return label + "：" + string.Join(" 或 ", testTpls.Select(x => ItemName(x) + "（" + x + "）"));
        if (fixedItems != null && fixedItems.TryGetValue(mk, out var lst) && lst != null && lst.Count > 0 && !string.IsNullOrEmpty(lst[0]?.Tpl))
            return label + "：" + lst[0].Count + " × " + ItemName(lst[0].Tpl) + "（" + lst[0].Tpl + "）";
        var need = PickFromDailyPool(mk, 1);
        return label + "：" + (need == null ? "（池子为空 ⇒ 不收费）" : need.Count + " × " + ItemName(need.Tpl) + "（" + need.Tpl + "）");
    }

    // ─────────── 状态比对：只有"免费图 / 所需物品 / 日期"变化时才发消息 ───────────
    private static string StatePath()
    {
        try { return System.IO.Path.Combine(EventEngine.ModPath ?? "", "toll_state.txt"); }
        catch { return null; }
    }

    /// <summary>算出本期状态串（日期 + 免费图 + 每图所需），用于判断"有没有变化"。</summary>
    private static string ComputeState(EventDef ev)
    {
        var t = ev?.Toll;
        if (t == null) return "";
        var sb = new System.Text.StringBuilder();
        sb.Append(DateTime.UtcNow.ToString("yyyy-MM-dd")).Append('|').Append(CurrentFreeMap(t) ?? "?");
        var testTpls = (t.TestItemTpls ?? new List<string>()).Where(x => !string.IsNullOrEmpty(x)).ToList();
        foreach (var mk in t.Rotation ?? new List<string>()) sb.Append('|').Append(mk).Append('=').Append(LineFor(mk, t.ItemsPerMap, testTpls));
        return sb.ToString();
    }

    /// <summary>
    /// ★ 唯一入口：活动套用时调用。状态有变化才写文件 + 发 Fence 站内消息；
    /// 没变化就什么都不做（避免每次进图都刷一条）。活动关掉时本方法根本不会被调用 ⇒ 不会发消息。
    /// </summary>
    public static void PublishIfChanged(EventDef ev)
    {
        try
        {
            if (ev?.Toll == null) return;
            var now = ComputeState(ev);
            var path = StatePath();
            string old = null;
            try { if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path)) old = System.IO.File.ReadAllText(path); } catch { }
            if (string.Equals(old, now, StringComparison.Ordinal))
            {
                Log?.Info("[EventRotator][路费] 本期状态与上次一致（免费图/物品未变）⇒ 不重复发消息");
                return;
            }
            bool first = string.IsNullOrEmpty(old);
            Log?.Info($"[EventRotator][路费] ★本期状态{(first ? "首次生成" : "有变化")} ⇒ 写文件并发送 Fence 消息");
            try { if (!string.IsNullOrEmpty(path)) System.IO.File.WriteAllText(path, now, System.Text.Encoding.UTF8); } catch { }
            PublishAll(ev);
            AnnounceInGame(ev);
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][路费] PublishIfChanged 失败: {ex.Message}"); }
    }

    /// <summary>
    /// ★ 以 **Fence** 名义把本期路费发成**站内消息**（直接写进玩家存档的 Fence 对话，
    /// 并调用 SaveServer 保存 ⇒ 打开消息列表就能看到；不依赖 GetDialog 的内部匹配，
    /// 因为原实现走 `SendMessageToPlayerAsync` + 自造发送者时会静默 no-op —— 用户实测没收到）。
    /// </summary>
    public static void AnnounceInGame(EventDef ev)
    {
        try
        {
            var t = ev?.Toll;
            if (t == null || SaveServer == null) return;
            if (_pool == null || _poolDay != DateTime.UtcNow.Date) { _pool = LoadDailyQuestPool(); _poolDay = DateTime.UtcNow.Date; }

            var free = CurrentFreeMap(t);
            var freeZh = MapZh.TryGetValue(free ?? "", out var fz) ? fz : (free ?? "?");
            var testTpls = (t.TestItemTpls ?? new List<string>()).Where(x => !string.IsNullOrEmpty(x)).ToList();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("【SCAV 接管】Fence 封了所有通路 —— 只有一张图能免费进，其它图要交东西。");
            sb.AppendLine("今天免费：" + freeZh + "（16 级以下玩家不受影响）");
            sb.AppendLine();
            foreach (var mk in t.Rotation ?? new List<string>())
                sb.AppendLine("· " + LineFor(mk, t.ItemsPerMap, testTpls));
            sb.AppendLine();
            sb.AppendLine("（和实验室门卡一样：进图前带在身上，进图后自动消失。同一张图的物品当天固定。）");
            string text = sb.ToString();

            const string fenceId = "579dc571d53a0658a154fbec";
            var midT = FindTypeByName("SPTarkov.Server.Core.Models.Common.MongoId");
            var getProfiles = SaveServer.GetType().GetMethod("GetProfiles", Type.EmptyTypes);
            var profiles = getProfiles?.Invoke(SaveServer, null) as IDictionary;
            if (profiles == null) { Log?.Warning("[EventRotator][路费] 取不到存档列表 ⇒ 消息未发"); return; }

            var sendWs = _notifyHelper;   // 可选：拿到通知服务就顺手推一条 WS 事件（拿不到也没关系，消息本体已写进对话）
            var wsMethod = sendWs?.GetType().GetMethod("SendMessageToPlayerAsync");
            int ok = 0, skipped = 0;
            foreach (DictionaryEntry de in profiles)
            {
                var sid = de.Key?.ToString();
                var prof = de.Value;
                if (string.IsNullOrEmpty(sid) || prof == null) continue;
                try
                {
                    // ★ 实测：profile.dialogues 是**字典**（键=商人 id），不是数组
                    // ★ profile 本身可能也是包装（实测 SaveServer.GetProfiles() 的值上取不到 Dialogues）
                    var profReal = UnwrapObj(prof) ?? prof;
                    if (_probedProf < 1)
                    {
                        _probedProf++;
                        var pty = profReal.GetType();
                        Log?.Warning($"[EventRotator][路费][探针] profile 原类型={prof?.GetType().FullName} 解包后={pty.FullName}"
                                  + "；含 Dialogue 的属性=["
                                  + string.Join(",", pty.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                        .Select(x => x.Name).Where(n => n.IndexOf("ialog", StringComparison.OrdinalIgnoreCase) >= 0))
                                  + "]");
                    }
                    System.Reflection.PropertyInfo dlgProp = null;
                    foreach (var nm in new[] { "DialogueRecords", "Dialogues", "Dialogue", "Dialogs" })
                    {
                        dlgProp = profReal.GetType().GetProperty(nm, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (dlgProp != null) break;
                    }
                    var dlgRaw = dlgProp?.GetValue(profReal);
                    var dlgUn = UnwrapObj(dlgRaw);
                    var dlgDict = dlgUn as IDictionary;
                    if (dlgDict == null)
                    {
                        skipped++;
                        Log?.Warning($"[EventRotator][路费] 存档 {sid} 的 Dialogues 不是字典 ⇒ 跳过"
                                  + $"（原类型={dlgRaw?.GetType().FullName}，解包后={dlgUn?.GetType().FullName}）");
                        continue;
                    }
                    if (_probedDlg < 1)
                    {
                        _probedDlg++;
                        Log?.Warning($"[EventRotator][路费][探针] Dialogues 解包后类型={dlgUn.GetType().FullName} 条数={dlgDict.Count}");
                    }

                    object dialog = null;
                    foreach (DictionaryEntry de2 in dlgDict)
                    {
                        if (string.Equals(de2.Key?.ToString(), fenceId, StringComparison.OrdinalIgnoreCase)) { dialog = de2.Value; break; }
                    }
                    if (dialog == null)
                    {
                        // 退路：按值里的 Uid/Id 找
                        foreach (DictionaryEntry de2 in dlgDict)
                        {
                            var v0 = de2.Value;
                            if (string.Equals(Prop(v0, "Id")?.ToString(), fenceId, StringComparison.OrdinalIgnoreCase)) { dialog = de2.Value; break; }
                        }
                    }
                    if (dialog == null) { skipped++; Log?.Warning($"[EventRotator][路费] 存档 {sid} 里没有 Fence 对话 ⇒ 跳过"); continue; }

                    var msgsProp = dialog.GetType().GetProperty("Messages", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (msgsProp?.GetValue(dialog) is not IList msgs || msgs.Count == 0) { skipped++; continue; }
                    // ★ 关键：要克隆一条**没有 templateId** 的普通消息 ——
                    //   否则客户端会按 templateId 渲染模板文本，完全无视我们写的 text（用户实测踩到）
                    object last = null;
                    for (int i = msgs.Count - 1; i >= 0; i--)
                    {
                        var cand = msgs[i];
                        var tid = Prop(cand, "TemplateId")?.ToString();
                        if (string.IsNullOrEmpty(tid)) { last = cand; break; }
                    }
                    if (last == null) last = msgs[msgs.Count - 1];
                    var msgT = msgs.GetType().IsGenericType ? msgs.GetType().GetGenericArguments()[0] : last.GetType();

                    // 克隆最后一条（字段最全），再改我们需要改的
                    var msg = Activator.CreateInstance(msgT);
                    foreach (var pi in msgT.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!pi.CanRead || !pi.CanWrite || pi.GetIndexParameters().Length > 0) continue;
                        try { pi.SetValue(msg, pi.GetValue(last)); } catch { }
                    }
                    var midT3 = FindTypeByName("SPTarkov.Server.Core.Models.Common.MongoId");
                    object newId = null;
                    try { newId = Activator.CreateInstance(midT3); } catch { }
                    foreach (var kv in new (string, object)[]
                    {
                        ("Id", newId), ("Text", text), ("TemplateId", ""),
                        ("DateTime", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds),
                        ("HasRewards", false), ("RewardCollected", true), ("Items", null), ("MaxStorageTime", 0),
                    })
                    {
                        var pi = msgT.GetProperty(kv.Item1, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (pi != null && pi.CanWrite) { try { pi.SetValue(msg, kv.Item2); } catch { } }
                    }
                    msgs.Add(msg);
                    var newProp = dialog.GetType().GetProperty("New", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (newProp != null && newProp.CanWrite)
                    {
                        try { newProp.SetValue(dialog, Convert.ToInt32(newProp.GetValue(dialog) ?? 0) + 1); } catch { }
                    }
                    ok++;

                    // 顺手推 WS 事件（拿不到通知服务就跳过；消息本体已在对话里）
                    try
                    {
                        if (wsMethod != null && _notifyHelper != null)
                        {
                            var sdT = FindTypeByName("SPTarkov.Server.Core.Models.Eft.Profile.UserDialogInfo");
                            var sd = sdT != null ? Activator.CreateInstance(sdT) : null;
                            if (sd != null && midT != null)
                            {
                                var idp = sdT.GetProperty("Id");
                                if (idp != null && idp.CanWrite) idp.SetValue(sd, Activator.CreateInstance(midT, new object[] { fenceId }));
                            }
                            if (sd != null)
                            {
                                var sidObj = midT != null ? Activator.CreateInstance(midT, new object[] { sid }) : (object)sid;
                                var mtT2 = FindTypeByName("SPTarkov.Server.Core.Models.Enums.MessageType");
                                wsMethod.Invoke(_notifyHelper, new object[] { sidObj, sd, "[SCAV 接管] 本期路费已更新，详见消息列表。", mtT2 != null ? Enum.Parse(mtT2, "NpcTraderMessage") : null });
                            }
                        }
                    }
                    catch { }
                }
                catch (Exception ex) { Log?.Warning($"[EventRotator][路费] 写消息到 {sid} 失败: {ex.Message}"); skipped++; }
            }
            // 保存存档，确保落盘
            try
            {
                var save = SaveServer.GetType().GetMethod("SaveProfile", new[] { typeof(string) });
                if (save != null)
                    foreach (DictionaryEntry de in profiles)
                        if (de.Key != null) { try { save.Invoke(SaveServer, new object[] { de.Key.ToString() }); } catch { } }
            }
            catch { }

            Log?.Info($"[EventRotator][路费] ★站内消息已写入 {ok} 个存档的 Fence 对话（跳过 {skipped}；已调用 SaveProfile 落盘）");
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][路费] 发站内消息失败: {ex.Message}"); }
    }

    // ─────────── 「和实验室卡一样」：写本体 AccessKeys ───────────
    /// <summary>
    /// ★ 把"该图需要的信物"写进本体 `location.Base.AccessKeys`（实验室门卡就是这套机制）：
    /// 游戏会要求玩家**进图前带在身上**，并在**进图后自动消耗掉**；免费图清空它。
    /// 全部登记还原闭包。
    /// </summary>
    public static void ApplyAccessKeys(EventDef ev, object locationTable, Action<Action> addRestore)
    {
        try
        {
            var t = ev?.Toll;
            if (t == null || locationTable == null) return;
            if (!string.Equals(t.Mode, "accessKey", StringComparison.OrdinalIgnoreCase)) return;
            var getDict = locationTable.GetType().GetMethod("GetDictionary", Type.EmptyTypes);
            var dict = (getDict != null ? getDict.Invoke(locationTable, null) : null) as IDictionary;
            if (dict == null) { Log?.Warning("[EventRotator][路费] 拿不到地图字典"); return; }

            var free = CurrentFreeMap(t);
            var testTpls = (t.TestItemTpls ?? new List<string>()).Where(x => !string.IsNullOrEmpty(x)).ToList();
            int changed = 0;
            foreach (var mk in t.Rotation ?? new List<string>())
            {
                object loc = null;
                foreach (DictionaryEntry de in dict)
                    if (string.Equals(de.Key?.ToString(), mk, StringComparison.OrdinalIgnoreCase)) { loc = de.Value; break; }
                if (loc == null) continue;
                var b = Prop(loc, "Base");
                if (b == null) continue;

                // 该图要交什么：测试信物 > itemsPerMap 指定 > 池子抽
                var need = new List<string>();
                if (testTpls.Count > 0) need = testTpls;
                else if (t.ItemsPerMap != null && t.ItemsPerMap.TryGetValue(mk, out var lst) && lst != null && lst.Count > 0)
                    need = lst.Where(x => !string.IsNullOrEmpty(x?.Tpl)).Select(x => x.Tpl).ToList();
                else
                {
                    var pick = PickFromDailyPool(mk, 1);
                    if (pick != null) need = new List<string> { pick.Tpl };
                }
                if (string.Equals(free, mk, StringComparison.OrdinalIgnoreCase)) need = new List<string>();   // 免费图

                foreach (var field in new[] { "AccessKeys", "AccessKeysPvE" })
                {
                    var cur = Prop(b, field);
                    var pi = b.GetType().GetProperty(field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (pi == null || !pi.CanWrite) continue;
                    var capB = b; var capPi = pi; var capOld = cur;
                    addRestore?.Invoke(() => { try { capPi.SetValue(capB, capOld); } catch { } });
                    try
                    {
                        // ★ 修：属性类型是 `IEnumerable<string>`（接口）⇒ 不能 Activator 造接口，
                        //   要直接构造 `List<元素类型>` 再赋值（List<T> 实现 IEnumerable<T>）
                        var pt = pi.PropertyType;
                        var elem = pt.IsGenericType ? pt.GetGenericArguments()[0]
                                 : (pt.IsArray ? pt.GetElementType() : typeof(string));
                        var listT = typeof(List<>).MakeGenericType(elem);
                        var nl = Activator.CreateInstance(listT);
                        if (nl is IList il)
                        {
                            foreach (var tpl in need)
                            {
                                object v = elem == typeof(string) ? tpl : Activator.CreateInstance(elem, new object[] { tpl });
                                il.Add(v);
                            }
                        }
                        if (pt.IsAssignableFrom(listT)) pi.SetValue(b, nl);
                        else if (pt.IsArray && nl is IList il2)
                        {
                            var arr = Array.CreateInstance(elem, il2.Count);
                            for (int q = 0; q < il2.Count; q++) arr.SetValue(il2[q], q);
                            pi.SetValue(b, arr);
                        }
                        else { Log?.Warning($"[EventRotator][路费] 属性 {field} 类型 {pt.Name} 不受支持，跳过"); continue; }
                        changed++;
                    }
                    catch (Exception ex) { Log?.Warning($"[EventRotator][路费] 写 {mk}.{field} 失败: {ex.Message}"); }
                }
                // 等级门槛清 0（官方是"16 级以下豁免"，交给本 mod 的服务器侧判定，不吃本体等级门槛）
                var ml = b.GetType().GetProperty("MinPlayerLvlAccessKeys");
                if (ml != null && ml.CanWrite) { addRestore?.Invoke(() => { try { ml.SetValue(b, 0); } catch { } }); try { ml.SetValue(b, 0); } catch { } }

                var label = MapZh.TryGetValue(mk, out var zh) ? zh : mk;
                Log?.Info($"[EventRotator][路费] {label}：AccessKeys = " +
                          (need.Count == 0 ? "（免费）" : string.Join(",", need.Select(x => ItemName(x)))) + "（和实验室门卡同机制：带着进图、进图后消失）");
            }
            Log?.Info($"[EventRotator][路费] ★已按「实验室门卡」机制写入 AccessKeys：{changed} 处"
                    + (testTpls.Count > 0 ? "（当前为**测试信物**模式）" : "（当前为**随机物品**模式）"));
        }
        catch (Exception ex) { Log?.Warning($"[EventRotator][路费] 写 AccessKeys 失败: {ex.Message}"); }
    }

    /// <summary>进图时执行（mapKey = 目标地图 key；sessionId = 房主）。</summary>
    public static void Enforce(EventDef ev, string mapKey, string sessionId)
    {
        try
        {
            var t = ev?.Toll;
            if (t == null || string.IsNullOrEmpty(mapKey)) return;
            // ★ accessKey 模式：由游戏本体执行"带着进图、进图后消失" ⇒ 本 mod 不再手动扣，避免双重收费
            if (string.Equals(t.Mode, "accessKey", StringComparison.OrdinalIgnoreCase))
            {
                var freeM = CurrentFreeMap(t);
                if (!string.Equals(freeM, mapKey, StringComparison.OrdinalIgnoreCase))
                {
                    object pf = null;
                    try
                    {
                        var gp = SaveServer?.GetType().GetMethod("GetProfile", new[] { typeof(string) });
                        pf = gp?.Invoke(SaveServer, new object[] { sessionId });
                    }
                    catch { }
                    var lv = pf == null ? 0 : LevelOf(pf);
                    var ex = t.ExemptLevel <= 0 ? 16 : t.ExemptLevel;
                    if (lv > 0 && lv < ex)
                        Log?.Info($"[EventRotator][路费] 地图={mapKey} 玩家 {lv} 级 < 豁免 {ex} ⇒ 官方规则：不受影响（但本体的 AccessKeys 仍会要求信物 ✗ 已知偏差）");
                }
                return;
            }
            var free = CurrentFreeMap(t);
            if (string.Equals(free, mapKey, StringComparison.OrdinalIgnoreCase)) return;   // 免费图放行

            object profile = null;
            try
            {
                var getProfile = SaveServer?.GetType().GetMethod("GetProfile", new[] { typeof(string) });
                profile = getProfile?.Invoke(SaveServer, new object[] { sessionId });
            }
            catch { }
            if (profile == null) return;

            int lvl = LevelOf(profile);
            int exempt = t.ExemptLevel <= 0 ? 16 : t.ExemptLevel;
            if (lvl > 0 && lvl < exempt)
            {
                Log?.Info($"[EventRotator][路费] 地图={mapKey} 玩家 {lvl} 级 < 豁免等级 {exempt} ⇒ 免路费放行（官方规则）");
                return;
            }

            // 该图要交什么
            TollItemDef need = null;
            if (t.ItemsPerMap != null)
            {
                if (!t.ItemsPerMap.TryGetValue(mapKey, out var list)) t.ItemsPerMap.TryGetValue("*", out list);
                need = list?.FirstOrDefault();
            }
            if ((need == null || string.IsNullOrEmpty(need.Tpl)) && t.UseDailyQuestPool)
            {
                need = PickFromDailyPool(mapKey, lvl);
                if (need != null)
                    Log?.Info($"[EventRotator][路费] 地图={mapKey} 本期路费（从每日任务上交池抽取）：{need.Count}× {need.Tpl}");
            }
            if (need == null || string.IsNullOrEmpty(need.Tpl))
            {
                var key0 = mapKey + "|none";
                if (_lastLogKey != key0)
                {
                    _lastLogKey = key0;
                    Log?.Warning($"[EventRotator][路费] 地图={mapKey}：**未配置路费物品** ⇒ 放行（官方物品清单未公布，"
                               + "请在 config.json 的 `tollItems` 或活动 `toll.itemsPerMap` 里填）");
                }
                return;
            }

            bool paid = TakeOne(profile, need.Tpl, need.Count);
            var key1 = mapKey + "|" + need.Tpl + "|" + paid;
            if (_lastLogKey != key1)
            {
                _lastLogKey = key1;
                if (paid) Log?.Info($"[EventRotator][路费] 地图={mapKey} ✓ 已交路费 {need.Count}× {need.Tpl}（免费图={free}）");
                else Log?.Warning($"[EventRotator][路费] 地图={mapKey} ✗ 缺少路费 {need.Count}× {need.Tpl}"
                                + (t.Strict ? " ⇒ 【已拦下进图】(strict=true)" : " ⇒ 本次放行（strict=false，仅警告）"));
            }
            if (!paid && t.Strict)
                throw new InvalidOperationException("[EventRotator][路费] 未交路费，进图被拦下（SCAV 接管）");
        }
        catch (Exception ex)
        {
            Log?.Warning($"[EventRotator][路费] {ex.Message}");
            throw;
        }
    }
}
