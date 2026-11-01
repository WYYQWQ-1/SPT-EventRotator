using System.Linq;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EFT;
using UnityEngine;

namespace EventRotator.ScourgeAi
{
    internal sealed class ScourgeSquadContext
    {
        public readonly ScourgeSquadSlots Slots = new ScourgeSquadSlots();
        public float NextGrenadeTime;
        public BotOwner ProtectionBoss;
        public string CloseGuardId;
        public int ProtectionRevision;
        public Vector3 ProtectionBossPosition;
        public bool HasProtectionBossPosition;
    }

    internal sealed class ScourgeBotContext
    {
        private static readonly ConditionalWeakTable<BotOwner, ScourgeBotContext> Bots = new ConditionalWeakTable<BotOwner, ScourgeBotContext>();
        private static readonly ConditionalWeakTable<object, ScourgeSquadContext> Squads = new ConditionalWeakTable<object, ScourgeSquadContext>();
        private readonly BotOwner _bot;
        private object _squadKey;
        private float _nextRefresh;
        private float _contactStart;
        private float _slotStart;
        private float _slotRestUntil;
        private bool _hasSlot;
        private readonly ScourgeHitReaction _hitReaction = new ScourgeHitReaction();
        private bool _moving;
        private float _moveUntil;
        private ScourgeTactic _moveTactic;
        private float _combatActiveUntil = float.NegativeInfinity;
        private float _personalLastSeen = float.NegativeInfinity;
        private float _capturedHitTime;
        private Vector3 _hitThreat;
        private int _observedProtectionRevision;
        private BotOwner _lockedProtectionBoss;
        private ScourgeProtectionAnchorState _protectionAnchorState
            = ScourgeProtectionAnchorState.Unresolved;
        public ScourgeSquadContext Squad { get; private set; }
        public BotOwner[] Members { get; private set; } = Array.Empty<BotOwner>();
        public BotOwner ProtectionBoss { get; private set; }
        public bool ProtectionActive => ProtectionBoss != null && !ProtectionBoss.IsDead;
        public bool IsProtectionAnchor => ProtectionActive && ReferenceEquals(ProtectionBoss, _bot);
        public bool IsCloseProtectionGuard => ProtectionActive && !IsProtectionAnchor
            && string.Equals(Squad?.CloseGuardId, _bot.ProfileId, StringComparison.Ordinal);
        public float ProtectionDistance => ProtectionActive
            ? Vector3.Distance(_bot.Position, ProtectionBoss.Position)
            : 0f;
        public Vector3 ProtectionCenter => ProtectionActive ? ProtectionBoss.Position : default(Vector3);
        public int ProtectionRevision => Squad?.ProtectionRevision ?? 0;
        public bool ProtectionRevisionChanged => Squad != null
            && Squad.ProtectionRevision != _observedProtectionRevision;
        public ScourgeProtectionSettings ProtectionSettings => ScourgeAiConfig.ProtectionSettings;
        public float ProtectionRouteLeashRadius => IsCloseProtectionGuard
            ? ProtectionSettings.CloseGuardRadius
            : ProtectionSettings.LeashRadius;
        public bool NeedsProtectionEscort => ScourgeProtectionPolicy.ShouldEscort(
            ProtectionActive,
            ProtectionDistance,
            ProtectionSettings);
        public bool NeedsProtectionRegroup => ScourgeProtectionPolicy.ShouldRegroup(
            ProtectionActive,
            IsProtectionAnchor,
            IsCloseProtectionGuard,
            HasContact,
            ProtectionRevisionChanged,
            ProtectionDistance,
            ProtectionSettings);
        public bool NeedsProtectionScreen => ProtectionActive && !IsProtectionAnchor
            && IsCloseProtectionGuard && !HasContact;
        public EnemyInfo Enemy { get; private set; }
        public string TargetId { get; private set; }
        public Vector3 KnownPosition { get; private set; }
        public Vector3 MovementThreat => Tactic == ScourgeTactic.Reposition ? _hitThreat : KnownPosition;
        public Vector3 AimPoint { get; private set; }
        public float LastSeen { get; private set; } = float.NegativeInfinity;
        public bool Visible { get; private set; }
        public bool CanFire { get; private set; }
        public float MinimumCombatDistance { get; private set; }
        public bool HasContact { get; private set; }
        public bool ReloadForBreak { get; private set; }
        public bool ShouldRecover => NeedsReload || ReloadForBreak || ScourgeRecoveryLogic.IsReloadPending(_bot);
        public BotOwner FlankSupporter { get; private set; }
        public bool SupportingFlank => Squad?.Slots.GetFlankSupporter(TargetId, Time.time) == _bot.ProfileId;
        public ScourgeTactic Tactic { get; private set; }
        public float YieldUntil { get; set; }
        public float NextManeuverTime { get; set; }
        public float NextRecoveryTime { get; set; }
        public bool OwnThrow { get; set; }
        public bool ContinuingThrow => OwnThrow && _bot.WeaponManager.Grenades.ThrowindNow;

