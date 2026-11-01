using System;
using System.Collections.Generic;

namespace EventRotator.ScourgeAi
{
    public readonly struct ScourgeSquadSlotSnapshot
    {
        public string TargetId { get; }
        public int AttackCount { get; }
        public string ManeuverOwner { get; }

        public ScourgeSquadSlotSnapshot(
            string targetId,
            int attackCount,
            string maneuverOwner)
        {
            TargetId = targetId;
            AttackCount = attackCount;
            ManeuverOwner = maneuverOwner;
        }
    }

    public sealed class ScourgeSquadSlots
    {
        public const int MaxAttackSlots = 2;
        public const int MaxManeuverSlots = 1;
        public const float DefaultLeaseSeconds = 1.5f;

        private const float MaxFlankSeconds = 6f;

        private readonly float _leaseSeconds;
        private readonly Dictionary<string, TargetSlots> _targets =
            new Dictionary<string, TargetSlots>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _botTargets =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public ScourgeSquadSlots()
            : this(DefaultLeaseSeconds)
        {
        }

        public ScourgeSquadSlots(float leaseSeconds)
        {
            if (float.IsNaN(leaseSeconds)
                || float.IsInfinity(leaseSeconds)
                || leaseSeconds <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
            }

            _leaseSeconds = leaseSeconds;
        }

        public bool TryAcquireAttack(string botId, string targetId, float now)
        {
            if (!IsValidIdentity(botId)
                || !IsValidIdentity(targetId)
                || !IsFinite(now))
            {
                return false;
            }

            Expire(now);
            ReleaseIfTargetChanged(botId, targetId);

            var slots = GetOrCreateTarget(targetId);
            if (OwnsAttack(slots, botId))
            {
                RenewAttack(slots, botId, now);
                return true;
            }

            if (slots.AttackOwner0 == null)
            {
                slots.AttackOwner0 = botId;
                slots.AttackExpiry0 = now + _leaseSeconds;
                _botTargets[botId] = targetId;
                return true;
            }

            if (slots.AttackOwner1 == null)
            {
                slots.AttackOwner1 = botId;
                slots.AttackExpiry1 = now + _leaseSeconds;
                _botTargets[botId] = targetId;
                return true;
            }

            return false;
        }

        public bool TryAcquireManeuver(string botId, string targetId, float now)
        {
            if (!IsValidIdentity(botId)
                || !IsValidIdentity(targetId)
                || !IsFinite(now))
            {
                return false;
            }

            Expire(now);
            ReleaseIfTargetChanged(botId, targetId);

            var slots = GetOrCreateTarget(targetId);
            if (slots.ManeuverOwner != null
                && !StringEquals(slots.ManeuverOwner, botId))
            {
                return false;
            }

            if (!OwnsAttack(slots, botId))
            {
                if (slots.AttackOwner0 == null)
                {
                    slots.AttackOwner0 = botId;
                    slots.AttackExpiry0 = now + _leaseSeconds;
                }
                else if (slots.AttackOwner1 == null)
                {
                    slots.AttackOwner1 = botId;
                    slots.AttackExpiry1 = now + _leaseSeconds;
                }
                else
                {
                    return false;
                }

                _botTargets[botId] = targetId;
            }
            else
            {
                RenewAttack(slots, botId, now);
            }

            slots.ManeuverOwner = botId;
            slots.ManeuverExpiry = now + _leaseSeconds;
            return true;
        }

        public bool TryAcquireFlank(string botId, string supporterId, string targetId, float now)
        {
            if (!IsValidIdentity(botId)
                || !IsValidIdentity(supporterId)
                || !IsValidIdentity(targetId)
                || StringEquals(botId, supporterId)
                || !IsFinite(now))
            {
                return false;
            }

            Expire(now);
            if (!_targets.TryGetValue(targetId, out var slots)
                || !OwnsAttack(slots, botId)
                || !OwnsAttack(slots, supporterId))
            {
                return false;
            }

            // A live maneuver can only be renewed by its existing owner. A flank
            // pair stays fixed for its six-second plan.
            if (slots.ManeuverOwner != null
                && !StringEquals(slots.ManeuverOwner, botId))
            {
                return false;
            }

            if (IsFlank(slots)
                && (!StringEquals(slots.FlankOwner, botId)
                    || !StringEquals(slots.FlankSupporter, supporterId)))
            {
                return false;
            }

            RenewAttack(slots, botId, now);
            // The supporter must maintain its own heartbeat; the mover cannot
            // keep a dead, interrupted or inactive covering bot's lease alive.
            slots.ManeuverOwner = botId;
            slots.ManeuverExpiry = now + _leaseSeconds;
            if (!IsFlank(slots))
            {
                slots.FlankOwner = botId;
                slots.FlankSupporter = supporterId;
                slots.FlankDeadline = now + MaxFlankSeconds;
            }

            _botTargets[botId] = targetId;
            _botTargets[supporterId] = targetId;
            return true;
        }

