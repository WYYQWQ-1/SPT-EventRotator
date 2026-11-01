using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace EventRotator.Patches
{
    /// <summary>
    /// ★★ 2026-10-31【ABPS 兼容 · 第三方自定义 AI 第一局不刷】
    /// <para><b>现象</b>：黑狐(BlackDiv) / 灾厄 / 其它自定义 bot 在**重启后的第一局**不刷，第二局开始正常。</para>
    /// <para><b>根因</b>（补丁 README + 反编译同源）：ABPS 的 <c>MapSpawns</c> 在启动/配置变更/每局结束时，
    /// 会把 <c>Location.Base.BossLocationSpawn</c> **整表替换成它自己的缓存**
    /// ⇒ 数据文件里注入的第三方出生项被清空；部分模组在"上一局结束/下一局开始"又注入一次，
    /// 所以第二局看起来正常。</para>
    /// <para><b>修法</b>（抄社区全局补丁的思路，但**不编译 ABPS**）：
    /// 在 ABPS 清表**之前**（TypePriority = PostLoad + 69419，比 ABPS 的 +69420 早 1）把第三方出生项快照下来；
    /// 之后每次进图（我们已有的 hook）把缺失的条目补回该图的 <c>BossLocationSpawn</c>。</para>
    /// <para><b>第三方判定</b>：名字不是原版 <c>WildSpawnType</c> 枚举成员，并且（数字 ≥ 1000 或 <c>BotTable.Types</c> 里有它）。
    /// 原版 bot 与 ABPS 自己配置的 bot 不碰。</para>
    /// </summary>
    [Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 69419)]
    public class ThirdPartySpawnGuard(IServiceProvider services, SPTarkov.Common.Models.Logging.ISptLogger<ThirdPartySpawnGuard> logger) : IOnLoad
    {
        private static IServiceProvider _sp;
        private static Action<string> _info;
        private static Action<string> _warn;

        private static void Info(string m) { try { _info?.Invoke(m); } catch { } }
        private static void Warn(string m) { try { _warn?.Invoke(m); } catch { } }

        private static readonly Dictionary<string, List<string>> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static Type _entryType;
        private static bool _captured;

        public Task OnLoadAsync(System.Threading.CancellationToken token)
        {
            _sp = services;
            _info = m => logger.Info(m); _warn = m => logger.Warning(m);
            try { Capture(); }
            catch { }
            return Task.CompletedTask;
        }

        private static Type EntryType
            => _entryType ??= RuntimeTypes.Find("SPTarkov.Server.Core.Models.Eft.Common.BossLocationSpawn")
               ?? RuntimeTypes.Find("SPTarkov.Server.Core.Models.Eft.Common.LocationBase+BossLocationSpawn");

        private static Type LocationTableType
            => RuntimeTypes.Find("SPTarkov.Server.Core.Models.Spt.Tables.LocationTable");

        private static Type BotTableType
            => RuntimeTypes.Find("SPTarkov.Server.Core.Models.Spt.Tables.BotTable");

        private static object Resolve(Type t)
        {
            try
            {
                if (t == null) return null;
                var root = _sp;
                if (root == null) return null;
                return root.GetService(t);
            }
            catch { return null; }
        }

        private static object Prop(object o, string name)
        {
            try { return o?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o); }
            catch { return null; }
        }

        /// <summary>★ 兜底入口：Preload 阶段（QuestLinePreload）就试着快照一次。
        /// 万一 ABPS 的清表发生在 PostLoad 之前，这里的快照仍然能看到数据库里的第三方条目。</summary>
        public static void CaptureNow(IServiceProvider sp, Action<string> info)
        {
            try
            {
                if (_captured) return;
                if (sp != null) _sp = sp;
                if (info != null) _info = info;
                Capture();
            }
            catch { }
        }

        /// <summary>快照：遍历 LocationTable，把第三方出生项序列化保存。</summary>
        private static void Capture()
        {
            if (_captured) return;
            var lt = Resolve(LocationTableType);
            if (lt == null) { Warn("[EventRotator][第三方出生项] ⚠ 拿不到 LocationTable ⇒ 跳过快照"); return; }
            var botTable = Resolve(BotTableType);
            var botTypes = (Prop(botTable, "Types") as IDictionary);

            // ★ SPT 4.1.6 的实际 API：LocationTable.GetDictionary()（无参）返回 IDictionary。
            //   之前试的是属性名 Locations/AllLocations ⇒ 一个都没命中 ⇒ "结构不识别"。
            object locations = null;
            try
            {
                var m = lt.GetType().GetMethod("GetDictionary", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (m != null) locations = m.Invoke(lt, null);
            }
            catch { }
            if (locations == null) locations = Prop(lt, "Locations") ?? Prop(lt, "AllLocations");
            if (locations is not IDictionary dict) { Warn("[EventRotator][第三方出生项] ⚠ LocationTable 结构不识别 ⇒ 跳过快照"); return; }

            int total = 0;
            var names = new List<string>();
            foreach (DictionaryEntry kv in dict)
            {
                var mapKey = kv.Key?.ToString();
                if (string.IsNullOrEmpty(mapKey)) continue;
                var list = Prop(Prop(kv.Value, "Base"), "BossLocationSpawn") as IList;
                if (list == null || list.Count == 0) continue;
                var keep = new List<string>();
                foreach (var it in list)
                {
                    if (it == null) continue;
                    var boss = Prop(it, "BossName")?.ToString();
                    var escort = Prop(it, "BossEscortType")?.ToString();
                    if (!IsThirdParty(boss, botTypes) && !IsThirdParty(escort, botTypes)) continue;
                    try { keep.Add(JsonSerializer.Serialize(it, it.GetType())); } catch { }
                }
                if (keep.Count == 0) continue;
                _cache[mapKey] = keep;
                total += keep.Count;
                foreach (var j in keep) { try { var o = JsonSerializer.Deserialize(j, EntryType); var nm = Prop(o, "BossName")?.ToString(); if (!string.IsNullOrEmpty(nm) && !names.Contains(nm)) names.Add(nm); } catch { } }
            }
            // ★ 只有真的抓到东西才算完成：Preload 阶段黑狐等 mod 还没注入 ⇒ 不能就此锁死，
            //   交给 PostLoad+69419 那次再补（那次在其它 mod 的 PostDbLoad 之后）。
            if (total > 0) _captured = true;
            if (total > 0)
                Info($"[EventRotator][第三方出生项] ✔ 已快照 {total} 条（{string.Join(",", _cache.Keys)}）"
                                    + (names.Count > 0 ? $"｜自定义 bot：{string.Join(",", names.Take(12))}" : "")
                                    + "：ABPS 清表后由本 mod 每局补回");
            else
                Info("[EventRotator][第三方出生项] 本次快照为空（没有第三方自定义 bot 出生项，或 ABPS 已先清表）");
        }

        private static bool IsThirdParty(string name, IDictionary botTypes)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.Equals("pmcUSEC", StringComparison.OrdinalIgnoreCase) || name.Equals("pmcBEAR", StringComparison.OrdinalIgnoreCase)) return false;
            // 数字型（MoreBotsAPI 建议的 1000+）⇒ 第三方
            if (int.TryParse(name, out var n)) return n >= 1000;
            // 原版枚举成员 ⇒ 不是第三方
            // ★ SPT 4.1.6 的实际位置是 Eft.Common（`[自定义bot类型] 服务端枚举 SPTarkov.Server.Core.Models.Eft.Common.WildSpawnType`）；
            //   旧写法查 Enums 命名空间 ⇒ 找不到类型 ⇒ 原版 boss（bossKilla/bossBully…）被误判成"第三方"。
            var wt = RuntimeTypes.Find("SPTarkov.Server.Core.Models.Eft.Common.WildSpawnType")
                  ?? RuntimeTypes.Find("SPTarkov.Server.Core.Models.Enums.WildSpawnType");
            if (wt != null)
            {
                try { if (Enum.TryParse(wt, name, true, out _)) return false; } catch { }
            }
            // BotTable 里有 ⇒ 第三方自定义
            if (botTypes != null && botTypes.Contains(name.ToLowerInvariant())) return true;
            return false;
        }

        /// <summary>进图时调用：把快照里这张图的第三方条目补回（缺哪个补哪个，不重复加）。</summary>
        /// <summary>已快照的地图 key（供 ABPS 缓存补丁遍历）。</summary>
        internal static IEnumerable<string> CachedMaps() => _cache.Keys.ToArray();

        /// <summary>把快照里该图的条目并进任意 IList（返回实际新增数）。ABPS 缓存补丁复用它。</summary>
        internal static int MergeInto(string mapKey, IList list)
        {
            if (string.IsNullOrEmpty(mapKey) || list == null) return 0;
            if (!_cache.TryGetValue(mapKey, out var saved) || saved.Count == 0) return 0;
            var t = EntryType;
            if (t == null) return 0;
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in list) { if (it != null) { var k = KeyOf(it); if (k != null) have.Add(k); } }
            int added = 0;
            foreach (var json in saved)
            {
                object obj;
                try { obj = JsonSerializer.Deserialize(json, t); } catch { continue; }
                var k = KeyOf(obj);
                if (k == null || !have.Add(k)) continue;
                list.Add(obj); added++;
            }
            return added;
        }

        public static void Ensure(string mapKey, IList list)
        {
            try
            {
                if (string.IsNullOrEmpty(mapKey) || list == null || !_cache.TryGetValue(mapKey, out var saved) || saved.Count == 0) return;
                var t = EntryType;
                if (t == null) return;

                var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var it in list)
                {
                    if (it == null) continue;
                    var k = KeyOf(it);
                    if (k != null) have.Add(k);
                }

                int added = 0;
                foreach (var json in saved)
                {
                    object obj;
                    try { obj = JsonSerializer.Deserialize(json, t); } catch { continue; }
                    var k = KeyOf(obj);
                    if (k == null || !have.Add(k)) continue;
                    list.Add(obj);
                    added++;
                }
                if (added > 0)
                    Warn($"[EventRotator][第三方出生项] {mapKey}：ABPS 清表后补回 {added} 条第三方出生项（{string.Join(",", saved.Select(s => ShortName(s, t)))}）");
            }
            catch (Exception ex) { Warn("[EventRotator][第三方出生项] 补回失败: " + ex.Message); }
        }

        private static string KeyOf(object entry)
        {
            try
            {
                var boss = Prop(entry, "BossName")?.ToString();
                var zone = Prop(entry, "BossZone")?.ToString();
                return (boss ?? "") + "|" + (zone ?? "");
            }
            catch { return null; }
        }

        private static string ShortName(string json, Type t)
        {
            try
            {
                var o = JsonSerializer.Deserialize(json, t);
                return Prop(o, "BossName")?.ToString() ?? "?";
            }
            catch { return "?"; }
        }
    }
}