        private ScourgeBotContext(BotOwner bot) { _bot = bot; }
        public static ScourgeBotContext Get(BotOwner bot) => Bots.GetValue(bot, owner => new ScourgeBotContext(owner));

        public void MarkProtectionRevisionObserved()
        {
            _observedProtectionRevision = ProtectionRevision;
        }

        public static bool Owns(BotOwner bot) => ScourgeAiRuntime.Ready && bot != null
            && bot.Profile?.Info?.Settings != null && ScourgeRoles.IsScourge((int)bot.Profile.Info.Settings.Role);

        public static bool Usable(BotOwner bot) => Owns(bot) && !bot.IsDead && bot.BotState == EBotState.Active
            && bot.Memory != null && bot.WeaponManager != null && bot.BotsGroup != null;

        public bool NativeEmergency => !Usable(_bot) || _bot.BewareGrenade.ShallRunAway() || _bot.FlashGrenade.IsFlashed
            || _bot.Medecine.Using || _bot.Medecine.FirstAid.Have2Do || _bot.Medecine.SurgicalKit.HaveWork
            || _bot.WeaponManager.Malfunctions.HaveMalfunction()
            || _bot.WeaponManager.Grenades.ThrowindNow
            || (_bot.WeaponManager.Reload.NoAmmoForReloadCached && !_bot.WeaponManager.HaveBullets)
            || !_bot.WeaponManager.IsWeaponReady;

        public bool NeedsReload => _bot.WeaponManager.Reload != null
            && (_bot.WeaponManager.Reload.Reloading || (!_bot.WeaponManager.Reload.NoAmmoForReloadCached
                && _bot.WeaponManager.Reload.BulletCount <= Math.Max(1, _bot.WeaponManager.Reload.MaxBulletCount / 5)));

        public void Release()
        {
            Squad?.Slots.Release(_bot.ProfileId);
            _hasSlot = false;
            CanFire = false;
            _moving = false;
            FlankSupporter = null;
            _combatActiveUntil = float.NegativeInfinity;
            Tactic = ScourgeTactic.None;
        }

        public void MarkCombatActive() => _combatActiveUntil = Time.time + 0.6f;

        public void ManeuverStarted(ScourgeTactic tactic)
        {
            _moving = true;
            _moveTactic = tactic;
            // Keep the policy commitment bounded so a stale destination is
            // reconsidered when the observed target or cover geometry changes.
            _moveUntil = Time.time + 5f;
            if (tactic == ScourgeTactic.Reposition) _hitReaction.Consume(Time.time);
            if (ScourgeAiConfig.Debug.Value)
                ScourgeAiLog.Source.LogInfo($"Scourge {_bot.ProfileId}: {tactic} route started, support={FlankSupporter?.ProfileId ?? "none"}.");
        }

