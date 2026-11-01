using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SPTarkov.Common.Models.Logging;

namespace EventRotator;

/// <summary>
/// ★ v0.1.3 Transit（多图转运）维度 —— 对应活动：`marathon` 马拉松。
///
/// 落点（实机数据实证）：`database/locations/&lt;map&gt;/base.json → transits[]`。
///   每条形如：
///   <code>
///   { "id": 1, "name": "SAN_TRANSIT_1", "location": "TarkovStreets",
///     "active": true, "activateAfterSec": 60, "time": 20, "target": "5714dc69..." }
///   </code>
///   **`active` 就是"这个转场口开不开"** —— 本体 13 张图共 **38 条**转场，一部分默认 `false`（需任务解锁）。
///
/// 本维度把全部转场口打开 = 复刻"马拉松"（零号地→街区↔实验室→立交桥→海关→工厂→森林→储备站→灯塔→海岸线）。
/// 全部可还原。
///
/// ★ 重要：这是**纯数据**改动（改 LocationBase.Transits 里的 bool），没有对游戏打任何补丁。
/// </summary>
internal static class TransitDimension
{
    public static object LocationTable;
    public static Action<string> Log;
    public static Action<Action> AddRestore;

    private static void Info(string m) { try { Log?.Invoke("[EventRotator][转场] " + m); } catch { } }

    private static object Prop(object o, string n)
        => R.PropWithFlags(o, n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);

    /// <summary>遍历 LocationTable 的所有地图，把每条 transits[].active 设为 want。返回改动条数。</summary>
    public static void Apply(bool want, ISptLogger<EventRotatorServer> logger)
    {
        try
        {
            if (LocationTable == null) { logger?.Warning("[EventRotator][转场] 拿不到 LocationTable"); return; }

            int changed = 0, maps = 0, total = 0;
            // 优先官方 API 逐图取；同时用属性遍历兜底（不同 SPT 版本 LocationTable 形状略有差异）
            var tblType = LocationTable.GetType();
            var getLoc = tblType.GetMethod("GetLocation", new[] { typeof(string) });
            var mapIds = new[] { "bigmap", "woods", "shoreline", "interchange", "lighthouse", "rezervbase",
                                 "laboratory", "labyrinth", "tarkovstreets", "sandbox", "sandbox_high",
                                 "factory4_day", "factory4_night", "town", "terminal", "suburbs" };

            var seen = new HashSet<object>();
            foreach (var mapId in mapIds)
            {
                object loc = null;
                try { if (getLoc != null) loc = getLoc.Invoke(LocationTable, new object[] { mapId }); } catch { }
                if (loc == null) continue;
                if (!seen.Add(loc)) continue;
                var baseObj = Prop(loc, "Base") ?? loc;
                if (baseObj == null) continue;

                var transits = Prop(baseObj, "Transits") as IEnumerable;
                if (transits == null) continue;
                int perMap = 0;
                foreach (var t in transits)
                {
                    if (t == null) continue;
                    total++;
                    var cur = Prop(t, "Active");
                    bool curB; try { curB = Convert.ToBoolean(cur); } catch { continue; }
                    if (curB == want) continue;

                    var capT = t;
                    AddRestore?.Invoke(() => { try { SetActive(capT, curB); } catch { } });
                    if (SetActive(t, want)) { perMap++; changed++; }
                }
                if (perMap > 0) maps++;
            }

            Info($"★{(want ? "打开" : "关闭")}转场口：改了 {changed} 条 / 扫到 {total} 条，涉及 {maps} 张图（可还原）");
            if (total == 0)
                Info("⚠ 一条 transits 都没扫到 —— 可能 LocationTable 结构变了，请把这条日志发我");

            // 回读校验（避免"日志说成功、实际没改"）
            int stillOff = 0;
            foreach (var mapId in mapIds)
            {
                object loc = null;
                try { if (getLoc != null) loc = getLoc.Invoke(LocationTable, new object[] { mapId }); } catch { }
                if (loc == null) continue;
                var baseObj = Prop(loc, "Base") ?? loc;
                if (Prop(baseObj, "Transits") is IEnumerable ts)
                    foreach (var t in ts)
                    {
                        var cur = Prop(t, "Active");
                        bool curB; try { curB = Convert.ToBoolean(cur); } catch { continue; }
                        if (curB != want) stillOff++;
                    }
            }
            Info($"回读：仍是 {(want ? "关闭" : "打开")} 状态的转场口 {stillOff} 条" + (stillOff == 0 ? " ✔" : " ⚠"));
        }
        catch (Exception ex) { logger?.Error($"[EventRotator][转场] 失败: {ex.Message}"); }
    }

    private static bool SetActive(object transit, bool value)
    {
        try
        {
            var t = transit.GetType();
            var p = t.GetProperty("Active", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (p != null && p.CanWrite) { p.SetValue(transit, value); return true; }
            var f = t.GetField("Active", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (f != null) { f.SetValue(transit, value); return true; }
            var f2 = t.GetField("<Active>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            if (f2 != null) { f2.SetValue(transit, value); return true; }
            Info("Transit.Active 不可写（属性名或类型不符）");
            return false;
        }
        catch (Exception ex) { Info("改 Transit.Active 失败: " + ex.Message); return false; }
    }
}