        public bool OwnsAttack(string botId, string targetId, float now)
        {
            if (!IsValidIdentity(botId)
                || !IsValidIdentity(targetId)
                || !IsFinite(now))
            {
                return false;
            }

            Expire(now);
            return _targets.TryGetValue(targetId, out var slots)
                && OwnsAttack(slots, botId);
        }

        public string GetFlankSupporter(string targetId, float now)
        {
            if (!IsValidIdentity(targetId) || !IsFinite(now))
            {
                return null;
            }

            Expire(now);
            return _targets.TryGetValue(targetId, out var slots) && IsFlank(slots)
                ? slots.FlankSupporter
                : null;
        }

        public string GetFlankOwner(string targetId, float now)
        {
            if (!IsValidIdentity(targetId) || !IsFinite(now))
            {
                return null;
            }

            Expire(now);
            return _targets.TryGetValue(targetId, out var slots) && IsFlank(slots)
                ? slots.FlankOwner
                : null;
        }

        public void ReleaseManeuver(string botId)
        {
            if (!IsValidIdentity(botId))
            {
                return;
            }

            var targetIds = new List<string>(_targets.Keys);
            for (var index = 0; index < targetIds.Count; index++)
            {
                var targetId = targetIds[index];
                if (!_targets.TryGetValue(targetId, out var slots))
                {
                    continue;
                }

                if (IsFlank(slots)
                    && (StringEquals(slots.FlankOwner, botId)
                        || StringEquals(slots.FlankSupporter, botId)))
                {
                    ClearFlank(slots);
                }
                else if (StringEquals(slots.ManeuverOwner, botId))
                {
                    ClearManeuver(slots);
                }

                RemoveEmptyTarget(targetId, slots);
            }
        }

        public void Release(string botId)
        {
            if (!IsValidIdentity(botId))
            {
                return;
            }

            // The snapshot keeps dictionary mutation safe while removing every lease held
            // by an owner, including a lease left behind by a caller that changed targets.
            var targetIds = new List<string>(_targets.Keys);
            for (var index = 0; index < targetIds.Count; index++)
            {
                var targetId = targetIds[index];
                if (!_targets.TryGetValue(targetId, out var slots))
                {
                    continue;
                }

                if (IsFlank(slots)
                    && (StringEquals(slots.FlankOwner, botId)
                        || StringEquals(slots.FlankSupporter, botId)))
                {
                    ClearFlank(slots);
                }

                RemoveOwner(slots, botId);
                RemoveEmptyTarget(targetId, slots);
            }

            _botTargets.Remove(botId);
        }

        public int GetAttackCount(string targetId, float now)
        {
            if (!IsValidIdentity(targetId) || !IsFinite(now))
            {
                return 0;
            }

            Expire(now);
            return _targets.TryGetValue(targetId, out var slots)
                ? slots.AttackCount
                : 0;
        }

        public int GetManeuverCount(string targetId, float now)
        {
            if (!IsValidIdentity(targetId) || !IsFinite(now))
            {
                return 0;
            }

            Expire(now);
            return _targets.TryGetValue(targetId, out var slots)
                && slots.ManeuverOwner != null
                ? 1
                : 0;
        }

        public string GetManeuverOwner(string targetId, float now)
        {
            if (!IsValidIdentity(targetId) || !IsFinite(now))
            {
                return null;
            }

            Expire(now);
            return _targets.TryGetValue(targetId, out var slots)
                ? slots.ManeuverOwner
                : null;
        }

        public ScourgeSquadSlotSnapshot GetSnapshot(string targetId, float now)
        {
            if (!IsValidIdentity(targetId) || !IsFinite(now))
            {
                return new ScourgeSquadSlotSnapshot(targetId, 0, null);
            }

            Expire(now);
            return _targets.TryGetValue(targetId, out var slots)
                ? new ScourgeSquadSlotSnapshot(targetId, slots.AttackCount, slots.ManeuverOwner)
                : new ScourgeSquadSlotSnapshot(targetId, 0, null);
        }

        public void Purge(float now)
        {
            if (IsFinite(now))
            {
                Expire(now);
            }
        }

        private TargetSlots GetOrCreateTarget(string targetId)
        {
            if (_targets.TryGetValue(targetId, out var slots))
            {
                return slots;
            }

            slots = new TargetSlots();
            _targets.Add(targetId, slots);
            return slots;
        }

        private void ReleaseIfTargetChanged(string botId, string targetId)
        {
            if (_botTargets.TryGetValue(botId, out var previousTarget)
                && !StringEquals(previousTarget, targetId))
            {
                Release(botId);
            }
        }

