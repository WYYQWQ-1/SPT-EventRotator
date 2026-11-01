using System;

namespace EventRotator.ScourgeAi
{
    public enum ScourgeTactic
    {
        None,
        Hold,
        Assault,
        Flank,
        Search,
        Withdraw,
        Reposition
    }

    public readonly struct ScourgeRoleTuning
    {
        public int Role { get; }
        public float PreferredDistance { get; }
        public float MinimumDistance { get; }
        public bool PrefersFlank { get; }

        public ScourgeRoleTuning(
            int role,
            float preferredDistance,
            float minimumDistance,
            bool prefersFlank)
        {
            Role = role;
            PreferredDistance = preferredDistance;
            MinimumDistance = minimumDistance;
            PrefersFlank = prefersFlank;
        }

        public static bool TryGet(int role, out ScourgeRoleTuning tuning)
        {
            switch (role)
            {
                case ScourgeRoles.BossValue:
                    // The anchor holds a conservative band and never takes a flank
                    // assignment that could pull protection away from the squad.
                    tuning = new ScourgeRoleTuning(role, 32f, 12f, false);
                    return true;
                case ScourgeRoles.FollowerValue:
                    tuning = new ScourgeRoleTuning(role, 24f, 7f, true);
                    return true;
                default:
                    tuning = default(ScourgeRoleTuning);
                    return false;
            }
        }

        public static ScourgeRoleTuning ForRole(int role)
        {
            return TryGet(role, out var tuning)
                ? tuning
                : default(ScourgeRoleTuning);
        }

        public ScourgeRoleTuning WithAggression(float aggressionMultiplier)
        {
            var multiplier = NormalizeAggression(aggressionMultiplier);
            return new ScourgeRoleTuning(Role,
                PreferredDistance / multiplier,
                MinimumDistance / multiplier,
                PrefersFlank);
        }

        public static bool TryGet(int role, float aggressionMultiplier, out ScourgeRoleTuning tuning)
        {
            if (!TryGet(role, out var baseline))
            {
                tuning = default(ScourgeRoleTuning);
                return false;
            }

            tuning = baseline.WithAggression(aggressionMultiplier);
            return true;
        }

        public static ScourgeRoleTuning ForRole(int role, float aggressionMultiplier)
        {
            return TryGet(role, aggressionMultiplier, out var tuning)
                ? tuning
                : default(ScourgeRoleTuning);
        }

        private static float NormalizeAggression(float aggressionMultiplier)
        {
            if (float.IsNaN(aggressionMultiplier) || float.IsInfinity(aggressionMultiplier))
            {
                return 1f;
            }

            return Math.Max(1f, Math.Min(2f, aggressionMultiplier));
        }
    }

    public readonly struct ScourgeTacticalSettings
    {
        public const float DefaultContactMemorySeconds = 12f;
        public const float DefaultInitialSuppressSeconds = 2f;
        public const float BaselineAggressionMultiplier = 1f;
        public const float MinimumAggressionMultiplier = 1f;
        public const float MaximumAggressionMultiplier = 2f;
        public const float MaximumEffectiveContactMemorySeconds = 30f;

        public float ContactMemorySeconds { get; }
        public float InitialSuppressSeconds { get; }
        public float AggressionMultiplier { get; }

        public float EffectiveContactMemorySeconds => ScaleContactMemory(ContactMemorySeconds, AggressionMultiplier);
        public float EffectiveInitialSuppressSeconds => ScaleOpeningFire(InitialSuppressSeconds, AggressionMultiplier);

        public ScourgeTacticalSettings(
            float contactMemorySeconds = DefaultContactMemorySeconds,
            float initialSuppressSeconds = DefaultInitialSuppressSeconds,
            float aggressionMultiplier = BaselineAggressionMultiplier)
        {
            ContactMemorySeconds = contactMemorySeconds;
            InitialSuppressSeconds = initialSuppressSeconds;
            AggressionMultiplier = NormalizeAggression(aggressionMultiplier);
        }

        public static ScourgeTacticalSettings Default => new ScourgeTacticalSettings(
            DefaultContactMemorySeconds,
            DefaultInitialSuppressSeconds,
            BaselineAggressionMultiplier);

        private static float NormalizeAggression(float aggressionMultiplier)
        {
            if (float.IsNaN(aggressionMultiplier) || float.IsInfinity(aggressionMultiplier))
            {
                return BaselineAggressionMultiplier;
            }

            return Math.Max(MinimumAggressionMultiplier,
                Math.Min(MaximumAggressionMultiplier, aggressionMultiplier));
        }

        private static float ScaleContactMemory(float contactMemorySeconds, float aggressionMultiplier)
        {
            if (float.IsNaN(contactMemorySeconds) || float.IsInfinity(contactMemorySeconds)
                || contactMemorySeconds < 0f)
            {
                return contactMemorySeconds;
            }

            return Math.Min(MaximumEffectiveContactMemorySeconds,
                contactMemorySeconds * NormalizeAggression(aggressionMultiplier));
        }

        private static float ScaleOpeningFire(float initialSuppressSeconds, float aggressionMultiplier)
        {
            if (float.IsNaN(initialSuppressSeconds) || float.IsInfinity(initialSuppressSeconds)
                || initialSuppressSeconds < 0f)
            {
                return initialSuppressSeconds;
            }

            return initialSuppressSeconds / NormalizeAggression(aggressionMultiplier);
        }
    }

