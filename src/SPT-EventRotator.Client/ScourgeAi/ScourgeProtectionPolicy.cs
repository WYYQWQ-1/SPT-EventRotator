using System;

namespace EventRotator.ScourgeAi
{
    /// <summary>
    /// Immutable anchor lifecycle state used to make first-association,
    /// reassociation, and later-death behavior deterministic without BotOwner
    /// or Unity test doubles.
    /// </summary>
    public readonly struct ScourgeProtectionAnchorState
    {
        private ScourgeProtectionAnchorState(string anchorId, bool locked, bool terminal)
        {
            AnchorId = anchorId;
            IsLocked = locked;
            IsTerminal = terminal;
        }

        public string AnchorId { get; }
        public bool IsLocked { get; }
        public bool IsTerminal { get; }

        public static ScourgeProtectionAnchorState Unresolved =>
            new ScourgeProtectionAnchorState(null, false, false);

        public static ScourgeProtectionAnchorState Locked(string anchorId) =>
            string.IsNullOrEmpty(anchorId)
                ? Unresolved
                : new ScourgeProtectionAnchorState(anchorId, true, false);

        public static ScourgeProtectionAnchorState Terminal(string anchorId) =>
            new ScourgeProtectionAnchorState(anchorId, true, true);
    }

    /// <summary>
    /// Pure protection limits shared by escort, combat, and recovery movement.
    /// Distances are supplied by the caller so this policy stays independent of
    /// Unity and can be exercised without a game runtime.
    /// </summary>
    public readonly struct ScourgeProtectionSettings
    {
        public const float DefaultLeashRadius = 36f;
        public const float DefaultCloseGuardRadius = 9f;
        public const float DefaultReplanDistance = 4f;

        public float LeashRadius { get; }
        public float CloseGuardRadius { get; }
        public float ReplanDistance { get; }

        public ScourgeProtectionSettings(
            float leashRadius = DefaultLeashRadius,
            float closeGuardRadius = DefaultCloseGuardRadius,
            float replanDistance = DefaultReplanDistance)
        {
            LeashRadius = NormalizeRadius(leashRadius, DefaultLeashRadius);
            CloseGuardRadius = Math.Min(
                NormalizeRadius(closeGuardRadius, DefaultCloseGuardRadius),
                LeashRadius);
            ReplanDistance = NormalizeRadius(replanDistance, DefaultReplanDistance);
        }

        public static ScourgeProtectionSettings Default => new ScourgeProtectionSettings(
            DefaultLeashRadius,
            DefaultCloseGuardRadius,
            DefaultReplanDistance);

        private static float NormalizeRadius(float value, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) || value <= 0f
                ? fallback
                : value;
        }
    }

    /// <summary>
    /// Radius and duty decisions for a living Scourge boss anchor.
    /// </summary>
    public static class ScourgeProtectionPolicy
    {
        public const float DefaultLeashRadius = ScourgeProtectionSettings.DefaultLeashRadius;
        public const float DefaultCloseGuardRadius = ScourgeProtectionSettings.DefaultCloseGuardRadius;
        public const float DefaultReplanDistance = ScourgeProtectionSettings.DefaultReplanDistance;

        /// <summary>
        /// Locks the first valid associated Scourge boss. Once locked, a
        /// different candidate can never replace it; only loss/death of that
        /// same identity transitions the state to terminal protection.
        /// </summary>
        public static ScourgeProtectionAnchorState ResolveAnchor(
            ScourgeProtectionAnchorState current,
            string candidateId,
            bool candidateIsScourgeBoss,
            bool candidateAssociated,
            bool candidateAlive)
        {
            if (current.IsTerminal) return current;

            if (current.IsLocked)
            {
                if (!string.Equals(current.AnchorId, candidateId, StringComparison.Ordinal))
                    return current;
                return candidateIsScourgeBoss && candidateAssociated && candidateAlive
                    ? current
                    : ScourgeProtectionAnchorState.Terminal(current.AnchorId);
            }

            if (string.IsNullOrEmpty(candidateId)
                || !candidateIsScourgeBoss
                || !candidateAssociated)
            {
                return current;
            }

            // A living, valid association locks the first identity. A dead
            // boss is still a valid first association, but it is terminal and
            // must never allow a later candidate to become the squad anchor.
            if (!candidateAlive)
                return ScourgeProtectionAnchorState.Terminal(candidateId);

            return ScourgeProtectionAnchorState.Locked(candidateId);
        }

        /// <summary>
        /// Resolves membership against one fixed anchor. An explicit native
        /// follower link is authoritative: a member linked to another boss
        /// cannot re-enter through a shared group. Group membership is only a
        /// fallback while no explicit link exists.
        /// </summary>
        public static bool IsAssociatedWithAnchor(
            bool isAnchor,
            bool hasExplicitAssociation,
            bool explicitAssociationMatchesAnchor,
            bool sharesBossGroup)
        {
            if (isAnchor) return true;
            return hasExplicitAssociation
                ? explicitAssociationMatchesAnchor
                : sharesBossGroup;
        }

        public static bool IsWithinLeash(float distance, float leashRadius = DefaultLeashRadius)
        {
            return IsFiniteNonNegative(distance)
                && IsFinitePositive(leashRadius)
                && distance <= leashRadius;
        }

        public static bool IsBeyondLeash(float distance, float leashRadius = DefaultLeashRadius)
        {
            return IsFiniteNonNegative(distance)
                && IsFinitePositive(leashRadius)
                && distance > leashRadius;
        }

        public static bool ShouldEscort(
            bool bossAlive,
            float distanceToBoss,
            ScourgeProtectionSettings settings)
        {
            return bossAlive && IsBeyondLeash(distanceToBoss, settings.LeashRadius);
        }

        public static bool ShouldEscort(
            bool bossAlive,
            float distanceToBoss,
            float leashRadius = DefaultLeashRadius)
        {
            return bossAlive && IsBeyondLeash(distanceToBoss, leashRadius);
        }

        /// <summary>
        /// Decides whether a follower needs a protection regroup route. The
        /// close guard remains a single assigned duty, including while it has
        /// contact, while ordinary followers only react to a pending boss
        /// movement revision once they reach the bounded regroup band.
        /// </summary>
        public static bool ShouldRegroup(
            bool bossAlive,
            bool isAnchor,
            bool isCloseGuard,
            bool hasContact,
            bool protectionRevisionChanged,
            float distanceToBoss,
            ScourgeProtectionSettings settings)
        {
            if (!bossAlive || isAnchor || !IsFiniteNonNegative(distanceToBoss)) return false;
            if (ShouldEscort(bossAlive, distanceToBoss, settings)) return true;
            if (isCloseGuard && distanceToBoss > settings.CloseGuardRadius) return true;
            return !hasContact && protectionRevisionChanged
                && distanceToBoss >= settings.LeashRadius * 0.75f;
        }

        public static bool IsWithinCloseGuardRadius(
            float distanceToBoss,
            float closeGuardRadius = DefaultCloseGuardRadius)
        {
            return IsWithinLeash(distanceToBoss, closeGuardRadius);
        }

        public static bool IsCloseGuardViable(
            bool bossAlive,
            bool botAlive,
            bool nativeEmergency,
            bool recovering,
            float distanceToBoss,
            ScourgeProtectionSettings settings)
        {
            return bossAlive && botAlive && !nativeEmergency && !recovering
                && IsWithinCloseGuardRadius(distanceToBoss, settings.CloseGuardRadius);
        }

        /// <summary>
        /// Exactly one stable close guard is selected by the squad context. This
        /// predicate keeps the assignment from spreading close duty to every
        /// follower when a squad has several viable members.
        /// </summary>
        public static bool ShouldHoldCloseGuard(
            bool botIsAssigned,
            bool bossAlive,
            bool botAlive,
            bool nativeEmergency,
            bool recovering,
            float distanceToBoss,
            ScourgeProtectionSettings settings)
        {
            return botIsAssigned && IsCloseGuardViable(
                bossAlive,
                botAlive,
                nativeEmergency,
                recovering,
                distanceToBoss,
                settings);
        }

        public static bool CanUseDestination(
            bool bossAlive,
            bool isBoss,
            float destinationDistanceToBoss,
            ScourgeProtectionSettings settings)
        {
            return !bossAlive || isBoss
                || IsWithinLeash(destinationDistanceToBoss, settings.LeashRadius);
        }

        public static bool CanUseDestination(
            bool bossAlive,
            bool isBoss,
            float currentDistanceToBoss,
            float destinationDistanceToBoss,
            ScourgeProtectionSettings settings)
        {
            return !bossAlive || isBoss
                || IsWithinLeash(destinationDistanceToBoss, settings.LeashRadius)
                || (IsBeyondLeash(currentDistanceToBoss, settings.LeashRadius)
                    && IsFiniteNonNegative(destinationDistanceToBoss)
                    && destinationDistanceToBoss < currentDistanceToBoss);
        }

        public static bool HasBossMoved(
            float displacement,
            float movementThreshold = DefaultReplanDistance)
        {
            if (!IsFiniteNonNegative(displacement) || !IsFinitePositive(movementThreshold))
            {
                return false;
            }

            return displacement >= movementThreshold;
        }

        private static bool IsFinitePositive(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
        }

        private static bool IsFiniteNonNegative(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f;
        }
    }
}