        public void ManeuverEnded(ScourgeTactic endedTactic, string reason, bool observedReplan = false)
        {
            _hitReaction.FinishManeuver(endedTactic, Time.time);
            if (ScourgeAiConfig.Debug.Value)
                ScourgeAiLog.Source.LogInfo($"Scourge {_bot.ProfileId}: {endedTactic} route {reason}.");
            _moving = false;
            _moveTactic = ScourgeTactic.None;
            if (Tactic == endedTactic) Tactic = ScourgeTactic.None;
            // A changed observation needs a fresh destination, not the full rest
            // after a completed or failed move. Still release and reacquire leases.
            NextManeuverTime = ScourgeManeuverTiming.NextManeuver(Time.time, observedReplan);
            _nextRefresh = 0f;
            Release();
        }

        private bool CanSupport(BotOwner member, float now)
        {
            return member != _bot && Usable(member)
                && member.Memory.GoalEnemy?.ProfileId == TargetId && member.Memory.GoalEnemy.IsVisible
                && member.Memory.GoalEnemy.CanShoot && member.WeaponManager.HaveBullets
                && (member.Position - _bot.Position).sqrMagnitude < 6400f
                && Bots.TryGetValue(member, out var other) && ReferenceEquals(other.Squad, Squad)
                && other._combatActiveUntil >= now && !other._moving && other.Tactic == ScourgeTactic.Hold
                && !other.NativeEmergency && !other.ShouldRecover && !other._hitReaction.Pending(now)
                && Squad.Slots.OwnsAttack(member.ProfileId, TargetId, now)
                && ScourgeNavigation.ClearShot(member.GetPlayer.PlayerBones.WeaponRoot.position, member.Memory.GoalEnemy.GetPartToShoot());
        }

        private static bool IsScourgeBoss(BotOwner bot)
        {
            return bot != null
                && bot.Profile?.Info?.Settings != null
                && (int)bot.Profile.Info.Settings.Role == ScourgeRoles.BossValue
                && bot.Boss?.IamBoss == true;
        }

        private static bool IsAssociatedWithBoss(BotOwner member, BotOwner boss)
        {
            if (member == null || boss == null) return false;
            if (ReferenceEquals(member, boss)) return true;

            // Once this member has a fixed anchor, that identity remains the
            // association authority even if native BotFollower state is later
            // reassigned. This preserves the original squad while ensuring a
            // different boss cannot claim the already-locked member.
            if (Bots.TryGetValue(member, out var lockedContext)
                && lockedContext._protectionAnchorState.IsLocked
                && lockedContext._lockedProtectionBoss != null)
            {
                return ReferenceEquals(lockedContext._lockedProtectionBoss, boss);
            }

            var followedBoss = member.BotFollower?.BossToFollow as BotBoss;
            if (followedBoss != null)
            {
                // Once native follower state exists it is authoritative. In
                // particular, a member following another boss must not fall
                // through to a shared BotsGroup and join this squad.
                return ScourgeProtectionPolicy.IsAssociatedWithAnchor(
                    isAnchor: false,
                    hasExplicitAssociation: true,
                    explicitAssociationMatchesAnchor: followedBoss.Owner != null
                        && ReferenceEquals(followedBoss.Owner, boss),
                    sharesBossGroup: false);
            }

            var memberGroup = member.BotsGroup;
            var bossGroup = boss.BotsGroup;
            return ScourgeProtectionPolicy.IsAssociatedWithAnchor(
                isAnchor: false,
                hasExplicitAssociation: false,
                explicitAssociationMatchesAnchor: false,
                sharesBossGroup: memberGroup != null && bossGroup != null
                    && (ReferenceEquals(memberGroup, bossGroup)
                        || ReferenceEquals(memberGroup.BossGroup?.Boss, boss)));
        }

