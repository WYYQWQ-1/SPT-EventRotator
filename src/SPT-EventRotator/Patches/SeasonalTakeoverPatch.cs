using System;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Services.Server;

namespace EventRotator.Patches
{
    /// <summary>
    /// ★★★ 2026-10-31【接管原版季节窗 · 真正生效的一刀】
    /// <para><b>问题</b>：原来的接管只是把 <c>SPT_Data/configs/seasonalevents.json</c> 里的
    /// <c>enableSeasonalEventDetection</c> 改成 false，并且（太晚地）把内存里的
    /// <c>SeasonalEventConfig.EnableSeasonalEventDetection</c> 也置 false。
    /// 但 SPT 在**启动早期的 <c>SeasonalEventService.EnableSeasonalEvents()</c>** 里就已经完成"按现实日期开节日"，
    /// 这一步早于本 mod 的 OnLoad ⇒ 磁盘/内存那两下对**当前进程**无效，
    /// 于是官方万圣节/圣诞照样生效（实测：别人用本 mod 时，非万圣节活动期间野外仍刷扎里亚奇等节日首领）。</para>
    /// <para><b>修法</b>：直接 Harmony **prefix 掉 <c>EnableSeasonalEvents()</c>** —— 接管开启时让它整段不执行。
    /// 我们自己的季节强推走的是 <c>ForceSeasonalEvent()</c>（另一个方法），不受影响。</para>
    /// </summary>
    internal static class SeasonalTakeoverPatch
    {
        internal static bool Active;
        private static Action<string> _log;
        private static bool _logged;

        internal static void Enable(Harmony harmony, Action<string> log)
        {
            _log = log;
            try
            {
                var t = RuntimeTypes.Find("SPTarkov.Server.Core.Services.Server.SeasonalEventService");
                if (t == null) { log?.Invoke("[EventRotator][季节窗] ⚠ 找不到 SeasonalEventService ⇒ 无法拦截原生节日（磁盘接管仍在，需重启生效）"); return; }
                var m = AccessTools.Method(t, "EnableSeasonalEvents");
                if (m == null) { log?.Invoke("[EventRotator][季节窗] ⚠ 找不到 EnableSeasonalEvents ⇒ 跳过拦截"); return; }
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(SeasonalTakeoverPatch).GetMethod(nameof(Prefix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));

                // ★ 第二道闸：SPT 往地图塞"季节首领"的唯一入口（AddEventBossesToMaps）。
                //   即使 EnableSeasonalEvents 在 mod 加载之前就跑过，这里也能挡住"非万圣节期间刷扎里亚奇"。
                var m2 = AccessTools.Method(t, "AddEventBossesToMaps");
                if (m2 != null)
                    harmony.Patch(m2, prefix: new HarmonyMethod(typeof(SeasonalTakeoverPatch).GetMethod(nameof(PrefixBosses),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
            }
            catch (Exception ex) { log?.Invoke("[EventRotator][季节窗] ⚠ 拦截原生节日失败（不影响其它功能）: " + ex.Message); }
        }

        /// <summary>我们的引擎里当前是否有万圣节族活动。（有 ⇒ 允许 SPT 塞节日首领，那是我们自己要的）</summary>
        private static bool HalloweenMine()
        {
            try
            {
                return EventEngine.Actives != null && System.Linq.Enumerable.Any(EventEngine.Actives, a =>
                    string.Equals(a.Family, "halloween", StringComparison.OrdinalIgnoreCase)
                    || (a.Id ?? "").IndexOf("halloween", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return false; }
        }

        private static bool PrefixBosses()
        {
            try
            {
                if (!Active) return true;
                if (HalloweenMine()) return true;      // 我们自己要开万圣节 ⇒ 放行
                if (!_loggedBoss)
                {
                    _loggedBoss = true;
                    _log?.Invoke("[EventRotator][季节窗] ✔ 已拦截 AddEventBossesToMaps（接管开启且当前没有万圣节活动 ⇒ 不往地图塞节日首领）");
                }
                return false;
            }
            catch { return true; }
        }

        private static bool _loggedBoss;

        private static bool Prefix()
        {
            try
            {
                if (!Active) return true;
                if (!_logged)
                {
                    _logged = true;
                    _log?.Invoke("[EventRotator][季节窗] ✔ 已拦截 SPT 原生 EnableSeasonalEvents（接管开启：官方按日期开节日整段跳过；"
                               + "本 mod 的季节强推走 ForceSeasonalEvent，不受影响）");
                }
                return false;
            }
            catch { return true; }
        }
    }
}
