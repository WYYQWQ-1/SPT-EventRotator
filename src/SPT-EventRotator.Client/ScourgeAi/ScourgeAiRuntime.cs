using System.Linq;
using System;
using System.Collections.Generic;
using DrakiaXYZ.BigBrain.Brains;
using EFT;

namespace EventRotator.ScourgeAi;

internal static class ScourgeAiRuntime
{
    /// <summary>★ 掉帧修复 A：ScourgeAi 的 3 层注册到我们自己的脑名（ScourgeBrain : ArenaFighterLayersStrategy）。
    /// 用 "ArenaFighter" 会同时吃到 SAIN 无角色门槛预注册的 6 层 ⇒ 每帧多 6 次层判定（掉帧）。
    /// 该常量必须与 <see cref="ScourgeBrain.BrainName"/> 一致。</summary>
    private const string ArenaFighterBrain = EventRotator.ScourgeAi.ScourgeBrain.BrainName;
    private const int CombatPriority = 71;
    private const int RecoveryPriority = 72;
    private const int EscortPriority = 73;

    internal static bool Ready { get; private set; }
    internal static string FailureReason { get; private set; } = "not initialized";

    internal static readonly List<WildSpawnType> Roles = new List<WildSpawnType>
    {
        (WildSpawnType)ScourgeRoles.BossValue,
        (WildSpawnType)ScourgeRoles.FollowerValue
    };

    internal static bool Initialize()
    {
        Ready = false;
        FailureReason = "initialization failed";

        if (!ScourgeAiConfig.Enabled)
        {
            FailureReason = "disabled by configuration";
            return false;
        }

        try
        {
            // 抄自 Scourge：把本小队从 SAIN 手里拿回来（只做"排除"，不做 SAIN 层手术）。
            SainExclude.Install();

            RegisterLayers();
            Ready = true;

            FailureReason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            Ready = false;
            FailureReason = exception.GetType().Name + ": " + exception.Message;
            ScourgeAiLog.Source.LogError(
                $"[Scourge] tactics could not initialize; existing AI configuration is retained: {exception}");
            return false;
        }
    }

    internal static void Disable(string reason)
    {
        Ready = false;
        FailureReason = string.IsNullOrEmpty(reason)
            ? "disabled by compatibility failure"
            : reason;
    }

    private static void RegisterLayers()
    {
        var selected = Roles
            .Where(role => ScourgeRoles.IsScourge((int)role))
            .Distinct()
            .ToList();
        if (selected.Count != 2)
        {
            throw new InvalidOperationException(
                "The Scourge custom-role allowlist did not resolve exactly two roles.");
        }

        var brains = new List<string> { ArenaFighterBrain };
        BrainManager.AddCustomLayer(
            typeof(ScourgeCombatLayer),
            brains,
            CombatPriority,
            selected);
        BrainManager.AddCustomLayer(
            typeof(ScourgeRecoveryLayer),
            brains,
            RecoveryPriority,
            selected);

        var follower = selected
            .Where(role => (int)role == ScourgeRoles.FollowerValue)
            .ToList();
        BrainManager.AddCustomLayer(
            typeof(ScourgeEscortLayer),
            brains,
            EscortPriority,
            follower);
    }
}
