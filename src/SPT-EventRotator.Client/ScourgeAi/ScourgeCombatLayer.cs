using System;
using System.Text;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;

namespace EventRotator.ScourgeAi
{
    public sealed class ScourgeCombatLayer : CustomLayer
    {
        public ScourgeCombatLayer(BotOwner bot, int priority) : base(bot, priority) { }
        public override string GetName() => "Scourge Squad Combat";
        public override bool IsActive()
        {
            if (!ScourgeBotContext.Usable(BotOwner)) return false;
            var context = ScourgeBotContext.Get(BotOwner);
            try
            {
                if (context.ContinuingThrow) return true;
                context.Refresh();
                return Time.time >= context.YieldUntil && context.HasContact && !context.NativeEmergency
                    && !context.ShouldRecover && context.Tactic != ScourgeTactic.None;
            }
            catch (Exception exception)
            {
                if (Time.time >= context.YieldUntil) ScourgeAiLog.Source.LogError($"Scourge combat yielded: {exception}");
                context.YieldUntil = Time.time + 5f;
                context.Release();
                return false;
            }
        }
        public override CustomLayer.Action GetNextAction() => new CustomLayer.Action(typeof(ScourgeCombatLogic), "Squad pressure and overwatch");
        public override bool IsCurrentActionEnding() => false;
        public override void Stop() => ScourgeBotContext.Get(BotOwner).Release();
        public override void BuildDebugText(StringBuilder text)
        {
            var context = ScourgeBotContext.Get(BotOwner);
            text.Append("Scourge: ").Append(context.Tactic).Append(" | observed: ").Append(context.Visible)
                .Append(" | attack: ").Append(context.CanFire);
        }
    }
}


