using System;
using System.Text;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;

namespace EventRotator.ScourgeAi
{
    /// <summary>
    /// Keeps one selected follower close to the living boss and gives distant
    /// followers a bounded regroup route. Combat remains available to the other
    /// followers, while native danger, malfunction, and medical layers retain
    /// priority through the layer scheduler.
    /// </summary>
    public sealed class ScourgeEscortLayer : CustomLayer
    {
        public ScourgeEscortLayer(BotOwner bot, int priority) : base(bot, priority) { }

        public override string GetName() => "Scourge Protection Escort";

        public override bool IsActive()
        {
            if (!ScourgeBotContext.Usable(BotOwner)) return false;
            var context = ScourgeBotContext.Get(BotOwner);
            try
            {
                context.Refresh();
                return Time.time >= context.YieldUntil
                    && context.ProtectionActive
                    && !context.IsProtectionAnchor
                    && !context.NativeEmergency
                    && !context.ShouldRecover
                    && (context.NeedsProtectionScreen || context.NeedsProtectionRegroup);
            }
            catch (Exception exception)
            {
                if (Time.time >= context.YieldUntil)
                    ScourgeAiLog.Source.LogError($"Scourge escort yielded: {exception}");
                context.YieldUntil = Time.time + 3f;
                context.Release();
                return false;
            }
        }

        public override CustomLayer.Action GetNextAction() =>
            new CustomLayer.Action(typeof(ScourgeEscortLogic), "Protect and regroup around Scourge boss");

        public override bool IsCurrentActionEnding() => false;

        public override void Stop() => ScourgeBotContext.Get(BotOwner).Release();

        public override void BuildDebugText(StringBuilder text)
        {
            var context = ScourgeBotContext.Get(BotOwner);
            text.Append("Scourge escort: ").Append(context.IsCloseProtectionGuard ? "close" : "regroup")
                .Append(" | distance: ").Append(context.ProtectionDistance.ToString("F1"));
        }
    }
}