        private static bool IsLockedToDifferentBoss(BotOwner member, BotOwner boss)
        {
            if (member == null || boss == null || ReferenceEquals(member, boss)) return false;
            if (!Bots.TryGetValue(member, out var context)) return false;
            return context._protectionAnchorState.IsLocked
                && context._lockedProtectionBoss != null
                && !ReferenceEquals(context._lockedProtectionBoss, boss);
        }

        private BotOwner ResolveProtectionBoss()
        {
            if (_protectionAnchorState.IsLocked)
            {
                // Revalidate the original object, not the follower's current
                // native association. A reassigned BotFollower must not hide
                // later death of the anchor that this context already locked.
                var lockedBossAlive = _lockedProtectionBoss != null
                    && !_lockedProtectionBoss.IsDead;
                _protectionAnchorState = ScourgeProtectionPolicy.ResolveAnchor(
                    _protectionAnchorState,
                    _protectionAnchorState.AnchorId,
                    IsScourgeBoss(_lockedProtectionBoss),
                    candidateAssociated: true,
                    candidateAlive: lockedBossAlive);
                return _protectionAnchorState.IsTerminal ? null : _lockedProtectionBoss;
            }

            var role = _bot.Profile?.Info?.Settings == null
                ? -1
                : (int)_bot.Profile.Info.Settings.Role;
            if (role == ScourgeRoles.BossValue)
            {
                // Role data and BotBoss initialization can arrive on separate
                // frames. Leave a partial boss unresolved until its real
                // BotBoss identity is available.
                var bossIsValid = IsScourgeBoss(_bot);
                _protectionAnchorState = ScourgeProtectionPolicy.ResolveAnchor(
                    _protectionAnchorState,
                    _bot.ProfileId,
                    bossIsValid,
                    candidateAssociated: bossIsValid,
                    candidateAlive: !_bot.IsDead);
                if (_protectionAnchorState.IsLocked && _lockedProtectionBoss == null)
                    _lockedProtectionBoss = _bot;
                return _protectionAnchorState.IsTerminal ? null : _lockedProtectionBoss;
            }

            // A follower's explicit BotFollower association is authoritative. If
            // that anchor is dead, do not fall through to another group boss.
            var followedBoss = _bot.BotFollower?.BossToFollow as BotBoss;
            if (followedBoss != null)
            {
                var followedOwner = followedBoss.Owner;
                if (!IsScourgeBoss(followedOwner) || !IsAssociatedWithBoss(_bot, followedOwner))
                {
                    // A native follower link may be present before the role or
                    // group association is fully initialized. Keep it
                    // unresolved, and never fall through to another boss.
                    return null;
                }

                _protectionAnchorState = ScourgeProtectionPolicy.ResolveAnchor(
                    _protectionAnchorState,
                    followedOwner.ProfileId,
                    candidateIsScourgeBoss: true,
                    candidateAssociated: true,
                    candidateAlive: !followedOwner.IsDead);
                if (_protectionAnchorState.IsLocked && _lockedProtectionBoss == null)
                    _lockedProtectionBoss = followedOwner;
                return _protectionAnchorState.IsTerminal ? null : _lockedProtectionBoss;
            }

            var groupBoss = _bot.BotsGroup?.BossGroup?.Boss;
            if (IsScourgeBoss(groupBoss) && IsAssociatedWithBoss(_bot, groupBoss))
            {
                _protectionAnchorState = ScourgeProtectionPolicy.ResolveAnchor(
                    _protectionAnchorState,
                    groupBoss.ProfileId,
                    candidateIsScourgeBoss: true,
                    candidateAssociated: true,
                    candidateAlive: !groupBoss.IsDead);
                if (_protectionAnchorState.IsLocked && _lockedProtectionBoss == null)
                    _lockedProtectionBoss = groupBoss;
                return _protectionAnchorState.IsTerminal ? null : _lockedProtectionBoss;
            }

            // The native association may be created after this first refresh.
            // Leave an unassociated follower unresolved so the eventual
            // legitimate BotFollower/BossGroup link can become its anchor.
            return null;
        }

