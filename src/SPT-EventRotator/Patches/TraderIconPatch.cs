using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;

namespace EventRotator.Patches;

/// <summary>
/// ★★★ **商人万圣节头像 · 延迟补做**（2026-10-16 实机证实的问题，独立新文件，不碰任何既有补丁）。
///
/// ── 为什么需要它（真服务端日志实测）────────────────────────────────────────────
///   同一个服务端进程内：
///     · **12:40:34 = mod `OnLoad` 套用** → `TradersTable` 里**只有 1 个商人（Fence）**
///       ⇒ 日志「★商人万圣节头像：改 1 个、已是目标值跳过 0 个、找不到 7 个」
///     · **12:42:55 = 用户 F12 换活动触发的第二次套用** → 同一张表里**已能找到 Mechanic**
///   ⇒ **SPT 的 `TradersTable` 在 `OnLoad` 时刻还没装满**；两次干净重启（12:40:34 / 12:55:32）
///     都是「改 1 个、找不到 7 个」，逐字一致 ⇒ 稳定复现，**不是偶发**。
///
/// ── 为什么挂 `TraderHelper.GetAllTraders` ─────────────────────────────────────
///   它是 `/client/trading/api/traderSettings` 的**唯一**构建点
///   （`Callbacks/TraderCallbacks.cs:32-35` → `traderHelper.GetAllTraders(sessionID)`），
///   而客户端**正是**从这个响应里拿 `AvatarURL` 去 HTTP 取头像
///   （`Assembly-CSharp.decompiled.cs:554662-554665` `[JsonProperty("avatar")] AvatarURL`；
///    `:743811-743819` → `:661646-661649` `LoadTextureWithCache(_backendUrls.Trading, url)`）。
///   ⇒ 在**这一刹那**补做：客户端**正要拿头像**，且商人表**必然已装满**。位置上是**最贴切**的钩子。
///
/// ── 全程可降级（绝不让服务端崩）──────────────────────────────────────────────
///   · 找不到 `TraderHelper` / `GetAllTraders` ⇒ 只 warning，**不挂补丁**，其余一切照旧。
///   · prefix 内部整体 try/catch，异常只 warning（且**只提示一次**），**绝不影响 traderSettings 正常返回**。
///   · 未启用（没有万圣节活动声明 `eventTraderIcons`）⇒ `WantTraderIcons == false` ⇒ **一次 bool 检查就返回**。
/// </summary>
internal static class TraderIconPatch
{
    private static ISptLogger<EventRotatorServer> _log;
    private static bool _warned;

    /// <summary>挂补丁。任何失败都只 warning（不影响 mod 其它功能）。</summary>
    public static void Enable(Harmony harmony, ISptLogger<EventRotatorServer> log)
    {
        _log = log;
        try
        {
            var helperType = RuntimeTypes.Find("SPTarkov.Server.Core.Helpers.Traders.TraderHelper");
            if (helperType == null)
            {
                log?.Warning("[EventRotator][商人头像] 找不到 TraderHelper ⇒ 头像延迟补做未挂载（头像只在初期那一轮尝试）");
                return;
            }
            // ★★★ 2026-10-30【修】原来只挂了一个重载（按 MongoId 精确定位），实机日志显示
            //   "已挂载"但 prefix **从未触发** ⇒ 客户端走的可能是另一个重载/或参数类型来自另一份程序集。
            //   现在把名叫 GetAllTraders 的**所有方法**都挂上，并逐条记日志，确保至少命中一条。
            var targets = new List<System.Reflection.MethodInfo>();
            foreach (var m in helperType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                                      | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
            {
                if (string.Equals(m.Name, "GetAllTraders", StringComparison.Ordinal)) targets.Add(m);
            }
            if (targets.Count == 0)
            {
                log?.Warning("[EventRotator][商人头像] 找不到 TraderHelper.GetAllTraders ⇒ 头像延迟补做未挂载");
                return;
            }
            int patched = 0;
            foreach (var t0 in targets)
            {
                try
                {
                    harmony.Patch(t0, new HarmonyMethod(typeof(TraderIconPatch), nameof(Prefix)), null, null, null, null);
                    patched++;
                    var ps = t0.GetParameters();
                    log?.Info($"[EventRotator][商人头像] 延迟补做已挂载 → GetAllTraders/{ps.Length}参"
                            + $"({string.Join(",", ps.Select(x => x.ParameterType.Name))})");
                }
                catch (Exception ex2)
                {
                    log?.Warning("[EventRotator][商人头像] 挂载某个 GetAllTraders 重载失败（继续）: " + ex2.Message);
                }
            }
            log?.Info($"[EventRotator][商人头像] 延迟补做共挂载 {patched}/{targets.Count} 个 GetAllTraders 重载");
        }
        catch (Exception ex)
        {
            log?.Warning("[EventRotator][商人头像] 延迟补做挂载失败（已忽略）: " + ex.Message);
        }
    }

    /// <summary>
    /// `GetAllTraders` 之前：若本次有万圣节活动要求换头像、且还没换成 ⇒ 此刻补做。
    /// 永远返回 true（**绝不拦截**原始方法）。
    /// </summary>
    private static bool _fired;

    private static bool Prefix()
    {
        try
        {
            if (!ExtraDimensions.WantTraderIcons) return true;      // 没启用 ⇒ 零开销
            if (!_fired) { _fired = true; _log?.Info("[EventRotator][商人头像] ★延迟补做已触发（GetAllTraders 被调用）"); }
            ExtraDimensions.ReapplyTraderIconsIfNeeded();
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _log?.Warning("[EventRotator][商人头像] 延迟补做异常（已忽略，不影响 traderSettings，不再重复提示）: " + ex.Message);
            }
        }
        return true;
    }
}
