using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;

namespace EventRotator.Patches
{
    /// <summary>
    /// ★★ 2026-11-01【ABPS 兼容 · 补内部缓存（不是只补列表）】
    /// <para><b>为什么还要补</b>：现有 <see cref="ThirdPartySpawnGuard"/> 只在每局进图时把第三方出生项补回
    /// <c>Location.Base.BossLocationSpawn</c>；但 ABPS 下次 <c>RebuildMap</c> 会用它**自己的缓存**
    /// <c>_botMapCache</c> 再整表覆盖一次 ⇒ 补进去的又被抹掉（多 boss 活动尤其明显）。</para>
    /// <para><b>修法</b>：直接 Harmony 挂在 ABPS 的
    /// <c>ConfigureInitialData()</c>（启动整表重建）和 <c>RebuildMap(string)</c>（每局重建）的 **postfix**，
    /// 把我们的快照同时并回 <c>_botMapCache[map]</c> 与该图实时列表，做到"缓存里就有"。</para>
    /// <para><b>安全</b>：找不到 ABPS 类型/方法/字段时只打一行日志并跳过，退回"只补列表"的旧行为，
    /// 不影响没有 ABPS 的环境。</para>
    /// </summary>
    internal static class AbpsCachePatch
    {
        private static Action<string> _log;
        private static Type _abpsType;
        private static FieldInfo _botMapCache;
        private static bool _enabled;

        internal static void Enable(Harmony harmony, Action<string> log)
        {
            _log = log;
            try
            {
                _abpsType = AccessTools.TypeByName("BotPlacementSystemServer.Controllers.MapSpawns")
                         ?? RuntimeTypes.Find("BotPlacementSystemServer.Controllers.MapSpawns");
                if (_abpsType == null) { log?.Invoke("[EventRotator][ABPS缓存] 未安装 ABPS ⇒ 跳过（无需补缓存）"); return; }

                _botMapCache = AccessTools.Field(_abpsType, "_botMapCache");
                if (_botMapCache == null)
                {
                    log?.Invoke("[EventRotator][ABPS缓存] ⚠ 找不到 _botMapCache 字段（ABPS 版本不同）⇒ 只保留进图补回");
                    return;
                }

                int patched = 0;
                var mConfigure = AccessTools.Method(_abpsType, "ConfigureInitialData");
                if (mConfigure != null)
                {
                    harmony.Patch(mConfigure, postfix: new HarmonyMethod(typeof(AbpsCachePatch),
                        nameof(AfterConfigure), new[] { typeof(object) }));
                    patched++;
                }
                var mRebuild = AccessTools.Method(_abpsType, "RebuildMap");
                if (mRebuild != null)
                {
                    harmony.Patch(mRebuild, postfix: new HarmonyMethod(typeof(AbpsCachePatch),
                        nameof(AfterRebuild), new[] { typeof(object), typeof(string) }));
                    patched++;
                }
                _enabled = patched > 0;
                log?.Invoke(patched > 0
                    ? $"[EventRotator][ABPS缓存] ✔ 已挂 {patched} 个重建后补缓存点（第三方出生项不会因 ABPS 重建而丢）"
                    : "[EventRotator][ABPS缓存] ⚠ ConfigureInitialData / RebuildMap 都没找到 ⇒ 跳过");
            }
            catch (Exception ex)
            {
                log?.Invoke("[EventRotator][ABPS缓存] ⚠ 挂载失败（不影响其它功能）: " + ex.Message);
            }
        }

        private static void AfterConfigure(object __instance) => MergeAll(__instance, "启动重建");

        private static void AfterRebuild(object __instance, string location)
        {
            if (string.IsNullOrEmpty(location)) { MergeAll(__instance, "整表重建"); return; }
            MergeOne(__instance, location, "每局重建");
        }

        private static void MergeAll(object instance, string why)
        {
            if (!_enabled || instance == null) return;
            try
            {
                int total = 0;
                foreach (var map in ThirdPartySpawnGuard.CachedMaps())
                    total += MergeOne(instance, map, why, quiet: true);
                if (total > 0)
                    _log?.Invoke($"[EventRotator][ABPS缓存] {why}后补回 {total} 条第三方出生项（已写入 ABPS 缓存）");
            }
            catch (Exception ex) { _log?.Invoke("[EventRotator][ABPS缓存] 合并失败: " + ex.Message); }
        }

        private static int MergeOne(object instance, string mapKey, string why, bool quiet = false)
        {
            if (!_enabled || instance == null) return 0;
            try
            {
                var cache = _botMapCache.GetValue(instance) as IDictionary;
                if (cache == null) return 0;
                if (!cache.Contains(mapKey)) return 0;

                var list = cache[mapKey] as IList;
                if (list == null) return 0;

                var added = ThirdPartySpawnGuard.MergeInto(mapKey, list);
                if (added > 0 && !quiet)
                    _log?.Invoke($"[EventRotator][ABPS缓存] {mapKey}：{why}后补回 {added} 条（写进 ABPS 缓存）");
                return added;
            }
            catch (Exception ex)
            {
                if (!quiet) _log?.Invoke("[EventRotator][ABPS缓存] 单图合并失败: " + ex.Message);
                return 0;
            }
        }
    }
}
