using System;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace EventRotator.ScourgeAi
{
    public sealed class ScourgeEscortLogic : CustomLogic
    {
        private readonly ScourgeBotContext _context;
        private Vector3? _destination;
        private Vector3 _progressPosition;
        private float _progressTime;
        private float _moveDeadline;
        private float _nextPathAttempt;
        private int _protectionRevision;
        private bool _finished;

        public ScourgeEscortLogic(BotOwner bot) : base(bot)
        {
            _context = ScourgeBotContext.Get(bot);
        }

        public override void Start()
        {
            _destination = null;
            _nextPathAttempt = Time.time;
            _protectionRevision = _context.ProtectionRevision;
            _finished = false;
            BotOwner.PatrollingData.Pause();
            BotOwner.StopMove();
        }

        public override void Stop()
        {
            _finished = true;
            _destination = null;
            _context.Release();
            BotOwner.StopMove();
            BotOwner.Sprint(false);
            BotOwner.ShootData.EndShoot();
            BotOwner.AimingManager.CurrentAiming.LoseTarget();
            BotOwner.PatrollingData.Unpause();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                if (_finished || !ScourgeBotContext.Usable(BotOwner)) return;
                Tick();
            }
            catch (Exception exception)
            {
                ScourgeAiLog.Source.LogError($"Scourge escort action yielded: {exception}");
                _context.YieldUntil = Time.time + 3f;
                Stop();
            }
        }

        private void Tick()
        {
            _context.Refresh();
            BotOwner.DoorOpener.UpdateDoorInteractionStatus();
            BotOwner.BewarePlantedMine.Update();
            BotOwner.BotAvoidDangerPlaces.Update();
            // Native emergency states are checked before every escort decision.
            // The higher-priority native layer can take over on the next brain
            // evaluation without waiting for a route timeout.
            if (_context.NativeEmergency || _context.ShouldRecover || !_context.ProtectionActive
                || _context.IsProtectionAnchor)
            {
                Stop();
                return;
            }
            if (Time.time < _context.YieldUntil)
            {
                ScreenThreat();
                return;
            }

            var shouldMove = _context.NeedsProtectionEscort
                || _context.NeedsProtectionRegroup
                || (_context.IsCloseProtectionGuard
                    && _context.ProtectionDistance > _context.ProtectionSettings.CloseGuardRadius);
            var protectionChanged = _protectionRevision != _context.ProtectionRevision;
            if (protectionChanged)
            {
                _protectionRevision = _context.ProtectionRevision;
                EndMove("protection anchor or close guard changed");
            }
            if (protectionChanged && !shouldMove && !_destination.HasValue)
                _context.MarkProtectionRevisionObserved();

            if (_destination.HasValue)
            {
                if (Vector3.Distance(BotOwner.Position, _destination.Value)
                    <= ScourgeManeuverTiming.CompletionRadius)
                {
                    EndMove("completed");
                    // Consume the revision only after the escort reaches its
                    // bounded destination. A route that merely started must
                    // remain layer-eligible when an ordinary guard is still
                    // inside the leash and its only duty trigger is the
                    // boss-movement revision.
                    _context.MarkProtectionRevisionObserved();
                }
                else if (Time.time > _moveDeadline
                    || (Time.time - _progressTime > 2f
                        && (BotOwner.Position - _progressPosition).sqrMagnitude < 0.25f))
                {
                    EndMove("stalled");
                    _nextPathAttempt = Time.time + ScourgeManeuverTiming.PathRetryCooldownSeconds;
                    _context.YieldUntil = Time.time + 1.5f;
                    return;
                }
                else
                {
                    if ((BotOwner.Position - _progressPosition).sqrMagnitude >= 0.25f)
                    {
                        _progressPosition = BotOwner.Position;
                        _progressTime = Time.time;
                    }
                    BotOwner.Steering.LookToMovingDirection();
                }
            }

            if (shouldMove && !_destination.HasValue && Time.time >= _nextPathAttempt)
                BeginMove();

            if (!_destination.HasValue)
            {
                BotOwner.StopMove();
                BotOwner.SetPose(1f);
                ScreenThreat();
            }
        }

        private void BeginMove()
        {
            _nextPathAttempt = Time.time + ScourgeManeuverTiming.PathRetryCooldownSeconds;
            var center = _context.ProtectionCenter;
            if (!ScourgeNavigation.TryDestination(
                    BotOwner,
                    center,
                    ScourgeTactic.Search,
                    false,
                    _context.Members,
                    out var point,
                    out var pathDistance,
                    null,
                    0f,
                    center,
                    _context.ProtectionRouteLeashRadius,
                    false))
            {
                // A failed escort probe is bounded. Native handling keeps
                // running and the next probe is retried after a short cooldown.
                _context.YieldUntil = Time.time + 1.5f;
                ScreenThreat();
                return;
            }

            BotOwner.SetPose(1f);
            BotOwner.BotLay.GetUp(true);
            BotOwner.SetTargetMoveSpeed(0.85f);
            BotOwner.Sprint(false);
            if (BotOwner.GoToPoint(point, true, 1f, true, true) != NavMeshPathStatus.PathComplete)
            {
                BotOwner.StopMove();
                _context.YieldUntil = Time.time + 1.5f;
                ScreenThreat();
                return;
            }

            _destination = point;
            _progressPosition = BotOwner.Position;
            _progressTime = Time.time;
            _moveDeadline = Time.time + Mathf.Clamp(pathDistance / 0.85f + 5f, 8f, 45f);
            if (ScourgeAiConfig.Debug?.Value == true)
                ScourgeAiLog.Source.LogInfo(
                    $"Scourge {BotOwner.ProfileId}: escort route start, "
                    + $"distance={_context.ProtectionDistance:F1}, path={pathDistance:F1}.");
        }

        private void EndMove(string reason)
        {
            if (!_destination.HasValue) return;
            _destination = null;
            BotOwner.StopMove();
            if (ScourgeAiConfig.Debug?.Value == true)
                ScourgeAiLog.Source.LogInfo(
                    $"Scourge {BotOwner.ProfileId}: escort route {reason}.");
        }

        private void ScreenThreat()
        {
            var point = _context.ProtectionCenter + Vector3.up;
            if (_context.LastSeen > float.NegativeInfinity
                && (_context.Visible || _context.HasContact))
            {
                point = _context.Visible ? _context.AimPoint : _context.KnownPosition + Vector3.up;
            }
            else
            {
                var forward = BotOwner.GetPlayer?.Transform.forward ?? Vector3.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
                point += forward.normalized * 8f;
            }

            BotOwner.Steering.LookToPoint(point);
            BotOwner.ShootData.EndShoot();
            BotOwner.AimingManager.CurrentAiming.LoseTarget();
        }
    }
}