    public struct ScourgeTacticalInput
    {
        public int Role { get; set; }
        public bool HasTarget { get; set; }
        public bool Visible { get; set; }
        public float TimeSinceSeen { get; set; }
        public float Distance { get; set; }
        public bool Emergency { get; set; }
        public bool CanManeuver { get; set; }
        public bool CanEngage { get; set; }
        public bool HasSupport { get; set; }
        public float ElapsedSinceContact { get; set; }
        public bool RepositionAfterHit { get; set; }
        public bool SupportingFlank { get; set; }

        public ScourgeTacticalInput(
            int role,
            bool hasTarget,
            bool visible,
            float timeSinceSeen,
            float distance,
            bool emergency,
            bool canManeuver,
            bool canEngage,
            bool hasSupport,
            float elapsedSinceContact)
        {
            Role = role;
            HasTarget = hasTarget;
            Visible = visible;
            TimeSinceSeen = timeSinceSeen;
            Distance = distance;
            Emergency = emergency;
            CanManeuver = canManeuver;
            CanEngage = canEngage;
            HasSupport = hasSupport;
            ElapsedSinceContact = elapsedSinceContact;
            RepositionAfterHit = false;
            SupportingFlank = false;
        }
    }

    public static class ScourgeTacticalPolicy
    {
        public static ScourgeTactic Decide(ScourgeTacticalInput input)
        {
            return Decide(input, ScourgeTacticalSettings.Default);
        }

        public static ScourgeTactic Decide(
            ScourgeTacticalInput input,
            ScourgeTacticalSettings settings)
        {
            if (input.Emergency || !input.HasTarget)
            {
                return ScourgeTactic.None;
            }

            if (!ScourgeRoleTuning.TryGet(input.Role, settings.AggressionMultiplier, out var tuning))
            {
                return ScourgeTactic.None;
            }

            var contactMemorySeconds = settings.EffectiveContactMemorySeconds;
            var initialSuppressSeconds = settings.EffectiveInitialSuppressSeconds;
            if (input.RepositionAfterHit && (input.Visible || !IsExpired(input.TimeSinceSeen, contactMemorySeconds)))
                return ScourgeTactic.Reposition;

            if (!input.Visible)
            {
                if (IsExpired(input.TimeSinceSeen, contactMemorySeconds))
                {
                    return ScourgeTactic.None;
                }

                // Reaching the observed area means watch it, not repeatedly walking
                // through the point where an unseen enemy used to stand.
                return input.CanManeuver && input.Distance > 3f
                    ? ScourgeTactic.Search
                    : ScourgeTactic.Hold;
            }

            // A current target is still actionable even when its last-seen timer is large.
            // A bot without a usable weapon should keep its place instead of advancing.
            if (!input.CanEngage)
            {
                return ScourgeTactic.Hold;
            }

            // Withdrawal is a safety move; the runtime can issue it without holding a maneuver lease.
            if (input.Distance < tuning.MinimumDistance)
            {
                return ScourgeTactic.Withdraw;
            }

            // Close-range safety takes precedence over a covering assignment.
            if (input.SupportingFlank) return ScourgeTactic.Hold;

            // Initial contact gives the squad a short suppression window before a maneuver.
            if (!float.IsNaN(initialSuppressSeconds) && !float.IsInfinity(initialSuppressSeconds)
                && input.ElapsedSinceContact <= initialSuppressSeconds)
            {
                return ScourgeTactic.Hold;
            }

            if (input.Distance > tuning.PreferredDistance)
            {
                return input.CanManeuver
                    ? ScourgeTactic.Assault
                    : ScourgeTactic.Hold;
            }

            // Recon and WPU roles hold the line once they are in their preferred band.
            if (!tuning.PrefersFlank)
            {
                return ScourgeTactic.Hold;
            }

            // A flank always needs both a route and another bot to cover the move.
            if (input.HasSupport && input.CanManeuver)
            {
                return ScourgeTactic.Flank;
            }

            return ScourgeTactic.Hold;
        }

        private static bool IsExpired(float timeSinceSeen, float contactMemorySeconds)
        {
            if (float.IsNaN(timeSinceSeen) || float.IsInfinity(timeSinceSeen)
                || float.IsNaN(contactMemorySeconds) || float.IsInfinity(contactMemorySeconds))
            {
                return true;
            }

            return timeSinceSeen > contactMemorySeconds;
        }
    }
}


