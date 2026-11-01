using System.Linq;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace EventRotator.ScourgeAi
{
    // Small, bounded probes work on each map's navigation mesh. No map coordinates
    // or hidden enemy transforms are used to choose a destination.
    internal static class ScourgeNavigation
    {
        private const float SampleRadius = 1.5f;
        private const float MaxCrowdDistance = 2f;
        private const float MaxPathProbeDistance = 32f;

        private readonly struct Candidate
        {
            public readonly Vector3 Position;
            public readonly float Score;
            public readonly float PathDistance;

            public Candidate(Vector3 position, float score, float pathDistance)
            {
                Position = position;
                Score = score;
                PathDistance = pathDistance;
            }
        }

        public static bool IsCovered(Vector3 position, Vector3 threat) =>
            Physics.Linecast(threat + Vector3.up, position + Vector3.up * 0.7f,
                LayersMaskController.HighPolyWithTerrainMask)
            && Physics.Linecast(threat + Vector3.up, position + Vector3.up * 1.2f,
                LayersMaskController.HighPolyWithTerrainMask);

        public static bool ClearShot(Vector3 origin, Vector3 point) =>
            !Physics.Linecast(origin, point, LayersMaskController.HighPolyWithTerrainMask);

        public static bool TryDestination(BotOwner bot, Vector3 knownThreat, ScourgeTactic tactic,
            bool requireCover, IReadOnlyList<BotOwner> squad, out Vector3 destination, Vector3? supporter = null,
            float minimumThreatDistance = 3f, Vector3? protectionCenter = null,
            float protectionLeashRadius = ScourgeProtectionPolicy.DefaultLeashRadius,
            bool protectionAnchor = false)
        {
            return TryDestination(bot, knownThreat, tactic, requireCover, squad,
                out destination, out _, supporter, minimumThreatDistance,
                protectionCenter, protectionLeashRadius, protectionAnchor);
        }

        public static bool TryDestination(BotOwner bot, Vector3 knownThreat, ScourgeTactic tactic,
            bool requireCover, IReadOnlyList<BotOwner> squad, out Vector3 destination,
            out float pathDistance, Vector3? supporter = null, float minimumThreatDistance = 3f,
            Vector3? protectionCenter = null,
            float protectionLeashRadius = ScourgeProtectionPolicy.DefaultLeashRadius,
            bool protectionAnchor = false)
        {
            destination = bot != null ? bot.Position : default(Vector3);
            pathDistance = 0f;
            if (bot == null || squad == null) return false;

            var forward = knownThreat - bot.Position;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = bot.GetPlayer.Transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) return false;
            forward.Normalize();
            var side = Vector3.Cross(Vector3.up, forward);
            var distance = Vector3.Distance(bot.Position, knownThreat);
            var step = Mathf.Clamp(distance - 3f, 2f, 10f);
            var candidates = new List<Candidate>(12);
            var path = new NavMeshPath();
            var flank = tactic == ScourgeTactic.Flank;
            if (flank && !supporter.HasValue) return false;

            // Search and assault first try the complete path to the observed position.
            // The selected point is an intermediate path corner when the target is
            // behind an obstacle, so the route follows the mesh instead of walking a
            // straight geometric probe through a wall.
            if ((tactic == ScourgeTactic.Search || tactic == ScourgeTactic.Assault)
                && TryLastKnownApproach(bot, knownThreat, tactic, squad, path, minimumThreatDistance,
                    protectionCenter, protectionLeashRadius, protectionAnchor,
                    out var approach, out var approachDistance))
            {
                candidates.Add(new Candidate(approach, approachDistance, approachDistance));
            }

            var supportBearing = supporter.GetValueOrDefault() - knownThreat;
            supportBearing.y = 0f;
            var startingBearing = bot.Position - knownThreat;
            startingBearing.y = 0f;
            var startingAngle = flank ? Vector3.Angle(supportBearing, startingBearing) : 0f;
            // This loop generates bounded geometric probes for cover, flank, and the
            // fallback when an observed destination cannot be sampled directly.
            for (var index = 0; index < 10; index++)
            {
                var sign = index % 2 == 0 ? 1f : -1f;
                var scale = 0.5f + (index / 2) * 0.2f;
                Vector3 offset;
                if (requireCover || tactic == ScourgeTactic.Withdraw)
                    offset = (-forward * 6f + side * sign * (2f + index)) * scale;
                else if (tactic == ScourgeTactic.Flank)
                    offset = (forward * 3f + side * sign * 9f) * scale;
                else
                    offset = (forward * step + side * sign * (index / 2)) * scale;

                if (!NavMesh.SamplePosition(bot.Position + offset, out var hit, SampleRadius, NavMesh.AllAreas)
                    || Mathf.Abs(hit.position.y - bot.Position.y) > 2f
                    || !IsUsableDestination(hit.position, bot, squad,
                        protectionCenter, protectionLeashRadius, protectionAnchor)
                    || (tactic == ScourgeTactic.Assault
                        && Vector3.Distance(hit.position, knownThreat) < minimumThreatDistance)) continue;
                if (!CalculateCompletePath(bot.Position, hit.position, path, out var length)) continue;
                if (length > MaxPathProbeDistance) continue;
                if (!IsPathWithinProtection(path.corners, protectionCenter, protectionLeashRadius,
                    protectionAnchor, bot.Position)) continue;

                var angle = 0f;
                if (flank)
                {
                    var bearing = hit.position - knownThreat;
                    bearing.y = 0f;
                    angle = Vector3.Angle(supportBearing, bearing);
                    // A flank must open a different firing angle and finish with LOS.
                    // Reject points beside the covering shooter or across its firing lane.
                    if (angle < 20f || angle < startingAngle + 10f || angle > 100f
                        || !ClearShot(hit.position + Vector3.up * 1.5f, knownThreat + Vector3.up)
                        || PathCrossesFireLane(path.corners, supporter.Value, knownThreat)) continue;
                }

                var covered = IsCovered(hit.position, knownThreat);
                if (requireCover && !covered) continue;
                var score = length + (covered ? -4f : 0f);
                if (flank) score -= angle * 0.15f;
                if (tactic == ScourgeTactic.Assault || tactic == ScourgeTactic.Search)
                    score += Vector3.Distance(hit.position, knownThreat);
                candidates.Add(new Candidate(hit.position, score, length));
            }

            if (candidates.Count == 0) return false;
            var selected = candidates.OrderBy(candidate => candidate.Score).First();
            destination = selected.Position;
            pathDistance = selected.PathDistance;
            return true;
        }

        private static bool TryLastKnownApproach(BotOwner bot, Vector3 knownThreat, ScourgeTactic tactic,
            IReadOnlyList<BotOwner> squad, NavMeshPath path, float minimumThreatDistance,
            Vector3? protectionCenter, float protectionLeashRadius, bool protectionAnchor,
            out Vector3 destination, out float pathDistance)
        {
            destination = bot.Position;
            pathDistance = 0f;
            if (!NavMesh.SamplePosition(knownThreat, out var threatHit, 2.5f, NavMesh.AllAreas)
                || Mathf.Abs(threatHit.position.y - knownThreat.y) > 2f
                || !CalculateCompletePath(bot.Position, threatHit.position, path, out var completeDistance))
                return false;
            var stopDistance = tactic == ScourgeTactic.Assault
                ? Mathf.Max(3f, minimumThreatDistance)
                : 0f;
            var travelDistance = completeDistance - stopDistance;
            if (travelDistance < ScourgeManeuverTiming.MinimumRouteTravel) return false;
            travelDistance = Mathf.Min(travelDistance, MaxPathProbeDistance);
            var intendedPoint = PointAlongPath(path.corners, travelDistance);
            if (!NavMesh.SamplePosition(intendedPoint, out var destinationHit, 0.75f, NavMesh.AllAreas)
                || Mathf.Abs(destinationHit.position.y - intendedPoint.y) > 0.75f
                || !CalculateCompletePath(bot.Position, destinationHit.position, path, out var actualPathDistance)
                || actualPathDistance > MaxPathProbeDistance) return false;
            if (!IsPathWithinProtection(path.corners, protectionCenter, protectionLeashRadius,
                protectionAnchor, bot.Position)) return false;
            destination = destinationHit.position;
            if (!IsUsableDestination(destination, bot, squad,
                    protectionCenter, protectionLeashRadius, protectionAnchor)
                || (tactic == ScourgeTactic.Assault
                    && Vector3.Distance(destination, knownThreat) < stopDistance)) return false;
            pathDistance = actualPathDistance;
            return true;
        }

        private static bool CalculateCompletePath(Vector3 start, Vector3 end, NavMeshPath path, out float length)
        {
            length = 0f;
            if (!NavMesh.CalculatePath(start, end, NavMesh.AllAreas, path)
                || path.status != NavMeshPathStatus.PathComplete
                || path.corners == null || path.corners.Length < 2)
                return false;

            length = PathLength(path.corners);
            return length >= ScourgeManeuverTiming.MinimumRouteTravel;
        }

        private static float PathLength(Vector3[] corners)
        {
            return corners.Select((point, index) =>
                index == 0 ? 0f : Vector3.Distance(corners[index - 1], point)).Sum();
        }

        private static Vector3 PointAlongPath(Vector3[] corners, float distance)
        {
            if (corners == null || corners.Length == 0) return default(Vector3);
            var remaining = Mathf.Max(0f, distance);
            for (var index = 1; index < corners.Length; index++)
            {
                var segment = Vector3.Distance(corners[index - 1], corners[index]);
                if (segment <= 0.01f) continue;
                if (remaining <= segment)
                    return Vector3.Lerp(corners[index - 1], corners[index], remaining / segment);
                remaining -= segment;
            }
            return corners[corners.Length - 1];
        }

        private static bool IsUsableDestination(
            Vector3 destination,
            BotOwner bot,
            IReadOnlyList<BotOwner> squad,
            Vector3? protectionCenter,
            float protectionLeashRadius,
            bool protectionAnchor)
        {
            if ((destination - bot.Position).sqrMagnitude
                < ScourgeManeuverTiming.MinimumRouteTravel * ScourgeManeuverTiming.MinimumRouteTravel)
                return false;
            if (protectionCenter.HasValue && !protectionAnchor
                && !ScourgeProtectionPolicy.CanUseDestination(
                    true,
                    false,
                    Vector3.Distance(bot.Position, protectionCenter.Value),
                    Vector3.Distance(destination, protectionCenter.Value),
                    new ScourgeProtectionSettings(protectionLeashRadius,
                        ScourgeProtectionPolicy.DefaultCloseGuardRadius)))
                return false;
            return !squad.Any(member => member != null && member != bot
                && !member.IsDead && (member.Position - destination).sqrMagnitude
                < MaxCrowdDistance * MaxCrowdDistance);
        }

        private static bool IsPathWithinProtection(
            Vector3[] corners,
            Vector3? protectionCenter,
            float protectionLeashRadius,
            bool protectionAnchor,
            Vector3 startPosition)
        {
            if (!protectionCenter.HasValue || protectionAnchor) return true;
            if (corners == null || corners.Length == 0) return false;
            var startingDistance = Vector3.Distance(startPosition, protectionCenter.Value);
            var previousDistance = startingDistance;
            for (var index = 0; index < corners.Length; index++)
            {
                var distance = Vector3.Distance(corners[index], protectionCenter.Value);
                if (index == 0 && Mathf.Abs(distance - previousDistance) <= 0.1f)
                {
                    previousDistance = distance;
                    continue;
                }
                if (!ScourgeProtectionPolicy.IsWithinLeash(distance, protectionLeashRadius)
                    && !(ScourgeProtectionPolicy.IsBeyondLeash(previousDistance, protectionLeashRadius)
                        && distance < previousDistance))
                    return false;
                previousDistance = distance;
            }

            return !ScourgeProtectionPolicy.IsBeyondLeash(startingDistance, protectionLeashRadius)
                || previousDistance < startingDistance;
        }

        private static bool CrossesFireLane(Vector3 point, Vector3 shooter, Vector3 target)
        {
            var lane = target - shooter;
            lane.y = 0f;
            var offset = point - shooter;
            offset.y = 0f;
            if (lane.sqrMagnitude < 1f) return true;
            var projection = Vector3.Dot(offset, lane) / lane.sqrMagnitude;
            return projection > 0.05f && projection < 1f
                && (offset - lane * projection).sqrMagnitude < 2.25f;
        }

        private static bool PathCrossesFireLane(Vector3[] corners, Vector3 shooter, Vector3 target)
        {
            // Sample path segments too: their endpoints alone can miss a crossing.
            for (var index = 1; index < corners.Length; index++)
            {
                var count = Mathf.CeilToInt(Vector3.Distance(corners[index - 1], corners[index]) / 0.75f);
                for (var sample = 0; sample <= count; sample++)
                    if (CrossesFireLane(Vector3.Lerp(corners[index - 1], corners[index],
                        count == 0 ? 0f : (float)sample / count), shooter, target)) return true;
            }
            return false;
        }
    }
}