        private static BotOwner SelectCloseGuard(
            IReadOnlyList<BotOwner> members,
            BotOwner boss,
            string previousId,
            ScourgeProtectionSettings settings)
        {
            if (members == null || boss == null) return null;
            var previous = members.FirstOrDefault(member =>
                member != null
                && !ReferenceEquals(member, boss)
                && !IsLockedToDifferentBoss(member, boss)
                && string.Equals(member.ProfileId, previousId, StringComparison.Ordinal)
                && member.Profile?.Info?.Settings != null
                && (int)member.Profile.Info.Settings.Role == ScourgeRoles.FollowerValue);
            if (previous != null
                && ScourgeBotContext.Usable(previous)
                && Vector3.Distance(previous.Position, boss.Position) <= settings.CloseGuardRadius)
            {
                var previousContext = Get(previous);
                if (!previousContext.NativeEmergency && !previousContext.ShouldRecover)
                    return previous;
            }

            return members
                .Where(member => member != null
                    && !ReferenceEquals(member, boss)
                    && !IsLockedToDifferentBoss(member, boss)
                    && !member.IsDead
                    && member.Profile?.Info?.Settings != null
                    && (int)member.Profile.Info.Settings.Role == ScourgeRoles.FollowerValue
                    && ScourgeBotContext.Usable(member)
                    && !Get(member).NativeEmergency
                    && !Get(member).ShouldRecover)
                .OrderBy(member => (member.Position - boss.Position).sqrMagnitude)
                .ThenBy(member => member.ProfileId)
                .FirstOrDefault();
        }

        private void RefreshProtection(BotOwner boss, float now)
        {
            var settings = ProtectionSettings;
            var previousBoss = Squad.ProtectionBoss;
            var previousGuard = Squad.CloseGuardId;
            ProtectionBoss = boss;
            Squad.ProtectionBoss = boss;

            if (!ReferenceEquals(previousBoss, boss))
                Squad.ProtectionRevision++;
            if (boss != null)
            {
                if (!Squad.HasProtectionBossPosition
                    || (boss.Position - Squad.ProtectionBossPosition).sqrMagnitude
                    >= settings.ReplanDistance * settings.ReplanDistance)
                {
                    if (Squad.HasProtectionBossPosition) Squad.ProtectionRevision++;
                    Squad.ProtectionBossPosition = boss.Position;
                    Squad.HasProtectionBossPosition = true;
                }

                var closeGuard = SelectCloseGuard(Members, boss, previousGuard, settings);
                Squad.CloseGuardId = closeGuard?.ProfileId;
            }
            else
            {
                Squad.CloseGuardId = null;
                Squad.HasProtectionBossPosition = false;
            }

            if (!string.Equals(previousGuard, Squad.CloseGuardId, StringComparison.Ordinal))
                Squad.ProtectionRevision++;
        }