        private void Expire(float now)
        {
            var targetIds = new List<string>(_targets.Keys);
            for (var index = 0; index < targetIds.Count; index++)
            {
                var targetId = targetIds[index];
                if (!_targets.TryGetValue(targetId, out var slots))
                {
                    continue;
                }

                var expiredAttackOwner0 = (string)null;
                var expiredAttackOwner1 = (string)null;
                if (slots.AttackOwner0 != null && now >= slots.AttackExpiry0)
                {
                    expiredAttackOwner0 = slots.AttackOwner0;
                    slots.AttackOwner0 = null;
                    slots.AttackExpiry0 = 0f;
                }

                if (slots.AttackOwner1 != null && now >= slots.AttackExpiry1)
                {
                    expiredAttackOwner1 = slots.AttackOwner1;
                    slots.AttackOwner1 = null;
                    slots.AttackExpiry1 = 0f;
                }

                var expiredManeuverOwner = (string)null;
                if (IsFlank(slots))
                {
                    var participantExpired = !OwnsAttack(slots, slots.FlankOwner)
                        || !OwnsAttack(slots, slots.FlankSupporter);
                    if (participantExpired
                        || now >= slots.FlankDeadline
                        || now >= slots.ManeuverExpiry)
                    {
                        expiredManeuverOwner = slots.ManeuverOwner;
                        ClearFlank(slots);
                    }
                }
                else if (slots.ManeuverOwner != null
                    && (now >= slots.ManeuverExpiry || !OwnsAttack(slots, slots.ManeuverOwner)))
                {
                    expiredManeuverOwner = slots.ManeuverOwner;
                    ClearManeuver(slots);
                }

                RemoveMappingIfInactive(expiredAttackOwner0, targetId, slots);
                RemoveMappingIfInactive(expiredAttackOwner1, targetId, slots);
                RemoveMappingIfInactive(expiredManeuverOwner, targetId, slots);
                RemoveInactiveMappings(slots, targetId);
                RemoveEmptyTarget(targetId, slots);
            }
        }

        private void RenewAttack(TargetSlots slots, string botId, float now)
        {
            if (StringEquals(slots.AttackOwner0, botId))
            {
                slots.AttackExpiry0 = now + _leaseSeconds;
            }
            else if (StringEquals(slots.AttackOwner1, botId))
            {
                slots.AttackExpiry1 = now + _leaseSeconds;
            }

            if (StringEquals(slots.ManeuverOwner, botId))
            {
                slots.ManeuverExpiry = now + _leaseSeconds;
            }
        }

        private static bool OwnsAttack(TargetSlots slots, string botId)
        {
            return StringEquals(slots.AttackOwner0, botId)
                || StringEquals(slots.AttackOwner1, botId);
        }

        private static bool IsFlank(TargetSlots slots)
        {
            return slots.FlankOwner != null
                && slots.FlankSupporter != null
                && StringEquals(slots.ManeuverOwner, slots.FlankOwner);
        }

        private static void ClearManeuver(TargetSlots slots)
        {
            slots.ManeuverOwner = null;
            slots.ManeuverExpiry = 0f;
        }

        private static void ClearFlank(TargetSlots slots)
        {
            slots.FlankOwner = null;
            slots.FlankSupporter = null;
            slots.FlankDeadline = 0f;
            ClearManeuver(slots);
        }

        private static void RemoveOwner(TargetSlots slots, string botId)
        {
            if (StringEquals(slots.AttackOwner0, botId))
            {
                slots.AttackOwner0 = null;
                slots.AttackExpiry0 = 0f;
            }

            if (StringEquals(slots.AttackOwner1, botId))
            {
                slots.AttackOwner1 = null;
                slots.AttackExpiry1 = 0f;
            }

            if (StringEquals(slots.ManeuverOwner, botId))
            {
                slots.ManeuverOwner = null;
                slots.ManeuverExpiry = 0f;
            }
        }

        private void RemoveInactiveMappings(TargetSlots slots, string targetId)
        {
            RemoveMappingIfInactive(slots.AttackOwner0, targetId, slots);
            RemoveMappingIfInactive(slots.AttackOwner1, targetId, slots);
            RemoveMappingIfInactive(slots.ManeuverOwner, targetId, slots);
        }

        private void RemoveMappingIfInactive(string botId, string targetId, TargetSlots slots)
        {
            if (botId == null
                || StringEquals(slots.AttackOwner0, botId)
                || StringEquals(slots.AttackOwner1, botId)
                || StringEquals(slots.ManeuverOwner, botId))
            {
                return;
            }

            if (_botTargets.TryGetValue(botId, out var mappedTarget)
                && StringEquals(mappedTarget, targetId))
            {
                _botTargets.Remove(botId);
            }
        }

        private void RemoveEmptyTarget(string targetId, TargetSlots slots)
        {
            if (slots.AttackOwner0 == null
                && slots.AttackOwner1 == null
                && slots.ManeuverOwner == null)
            {
                _targets.Remove(targetId);
            }
        }

        private static bool IsValidIdentity(string value)
        {
            return !string.IsNullOrEmpty(value);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool StringEquals(string left, string right)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }

        private sealed class TargetSlots
        {
            public string AttackOwner0;
            public float AttackExpiry0;
            public string AttackOwner1;
            public float AttackExpiry1;
            public string ManeuverOwner;
            public float ManeuverExpiry;
            public string FlankOwner;
            public string FlankSupporter;
            public float FlankDeadline;

            public int AttackCount =>
                (AttackOwner0 == null ? 0 : 1)
                + (AttackOwner1 == null ? 0 : 1);
        }
    }
}


