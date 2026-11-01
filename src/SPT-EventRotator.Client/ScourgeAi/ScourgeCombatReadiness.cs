using System;

namespace EventRotator.ScourgeAi
{
    public static class ScourgeManeuverTiming
    {
        public const float CompletionRadius = 1.5f;
        public const float MinimumRouteTravel = 2.5f;
        public const float PathRetryCooldownSeconds = 2f;
        public const float ManeuverCooldownSeconds = 3f;
        public const float ObservedReplanCooldownSeconds = 0.35f;

        public static float NextPathAttempt(float now, bool observedReplan = false)
        {
            return IsFinite(now) ? now + (observedReplan ? ObservedReplanCooldownSeconds : PathRetryCooldownSeconds)
                : float.PositiveInfinity;
        }

        public static float NextManeuver(float now, bool observedReplan = false)
        {
            return IsFinite(now) ? now + (observedReplan ? ObservedReplanCooldownSeconds : ManeuverCooldownSeconds)
                : float.PositiveInfinity;
        }

        public static bool IsComplete(float traveledDistance)
        {
            return IsFinite(traveledDistance) && traveledDistance <= CompletionRadius;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    public static class ScourgeRouteLifecycle
    {
        public const float MinimumObservedReplanAge = 1f;
        public const float MinimumObservedReplanDistance = 5f;

        public static bool ShouldReplanForObservedMove(
            string routeTargetId,
            ScourgeTactic routeTactic,
            string currentTargetId,
            ScourgeTactic currentTactic,
            float routeAge,
            float observedDisplacement)
        {
            if (routeTactic != ScourgeTactic.Assault && routeTactic != ScourgeTactic.Search)
                return false;
            if (!string.Equals(routeTargetId, currentTargetId, StringComparison.Ordinal)
                || routeTactic != currentTactic)
                return false;
            return IsFinite(routeAge) && routeAge >= MinimumObservedReplanAge
                && IsFinite(observedDisplacement)
                && observedDisplacement >= MinimumObservedReplanDistance;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    public static class ScourgeCombatReadiness
    {
        public static bool ShouldReloadDuringBreak(int rounds, int capacity, float sinceSeen,
            float sinceHit, bool visible, bool covered, bool spareAmmo, bool emergency, bool supporting)
        {
            return !visible && covered && spareAmmo && !emergency && !supporting
                && capacity > 1 && rounds > 0 && rounds <= capacity * 0.75f
                && !float.IsNaN(sinceSeen) && !float.IsInfinity(sinceSeen) && sinceSeen >= 5.5f
                && !float.IsNaN(sinceHit) && sinceHit >= 4f;
        }
    }

    // Consume each native hit once. Repeated damage cannot continually restart a route.
    public sealed class ScourgeHitReaction
    {
        private float _observedHit;
        private float _pendingUntil = float.NegativeInfinity;
        private float _nextReaction;

        public void Observe(float hitTime, float now)
        {
            if (float.IsNaN(hitTime) || float.IsInfinity(hitTime) || hitTime <= _observedHit || hitTime > now)
                return;
            _observedHit = hitTime;
            if (now >= _nextReaction && now - hitTime <= 2f)
                _pendingUntil = now + 2f;
        }

        public bool Pending(float now) => now < _pendingUntil;

        public void Consume(float now)
        {
            _pendingUntil = float.NegativeInfinity;
            _nextReaction = now + 6f;
        }

        public void FinishManeuver(ScourgeTactic endedTactic, float now)
        {
            // Ending the old route must not consume the hit that interrupted it.
            if (endedTactic == ScourgeTactic.Reposition && Pending(now)) Consume(now);
        }
    }
}


