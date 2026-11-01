using System;
using System.Runtime.CompilerServices;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace EventRotator.ScourgeAi
{
    public sealed class ScourgeRecoveryLayer : CustomLayer
    {
        public ScourgeRecoveryLayer(BotOwner bot, int priority) : base(bot, priority) { }
        public override string GetName() => "Scourge Cover Reload";
        public override bool IsActive()
        {
            if (!ScourgeBotContext.Usable(BotOwner)) return false;
            var context = ScourgeBotContext.Get(BotOwner);
            try
            {
                context.Refresh();
                var reload = BotOwner.WeaponManager.Reload;
                var reloadInProgress = reload != null && reload.Reloading;
                var reloadPending = ScourgeRecoveryLogic.IsReloadPending(BotOwner);
                if (Time.time < context.YieldUntil
                    || (!reloadInProgress && Time.time < context.NextRecoveryTime)) return false;
                return context.HasContact && !context.NativeEmergency && (context.ShouldRecover || reloadPending);
            }
            catch (Exception exception)
            {
                ScourgeAiLog.Source.LogError($"Scourge recovery yielded: {exception}");
                context.YieldUntil = Time.time + 5f;
                return false;
            }
        }
        public override CustomLayer.Action GetNextAction() => new CustomLayer.Action(typeof(ScourgeRecoveryLogic), "Find cover and reload");
        public override bool IsCurrentActionEnding() => false;
    }

    public sealed class ScourgeRecoveryLogic : CustomLogic
    {
        private sealed class PendingReload { }

        private static readonly ConditionalWeakTable<BotOwner, PendingReload> PendingReloads
            = new ConditionalWeakTable<BotOwner, PendingReload>();

        private const float CoverSearchSeconds = 3f;
        private const float ReloadStartWaitSeconds = 2f;
        private const float ReloadCompletionWaitSeconds = 20f;
        private const float RecoveryCooldownSeconds = 4f;
        private const float BreakAbortCooldownSeconds = 3f;
        private const float BreakAbortYieldSeconds = 1.5f;

        private readonly ScourgeBotContext _context;
        private Vector3? _cover;
        private float _deadline;
        private float _reloadStartDeadline;
        private float _reloadCompletionDeadline;
        private bool _breakReload;
        private bool _reloadRequested;
        private bool _reloadStarted;
        private bool _reloadStartLogged;
        private bool _reloadCompletionTimedOut;
        private bool _finished;
        private int _protectionRevision;

        public ScourgeRecoveryLogic(BotOwner bot) : base(bot) { _context = ScourgeBotContext.Get(bot); }

        internal static bool IsReloadPending(BotOwner bot) => PendingReloads.TryGetValue(bot, out _);

        public override void Start()
        {
            var now = Time.time;
            var reload = BotOwner.WeaponManager.Reload;
            _breakReload = _context.ReloadForBreak;
            _context.Release();
            _cover = null;
            _deadline = now + CoverSearchSeconds;
            _reloadRequested = reload != null && reload.Reloading;
            _reloadStarted = _reloadRequested;
            _reloadStartLogged = false;
            _reloadCompletionTimedOut = false;
            _finished = false;
            _protectionRevision = _context.ProtectionRevision;
            _reloadStartDeadline = now + ReloadStartWaitSeconds;
            _reloadCompletionDeadline = _reloadStarted ? now + ReloadCompletionWaitSeconds : 0f;
            BotOwner.ShootData.EndShoot();
            BotOwner.AimingManager.CurrentAiming.LoseTarget();
            BotOwner.PatrollingData.Pause();
            BotOwner.StopMove();
            if (_reloadStarted) LogReloadStarted(now);
            if (!_breakReload && !_reloadStarted && reload != null
                && !ScourgeNavigation.IsCovered(BotOwner.Position, _context.KnownPosition)
                && ScourgeNavigation.TryDestination(BotOwner, _context.KnownPosition, ScourgeTactic.Withdraw,
                    true, _context.Members, out var cover, null, 3f,
                    _context.ProtectionActive ? _context.ProtectionCenter : (Vector3?)null,
                    _context.ProtectionRouteLeashRadius,
                    _context.IsProtectionAnchor))
            {
                BotOwner.SetPose(1f);
                BotOwner.SetTargetMoveSpeed(1f);
                BotOwner.Sprint(false);
                if (BotOwner.GoToPoint(cover, true, 1f, true, true) == NavMeshPathStatus.PathComplete) _cover = cover;
            }
        }
        public override void Stop()
        {
            _finished = true;
            PendingReloads.Remove(BotOwner);
            _cover = null;
            _context.Release();
            BotOwner.StopMove();
            BotOwner.Sprint(false);
            BotOwner.ShootData.EndShoot();
            BotOwner.AimingManager.CurrentAiming.LoseTarget();
            BotOwner.SetPose(1f);
            BotOwner.PatrollingData.Unpause();
        }
        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                if (_finished || !ScourgeBotContext.Usable(BotOwner)) return;
                _context.Refresh();
                if (_context.NativeEmergency) return;
                BotOwner.ShootData.EndShoot();
                BotOwner.DoorOpener.UpdateDoorInteractionStatus();
                BotOwner.BewarePlantedMine.Update();
                BotOwner.BotAvoidDangerPlaces.Update();
                if (_protectionRevision != _context.ProtectionRevision)
                {
                    _protectionRevision = _context.ProtectionRevision;
                    BotOwner.StopMove();
                    _cover = null;
                    _deadline = Time.time;
                }

                var reload = BotOwner.WeaponManager.Reload;
                if (reload == null)
                {
                    FailAndStop("reload unavailable", RecoveryCooldownSeconds, false);
                    return;
                }
                if (!_reloadStarted && reload.Reloading)
                {
                    _reloadRequested = true;
                    LogReloadStarted(Time.time);
                }
                if (_reloadStarted)
                {
                    if (reload.Reloading)
                    {
                        BotOwner.StopMove();
                        if (!_reloadCompletionTimedOut && Time.time >= _reloadCompletionDeadline)
                        {
                            _reloadCompletionTimedOut = true;
                            FailAndStop("completion timeout; native fallback", RecoveryCooldownSeconds, true);
                        }
                        return;
                    }
                    FinishReload();
                    return;
                }
                if (_reloadRequested)
                {
                    if (Time.time < _reloadStartDeadline) return;
                    FailAndStop("start timeout; native fallback", RecoveryCooldownSeconds, false);
                    return;
                }
                if (_cover.HasValue && Time.time < _deadline && (BotOwner.Position - _cover.Value).sqrMagnitude > 2.25f)
                {
                    BotOwner.Steering.LookToMovingDirection();
                    return;
                }
                _cover = null;
                BotOwner.StopMove();
                var covered = ScourgeNavigation.IsCovered(BotOwner.Position, _context.KnownPosition);
                if (covered) BotOwner.SetPose(0f);
                else BotOwner.SetPose(1f);
                BotOwner.Steering.LookToPoint(_context.KnownPosition + Vector3.up);

                if (_breakReload && !CanStartBreakReload())
                {
                    _context.NextRecoveryTime = Time.time + BreakAbortCooldownSeconds;
                    _context.YieldUntil = Time.time + BreakAbortYieldSeconds;
                    LogReloadFailure("contact unsafe before native start");
                    Stop();
                    return;
                }

                _reloadRequested = true;
                PendingReloads.GetValue(BotOwner, _ => new PendingReload());
                _reloadStartDeadline = Time.time + ReloadStartWaitSeconds;
                if (!reload.TryReload())
                {
                    FailAndStop("native request rejected", RecoveryCooldownSeconds, false);
                    return;
                }
                if (reload.Reloading) LogReloadStarted(Time.time);
            }
            catch (Exception exception)
            {
                ScourgeAiLog.Source.LogError($"Scourge reload yielded: {exception}");
                LogReloadFailure("exception; native fallback");
                _context.NextRecoveryTime = Time.time + RecoveryCooldownSeconds;
                Stop();
            }
        }

        private bool CanStartBreakReload()
        {
            var enemy = BotOwner.Memory?.GoalEnemy;
            return enemy != null && enemy.ProfileId == _context.TargetId && !enemy.IsVisible
                && ScourgeNavigation.IsCovered(BotOwner.Position, _context.KnownPosition)
                && _context.ReloadForBreak;
        }

        private void LogReloadStarted(float now)
        {
            if (_reloadStartLogged) return;
            _reloadStarted = true;
            _reloadCompletionDeadline = now + ReloadCompletionWaitSeconds;
            _reloadStartLogged = true;
            PendingReloads.Remove(BotOwner);
            if (ScourgeAiConfig.Debug.Value)
                ScourgeAiLog.Source.LogInfo($"Scourge {BotOwner.ProfileId}: {ReloadKind} reload started.");
        }

        private void FinishReload()
        {
            _finished = true;
            _context.NextRecoveryTime = Time.time + RecoveryCooldownSeconds;
            if (!_reloadCompletionTimedOut) LogReloadCompleted();
        }

        private void FailAndStop(string reason, float cooldown, bool yieldCombat)
        {
            _context.NextRecoveryTime = Time.time + cooldown;
            if (yieldCombat) _context.YieldUntil = Time.time + BreakAbortYieldSeconds;
            LogReloadFailure(reason);
            Stop();
        }

        private void LogReloadCompleted()
        {
            if (ScourgeAiConfig.Debug.Value)
                ScourgeAiLog.Source.LogInfo($"Scourge {BotOwner.ProfileId}: {ReloadKind} reload completed.");
        }

        private void LogReloadFailure(string reason)
        {
            if (ScourgeAiConfig.Debug.Value)
                ScourgeAiLog.Source.LogInfo($"Scourge {BotOwner.ProfileId}: {ReloadKind} reload failed ({reason}).");
        }

        private string ReloadKind => _breakReload ? "contact break" : "low ammo";
    }
}


