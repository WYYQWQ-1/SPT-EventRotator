using System;
using System.Reflection;
using BepInEx.Bootstrap;
using EFT;
using HarmonyLib;

namespace EventRotator.ScourgeAi;

/// <summary>
/// 让 SAIN **不要**接管本 mod 的两个角色（等价于 Scourge 的
/// <c>ScourgeSainCompatibility.SainBotSpawnExclusionPatch</c>，但只保留"排除"这一件事）。
///
/// <para>原版 Scourge 的 40KB 兼容层还顺带做了 SAIN 层手术；本 mod 不需要那一半，因为战术 AI 全部由
/// 移植过来的 BigBrain 自定义层提供。这里只 postfix SAIN 的
/// <c>SAIN.Components.BotController.BotSpawnController.IsExcluded(WildSpawnType)</c>，
/// 对 13790/13791 返回 true ⇒ SAIN 跳过这两个角色（不再报
/// <c>[bossScourge] does not exist in SAINSettings Dictionary!</c>，也不会来抢层）。</para>
///
/// <para>SAIN 未安装 / 方法签名变了 ⇒ 只 warning，不影响其它功能。</para>
/// </summary>
internal static class SainExclude
{
    private const string SainGuid = "me.sol.sain";
    private const string ControllerTypeName = "SAIN.Components.BotController.BotSpawnController";
    private static bool _done;

    internal static void Install()
    {
        if (_done) return;
        _done = true;
        try
        {
            Assembly asm = null;
            if (Chainloader.PluginInfos != null
                && Chainloader.PluginInfos.TryGetValue(SainGuid, out var info)
                && info?.Instance != null)
            {
                asm = info.Instance.GetType().Assembly;
            }
            if (asm == null)
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { if (a.GetType(ControllerTypeName, false) != null) { asm = a; break; } }
                    catch { }
                }
            }
            if (asm == null)
            {
                ScourgeAiLog.Source?.LogInfo("[Scourge] 未检测到 SAIN ⇒ 无需排除");
                return;
            }

            var type = asm.GetType(ControllerTypeName, false);
            var method = type?.GetMethod("IsExcluded", BindingFlags.Public | BindingFlags.Static,
                                         null, new[] { typeof(WildSpawnType) }, null);
            if (method == null)
            {
                ScourgeAiLog.Source?.LogWarning("[Scourge] ⚠ 找不到 SAIN IsExcluded(WildSpawnType) ⇒ SAIN 仍可能尝试管理本小队");
                return;
            }

            new Harmony("com.kirisame.eventrotator.client.scourgeai.sainexclude")
                .Patch(method, postfix: new HarmonyMethod(
                    typeof(SainExclude).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static)));

            ScourgeAiLog.Source?.LogInfo("[Scourge] ✔ 已让 SAIN 排除 bossScourge/followerScourge（AI 全由本 mod 的 BigBrain 层驱动）");
        }
        catch (Exception ex)
        {
            ScourgeAiLog.Source?.LogWarning("[Scourge] SAIN 排除补丁失败（忽略，不影响生成）: " + ex.Message);
        }
    }

    private static void Postfix(WildSpawnType type, ref bool __result)
    {
        try { if (ScourgeRoles.IsScourge((int)type)) __result = true; } catch { }
    }
}