        public void Refresh()
        {
            var now = Time.time;
            if (now < _nextRefresh) return;
            _nextRefresh = now + 0.2f;
            _hitReaction.Observe(_bot.Memory.LastTimeHit, now);
            if (_bot.Memory.LastTimeHit > _capturedHitTime && _bot.Memory.LastTimeHit <= now)
            {
                _capturedHitTime = _bot.Memory.LastTimeHit;
                _hitThreat = _bot.Memory.LastHitPos;
            }
            // Supports may follow the same boss while belonging to different
            // BotsGroups, but every protection decision is anchored to the
            // explicit living Scourge BotBoss. A dead anchor intentionally
            // produces an independent squad context.
            var boss = ResolveProtectionBoss();
            object key = (object)boss ?? _bot;
            if (!ReferenceEquals(key, _squadKey))
            {
                Release();
                _squadKey = key;
                Squad = Squads.GetValue(key, _ => new ScourgeSquadContext());
                _observedProtectionRevision = Squad.ProtectionRevision;
            }
            if (boss != null)
            {
                var members = new List<BotOwner>();
                if (_bot.BotsGroup?._members != null) members.AddRange(_bot.BotsGroup._members);
                if (boss.BotsGroup != null) members.AddRange(boss.BotsGroup._members);
                if (boss.Boss?.Followers != null) members.AddRange(boss.Boss.Followers);
                members.Add(boss);
                Members = members.Where(member => member != null && !member.IsDead
                    && member.Profile?.Info?.Settings != null
                    && (ReferenceEquals(member, boss)
                        || (int)member.Profile.Info.Settings.Role == ScourgeRoles.FollowerValue)
                    && !IsLockedToDifferentBoss(member, boss)
                    && IsAssociatedWithBoss(member, boss))
                    .Distinct().ToArray();
            }
            else
            {
                // Once the anchor is gone, survivors keep their own combat
                // memory and leases instead of promoting another bot.
                Members = new[] { _bot };
            }
            RefreshProtection(boss, now);

            var enemy = _bot.Memory.GoalEnemy;
            var targetId = enemy?.ProfileId;
            if (!string.Equals(TargetId, targetId, StringComparison.Ordinal))
            {
                Release();
                TargetId = targetId;
                LastSeen = float.NegativeInfinity;
                _personalLastSeen = float.NegativeInfinity;
                _contactStart = now;
                NextManeuverTime = now;
            }
            Enemy = enemy;
            Visible = enemy?.IsVisible == true;
            if (Visible)
            {
                KnownPosition = enemy.CurrPosition;
                AimPoint = enemy.GetPartToShoot();
                LastSeen = now;
                _personalLastSeen = now;
            }
            else if (enemy?.HaveSeenPersonal == true && enemy.PersonalLastSeenTime > LastSeen)
            {
                KnownPosition = enemy.PersonalLastPos;
                AimPoint = KnownPosition + Vector3.up;
                LastSeen = enemy.PersonalLastSeenTime;
            }
            else if (enemy?.GroupInfo?.IsHaveSeen == true && enemy.TimeLastSeenReal > LastSeen)
            {
                KnownPosition = enemy.EnemyLastPositionReal;
                AimPoint = KnownPosition + Vector3.up;
                LastSeen = enemy.TimeLastSeenReal;
            }
            var settings = ScourgeAiConfig.TacticalSettings;
            MinimumCombatDistance = ScourgeRoleTuning.ForRole(
                (int)_bot.Profile.Info.Settings.Role, settings.AggressionMultiplier).MinimumDistance;
            HasContact = enemy != null && enemy.Person.HealthController.IsAlive
                && now - LastSeen <= settings.EffectiveContactMemorySeconds;
            if (enemy?.HaveSeenPersonal == true)
                _personalLastSeen = Math.Max(_personalLastSeen, enemy.PersonalLastSeenTime);
            var reload = _bot.WeaponManager.Reload;
            ReloadForBreak = HasContact && ScourgeCombatReadiness.ShouldReloadDuringBreak(
                reload.BulletCount, reload.MaxBulletCount, now - _personalLastSeen,
                _bot.Memory.LastTimeHit > 0f ? now - _bot.Memory.LastTimeHit : float.PositiveInfinity,
                Visible, !Visible && ScourgeNavigation.IsCovered(_bot.Position, KnownPosition),
                !reload.NoAmmoForReloadCached, NativeEmergency, SupportingFlank || _moving)
                && reload.FightShallReload();
            if (!HasContact || NativeEmergency || ShouldRecover || now < YieldUntil)
            {
                Release();
                Tactic = ScourgeTactic.None;
                return;
            }

            // A flank is an explicit pair. Validate the covering bot every refresh;
            // death, reload, danger, a new target or lost sight ends the commitment.
            var flankOwner = Squad.Slots.GetFlankOwner(TargetId, now);
            if (flankOwner == _bot.ProfileId)
            {
                var supportId = Squad.Slots.GetFlankSupporter(TargetId, now);
                FlankSupporter = Members.FirstOrDefault(member => member.ProfileId == supportId);
                if (FlankSupporter == null || !CanSupport(FlankSupporter, now)
                    || !Visible || _hitReaction.Pending(now))
                {
                    Squad.Slots.ReleaseManeuver(_bot.ProfileId);
                    FlankSupporter = null;
                    _moving = false;
                    NextManeuverTime = ScourgeManeuverTiming.NextManeuver(now);
                }
            }
            else if (_moving && _moveTactic == ScourgeTactic.Flank)
            {
                _moving = false;
                FlankSupporter = null;
                NextManeuverTime = ScourgeManeuverTiming.NextManeuver(now);
            }
            if (SupportingFlank && (_hitReaction.Pending(now) || !Visible || !enemy.CanShoot
                || now - _bot.Memory.LastTimeHit < 1f)) Squad.Slots.ReleaseManeuver(_bot.ProfileId);

            // Ordinary attackers rotate. A bounded maneuver and its covering shooter
            // retain their slots until the route completes or the pair expires.
            if (_hasSlot && !_moving && !SupportingFlank && now - _slotStart >= 4f)
            {
                Release();
                _slotRestUntil = now + 0.8f;
            }
            var attack = now >= _slotRestUntil && Squad.Slots.TryAcquireAttack(_bot.ProfileId, TargetId, now);
            if (attack && !_hasSlot) _slotStart = now;
            _hasSlot = attack;
            var support = Members.FirstOrDefault(member => CanSupport(member, now));
            var canManeuver = attack && now >= NextManeuverTime
                && (Squad.Slots.GetManeuverOwner(TargetId, now) == null
                    || Squad.Slots.GetManeuverOwner(TargetId, now) == _bot.ProfileId);
            var distance = Vector3.Distance(_bot.Position, KnownPosition);
            var input = new ScourgeTacticalInput((int)_bot.Profile.Info.Settings.Role,
                true, Visible, now - LastSeen, distance, false, canManeuver, attack, support != null, now - _contactStart)
            {
                RepositionAfterHit = _hitReaction.Pending(now),
                SupportingFlank = SupportingFlank
            };
            var next = ScourgeTacticalPolicy.Decide(input, settings);
            if (next == ScourgeTactic.Withdraw || next == ScourgeTactic.Reposition)
                Squad.Slots.ReleaseManeuver(_bot.ProfileId);
            if (_moving && now < _moveUntil && next != ScourgeTactic.Reposition && next != ScourgeTactic.Withdraw)
                next = _moveTactic;
            if (next == ScourgeTactic.Flank)
            {
                var covering = FlankSupporter ?? support;
                if (covering == null || !Squad.Slots.TryAcquireFlank(_bot.ProfileId, covering.ProfileId, TargetId, now))
                    next = ScourgeTactic.Hold;
                else FlankSupporter = covering;
            }
            else if ((next == ScourgeTactic.Assault || next == ScourgeTactic.Search)
                && !Squad.Slots.TryAcquireManeuver(_bot.ProfileId, TargetId, now)) next = ScourgeTactic.Hold;
            CanFire = attack || distance < MinimumCombatDistance;
            if (Tactic != next && ScourgeAiConfig.Debug.Value)
                ScourgeAiLog.Source.LogInfo($"Scourge {_bot.ProfileId}: {next}, role={(int)_bot.Profile.Info.Settings.Role}, "
                    + $"distance={distance:F1}, canFire={CanFire}, support={support?.ProfileId ?? "none"}, "
                    + $"visible={Visible}, attackSlot={attack}.");
            Tactic = next;
        }
    }
}


