using System.Linq;
using System;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace EventRotator.ScourgeAi
{
    public sealed class ScourgeCombatLogic : CustomLogic
    {
        private readonly ScourgeBotContext _context;
        private readonly AimingToGoalTarget _aiming;
        private readonly AimingResultParams _aim = new AimingResultParams(null);
        private Vector3? _destination;
        private Vector3 _progressPosition;
        private float _progressTime;
        private float _moveDeadline;
        private float _nextPathAttempt;
        private float _nextGrenadeCheck;
        private string _movingTarget;
        private ScourgeTactic _movingTactic;
        private Vector3 _routeStartPosition;
        private Vector3 _routeThreat;
        private float _routeStartedAt;
        private float _plannedPathDistance;
        private int _protectionRevision;

        public ScourgeCombatLogic(BotOwner bot) : base(bot)
        {
            _context = ScourgeBotContext.Get(bot);
            _aiming = new AimingToGoalTarget(bot);
        }

        public override void Start()
        {
            _destination = null;
            _nextPathAttempt = Time.time;
            _movingTarget = null;
            _movingTactic = ScourgeTactic.None;
            _routeThreat = default(Vector3);
            _routeStartedAt = float.NegativeInfinity;
            _plannedPathDistance = 0f;
            _protectionRevision = _context.ProtectionRevision;
            BotOwner.PatrollingData.Pause();
            BotOwner.StopMove();
        }

        public override void Stop()
        {
            _context.Release();
            _destination = null;
            BotOwner.StopMove();
            BotOwner.Sprint(false);
            BotOwner.ShootData.EndShoot();
            BotOwner.AimingManager.CurrentAiming.LoseTarget();
            BotOwner.PatrollingData.Unpause();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            try { Tick(); }
            catch (Exception exception)
            {
                ScourgeAiLog.Source.LogError($"Scourge action yielded: {exception}");
                _context.YieldUntil = Time.time + 5f;
                Stop();
            }
        }

        private void Tick()
        {
            if (!ScourgeBotContext.Usable(BotOwner)) return;
            if (_context.ContinuingThrow)
            {
                // Throw callbacks finish asynchronously. Keep the native toss aim alive
                // through the hands transition; rifle aim and route changes must wait.
                BotOwner.StopMove();
                BotOwner.ShootData.EndShoot();
                BotOwner.WeaponManager.Grenades.AimingUpd();
                return;
            }
            _context.OwnThrow = false;
            _context.Refresh();
            if (_destination.HasValue && _protectionRevision != _context.ProtectionRevision)
            {
                _protectionRevision = _context.ProtectionRevision;
                EndMove("protection assignment changed", observedReplan: true);
                return;
            }
            if (_context.NativeEmergency || _context.ShouldRecover)
            {
                _context.Release();
                BotOwner.ShootData.EndShoot();
                return;
            }
            if (!_context.HasContact || Time.time < _context.YieldUntil) return;
            _context.MarkCombatActive();
            BotOwner.DoorOpener.UpdateDoorInteractionStatus();
            BotOwner.BewarePlantedMine.Update();
            BotOwner.BotAvoidDangerPlaces.Update();

            var tactic = _context.Tactic;
            var requiresCover = tactic == ScourgeTactic.Withdraw || tactic == ScourgeTactic.Reposition
                || (tactic == ScourgeTactic.Hold && !_context.CanFire);
            var wantsMove = requiresCover || tactic == ScourgeTactic.Flank || tactic == ScourgeTactic.Assault || tactic == ScourgeTactic.Search;
            if (_destination.HasValue && (_movingTarget != _context.TargetId || _movingTactic != tactic)) EndMove("interrupted");
            if (_destination.HasValue
                && ScourgeRouteLifecycle.ShouldReplanForObservedMove(
                    _movingTarget, _movingTactic, _context.TargetId, tactic,
                    Time.time - _routeStartedAt,
                    Vector3.Distance(_routeThreat, _context.KnownPosition)))
            {
                EndMove("observed destination moved", observedReplan: true);
                return;
            }
            if (!wantsMove && _destination.HasValue) EndMove("interrupted");
            if (wantsMove && !_destination.HasValue && Time.time >= _nextPathAttempt
                && !(requiresCover && tactic != ScourgeTactic.Reposition
                    && ScourgeNavigation.IsCovered(BotOwner.Position, _context.MovementThreat)))
                BeginMove(tactic, requiresCover);

            if (_destination.HasValue)
            {
                if (ScourgeManeuverTiming.IsComplete(
                    Vector3.Distance(BotOwner.Position, _destination.Value)))
                    EndMove("completed");
                else if (Time.time > _moveDeadline || (Time.time - _progressTime > 2f
                    && (BotOwner.Position - _progressPosition).sqrMagnitude < 0.25f))
                {
                    EndMove("stalled");
                    _context.YieldUntil = Time.time + 2f;
                    _context.Release();
                    if (ScourgeAiConfig.Debug.Value) ScourgeAiLog.Source.LogInfo($"Scourge {BotOwner.ProfileId}: route stalled; returning to native handling.");
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
            else
            {
                BotOwner.StopMove();
                if (requiresCover && ScourgeNavigation.IsCovered(BotOwner.Position, _context.MovementThreat)) BotOwner.SetPose(0f);
                else if (_context.CanFire) BotOwner.SetPose(1f);
            }

            if (TryGrenade()) return;
            var enemy = BotOwner.Memory.GoalEnemy;
            // Only the visible path queries a current body part. Brief suppression uses
            // the frozen observed aim point, and never fires through static geometry.
            var visibleShot = enemy != null && enemy.ProfileId == _context.TargetId && enemy.IsVisible && enemy.CanShoot;
            var suppress = !_context.Visible && Time.time - _context.LastSeen <= 1.2f;
            var pointToShoot = visibleShot ? enemy.GetPartToShoot() : _context.AimPoint;
            if (_context.CanFire && BotOwner.WeaponManager.HaveBullets && (visibleShot || suppress)
                && ScourgeNavigation.ClearShot(BotOwner.GetPlayer.PlayerBones.WeaponRoot.position, pointToShoot))
            {
                _aim.PointToShoot = pointToShoot;
                _aiming.UpdateNodeByBrain(_aim);
            }
            else
            {
                BotOwner.ShootData.EndShoot();
                BotOwner.AimingManager.CurrentAiming.LoseTarget();
                if (!_destination.HasValue) BotOwner.Steering.LookToPoint(_context.KnownPosition + Vector3.up);
            }
        }

        private void BeginMove(ScourgeTactic tactic, bool cover)
        {
            _nextPathAttempt = ScourgeManeuverTiming.NextPathAttempt(Time.time);
            if (!ScourgeNavigation.TryDestination(BotOwner, _context.MovementThreat, tactic, cover,
                    _context.Members, out var point, out var pathDistance, _context.FlankSupporter?.Position,
                    _context.MinimumCombatDistance,
                    _context.ProtectionActive ? _context.ProtectionCenter : (Vector3?)null,
                    _context.ProtectionRouteLeashRadius,
                    _context.IsProtectionAnchor))
            {
                _context.ManeuverEnded(tactic, "has no safe path");
                return;
            }
            BotOwner.SetPose(1f);
            BotOwner.BotLay.GetUp(true);
            BotOwner.SetTargetMoveSpeed(0.8f);
            BotOwner.Sprint(false);
            if (BotOwner.GoToPoint(point, true, 1f, true, true) != NavMeshPathStatus.PathComplete)
            {
                _context.ManeuverEnded(tactic, "was rejected by navigation");
                return;
            }
            _destination = point;
            _movingTactic = tactic;
            _movingTarget = _context.TargetId;
            _routeStartPosition = BotOwner.Position;
            _routeThreat = _context.KnownPosition;
            _routeStartedAt = Time.time;
            _plannedPathDistance = pathDistance;
            _progressPosition = BotOwner.Position;
            _progressTime = Time.time;
            _moveDeadline = Time.time + Mathf.Clamp(pathDistance / 0.8f + 5f, 8f, 45f);
            _context.ManeuverStarted(tactic);
            _protectionRevision = _context.ProtectionRevision;
            if (ScourgeAiConfig.Debug.Value)
                ScourgeAiLog.Source.LogInfo($"Scourge {BotOwner.ProfileId}: {tactic} route start, "
                    + $"role={(int)BotOwner.Profile.Info.Settings.Role}, distance={Vector3.Distance(BotOwner.Position, _context.KnownPosition):F1}, "
                    + $"path={pathDistance:F1}, canFire={_context.CanFire}, support={_context.FlankSupporter?.ProfileId ?? "none"}.");
        }

        private void EndMove(string reason, bool observedReplan = false)
        {
            var traveledDistance = Vector3.Distance(BotOwner.Position, _routeStartPosition);
            var endedTactic = _movingTactic;
            _destination = null;
            BotOwner.StopMove();
            _context.ManeuverEnded(endedTactic, reason, observedReplan);
            _nextPathAttempt = ScourgeManeuverTiming.NextPathAttempt(Time.time, observedReplan);
            if (ScourgeAiConfig.Debug.Value)
                ScourgeAiLog.Source.LogInfo($"Scourge {BotOwner.ProfileId}: {endedTactic} route end ({reason}), "
                    + $"traveled={traveledDistance:F1}, path={_plannedPathDistance:F1}.");
            _movingTactic = ScourgeTactic.None;
            _movingTarget = null;
            _routeThreat = default(Vector3);
            _routeStartedAt = float.NegativeInfinity;
            _plannedPathDistance = 0f;
        }

        private bool TryGrenade()
        {
            var now = Time.time;
            if (_destination.HasValue || _context.SupportingFlank || !_context.CanFire || !_context.Visible || now < _nextGrenadeCheck
                || now < _context.Squad.NextGrenadeTime || !BotOwner.WeaponManager.Grenades.HaveGrenade
                || !BotOwner.Settings.FileSettings.Grenade.CAN_THROW_STRAIGHT_CONTACT
                || BotOwner.Memory.GoalEnemy?.ProfileId != _context.TargetId
                || !BotOwner.Memory.GoalEnemy.IsVisible
                || now - BotOwner.Memory.EnemySetTime < BotOwner.Settings.FileSettings.Grenade.STRAIGHT_CONTACT_DELTA_SEC) return false;
            _nextGrenadeCheck = now + 2f;
            var distance = Vector3.Distance(BotOwner.Position, _context.KnownPosition);
            if (distance < 12f || distance > 35f || _context.Members.Any(member =>
                (member.Position - _context.KnownPosition).sqrMagnitude < 144f)
                || BotOwner.BotsGroup.Allies.Any(ally => ally != null && ally.HealthController.IsAlive
                    && (ally.Position - _context.KnownPosition).sqrMagnitude < 144f)) return false;
            // Native controller retains inventory, trajectory, friendly-fire and throw checks.
            // Revalidate this exact observed point instead of reusing a previous enemy's toss.
            if (!BotOwner.WeaponManager.Grenades.CanThrowGrenade(_context.KnownPosition + Vector3.up)
                || !BotOwner.WeaponManager.Grenades.DoThrow()) return false;
            _context.Squad.NextGrenadeTime = now + 6f;
            _context.OwnThrow = true;
            BotOwner.ShootData.EndShoot();
            return true;
        }
    }
}


