using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace WaveByWave.Enemies
{
    public sealed partial class DotsEnemyRuntime
    {
        private struct SurfaceTransfer
        {
            public DotsEnemyState From, To;
            public Collider Source, Landing;
            public Vector3 EdgeFromLocal, EdgeToLocal;
            public float Progress;
            public bool Returning;
        }
        private readonly Dictionary<int, SurfaceTransfer> _surfaceTransfers = new();

        private void UpdateLocomotion(ref DotsEnemyState state, ref DotsEnemyBrain brain,
            float distance, float deltaTime, bool evaluated, float now)
        {
            if (brain.Attacking != 0 || state.StunUntil > now) { brain.LocomotionIdleTime = 0; return; }
            // A deferred edge search is not evidence that the character has stopped.
            if (!evaluated) return;
            if (distance >= Mathf.Max(0.004f, deltaTime * 0.08f))
            {
                brain.LocomotionIdleTime = 0;
                SetAnimation(ref state, EnemyAnimationState.Run);
            }
            else
            {
                brain.LocomotionIdleTime += deltaTime;
                if (brain.LocomotionIdleTime >= 0.25f)
                    SetAnimation(ref state, EnemyAnimationState.Idle);
            }
        }

        private void FaceTarget(ref DotsEnemyState state, DotsEnemyBrain brain, float deltaTime, float now)
        {
            if (brain.Target < 0 || state.StunUntil > now || brain.Attacking != 0 ||
                math.lengthsq(brain.Direction) < 0.0001f) return;
            var up = TryGetSurfaceFrame(state.SupportId, true, out var frame)
                ? frame.rotation * Vector3.up : Vector3.up;
            var forward = Vector3.ProjectOnPlane(brain.Direction, up);
            state.Rotation = math.slerp(state.Rotation, quaternion.LookRotationSafe(forward, up),
                1f - math.exp(-12f * deltaTime));
            UpdateLocal(ref state);
        }

        private static int NextSurfaceCursor(int cursor, int probes, int population, int edgeBudget) =>
            // A full pass used to reset to zero: the first 32 blocked enemies monopolized
            // the edge budget forever when the population fit inside the probe budget.
            (cursor + (probes == population ? math.min(probes, math.max(1, edgeBudget)) : probes)) % population;

        private static void ReleaseBoardedCrew(ref DotsEnemyBrain brain, DotsEnemyState state)
        {
            if (brain.SpawnGroup <= -1000000 &&
                (!IsShipSurface(state.SupportId) || CrewGroupForShip(unchecked((int)(uint)state.SupportId)) != brain.SpawnGroup))
                brain.SpawnGroup = 0; // Sinking the original ship must not despawn boarders.
        }

        private bool GroundAt(Vector3 feet, out RaycastHit hit) =>
            TryGround(feet + Vector3.up * (Catalog.StepHeight + 0.08f),
                Catalog.StepHeight + Catalog.MaximumDrop + 0.1f, out hit);

        private bool HasContinuousGround(Vector3 from, Vector3 to)
        {
            // Validate the interior as well as the destination: a long frame must not
            // let a skeleton walk across water simply because the opposite deck is hit.
            var steps = Mathf.CeilToInt(Vector3.Distance(from, to) / 0.12f);
            for (var i = 1; i < steps; i++)
                if (!GroundAt(Vector3.Lerp(from, to, i / (float)steps), out _)) return false;
            return true;
        }

        private bool TransferGroundAt(Vector3 feet, out RaycastHit hit)
        {
            var height = Mathf.Max(0.05f, Catalog.SurfaceTransferHeight);
            return TryGround(feet + Vector3.up * (height + 0.01f), height * 2f + 0.02f, out hit) &&
                Mathf.Abs(hit.point.y - feet.y) <= height;
        }

        private bool TryBeginSurfaceTransfer(DotsEnemyState state, Vector3 target)
        {
            var maximumGap = Mathf.Clamp(Catalog.MaximumSurfaceGap, 0f, 5f);
            if (maximumGap <= 0 || !TransferGroundAt(state.Position, out var source)) return false;
            Vector3 from = source.point;
            var toward = Vector3.ProjectOnPlane(target - from, Vector3.up);
            if (toward.sqrMagnitude < 0.0001f) return false;
            var rotation = TryGetSurfaceFrame(state.SupportId, true, out var frame) ? frame.rotation : Quaternion.identity;
            const float spacing = 0.1f;
            // Only search at a blocked edge, never across the entire water surface.
            for (var directionIndex = 0; directionIndex < 9; directionIndex++)
            {
                var direction = directionIndex == 5 ? rotation * Vector3.right : directionIndex == 6 ? rotation * Vector3.left
                    : directionIndex == 7 ? rotation * Vector3.forward : directionIndex == 8 ? rotation * Vector3.back
                    : Quaternion.AngleAxis(directionIndex == 0 ? 0 : ((directionIndex + 1) / 2) * 30f *
                        (directionIndex % 2 == 0 ? -1 : 1), Vector3.up) * toward.normalized;
                direction = Vector3.ProjectOnPlane(direction, Vector3.up).normalized;
                if (Vector3.Dot(direction, toward) <= 0) continue;
                var lastSolid = 0f;
                var firstEmpty = -1f;
                for (var distance = spacing; distance <= maximumGap + 0.4f; distance += spacing)
                {
                    var supported = TransferGroundAt(from + direction * distance, out var landing);
                    if (firstEmpty < 0)
                    {
                        if (supported) { lastSolid = distance; continue; }
                        firstEmpty = distance;
                        // Refine the departure edge so the setting measures water, not
                        // distance from the skeleton's centre to the other deck.
                        for (var refine = 0; refine < 6; refine++)
                        {
                            var middle = (lastSolid + firstEmpty) * 0.5f;
                            if (TransferGroundAt(from + direction * middle, out _)) lastSolid = middle;
                            else firstEmpty = middle;
                        }
                    }
                    if (distance - lastSolid > maximumGap + spacing) break;
                    if (!supported) continue;
                    var dry = distance;
                    var wet = distance - spacing;
                    for (var refine = 0; refine < 6; refine++)
                    {
                        var middle = (wet + dry) * 0.5f;
                        if (TransferGroundAt(from + direction * middle, out _)) dry = middle;
                        else wet = middle;
                    }
                    if (dry - lastSolid > maximumGap + 0.005f) break;
                    // Land slightly inside the destination, not on its numerical edge.
                    if (!TransferGroundAt(from + direction * (dry + 0.08f), out landing)) break;
                    if (Vector3.ProjectOnPlane(target - landing.point, Vector3.up).sqrMagnitude >= toward.sqrMagnitude) break;
                    var departure = state;
                    departure.Position = from;
                    AttachSurface(ref departure, source.collider);
                    var destination = state;
                    destination.Position = landing.point;
                    AttachSurface(ref destination, landing.collider);
                    _surfaceTransfers[state.Id] = new SurfaceTransfer
                    {
                        From = departure, To = destination, Source = source.collider, Landing = landing.collider,
                        EdgeFromLocal = SurfacePoint(departure.SupportId, from + direction * lastSolid, true),
                        EdgeToLocal = SurfacePoint(destination.SupportId, from + direction * dry, true)
                    };
                    return true;
                }
            }
            return false;
        }

        private Vector3 SurfacePoint(ulong support, Vector3 point, bool toLocal)
        {
            if (!TryGetSurfaceFrame(support, true, out var frame)) return point;
            return (toLocal ? frame.inverse : frame).MultiplyPoint3x4(point);
        }

        private bool AdvanceSurfaceTransfer(ref DotsEnemyState state, ref DotsEnemyBrain brain, float deltaTime, float now)
        {
            if (!_surfaceTransfers.TryGetValue(state.Id, out var transfer)) return false;
            Carry(ref transfer.From);
            Carry(ref transfer.To);
            var height = math.abs(transfer.From.Position.y - transfer.To.Position.y);
            var gap = Vector3.ProjectOnPlane(SurfacePoint(transfer.From.SupportId, transfer.EdgeFromLocal, false) -
                SurfacePoint(transfer.To.SupportId, transfer.EdgeToLocal, false), Vector3.up).magnitude;
            // Revalidate moving decks. No stale bridge to a ship that has sailed away.
            var valid = !transfer.Returning && transfer.Source != null && transfer.Source.enabled && transfer.Source.gameObject.activeInHierarchy &&
                transfer.Landing != null && transfer.Landing.enabled && transfer.Landing.gameObject.activeInHierarchy &&
                Catalog.MaximumSurfaceGap > 0 && gap <= Mathf.Clamp(Catalog.MaximumSurfaceGap, 0f, 5f) + 0.005f &&
                height <= Catalog.SurfaceTransferHeight;
            if (!valid && !transfer.Returning)
            {
                // Retreat from the current position, not from the now-displaced landing.
                // Otherwise a departing ship could yank the skeleton across the gap.
                transfer.Returning = true;
                transfer.To = state;
                transfer.Progress = 1f;
            }
            var span = math.distance(transfer.From.Position.xz, transfer.To.Position.xz);
            var speed = Catalog.MoveSpeed * deltaTime / Mathf.Max(0.01f, span);
            if (valid && state.StunUntil > now) speed = 0;
            transfer.Progress = Mathf.Clamp01(transfer.Progress + (valid ? speed : -speed));
            state.Position = math.lerp(transfer.From.Position, transfer.To.Position, transfer.Progress);
            state.SupportId = transfer.From.SupportId;
            UpdateLocal(ref state);
            brain.Knockback = float3.zero;
            brain.Attacking = 0;
            brain.LocomotionIdleTime = 0;
            SetAnimation(ref state, state.StunUntil > now ? EnemyAnimationState.Stunned : EnemyAnimationState.Run,
                state.StunUntil > now ? state.StunUntil - now : 0);
            if (valid && transfer.Progress >= 1f)
            {
                AttachSurface(ref state, transfer.Landing);
                ReleaseBoardedCrew(ref brain, state);
                _surfaceTransfers.Remove(state.Id);
            }
            else if (!valid && transfer.Progress <= 0)
                _surfaceTransfers.Remove(state.Id);
            else _surfaceTransfers[state.Id] = transfer;
            return true;
        }

        private bool TryFollowEdge(DotsEnemyState state, Vector3 target, float deltaTime, out RaycastHit best)
        {
            best = default;
            Vector3 from = state.Position;
            var toTarget = Vector3.ProjectOnPlane(target - from, Vector3.up);
            var direction = toTarget.normalized;
            if (direction.sqrMagnitude < 0.01f) return false;
            var distance = Catalog.MoveSpeed * deltaTime;
            var progress = 0.0001f;
            var right = Vector3.right;
            var forward = Vector3.forward;
            if (TryGetSurfaceFrame(state.SupportId, true, out var frame))
            {
                right = Vector3.ProjectOnPlane(frame.rotation * Vector3.right, Vector3.up).normalized;
                forward = Vector3.ProjectOnPlane(frame.rotation * Vector3.forward, Vector3.up).normalized;
            }
            // Re-evaluate against the current player direction on every surface turn.
            // Shortened steps approach the edge; oblique steps follow its boundary.
            for (var sample = 0; sample < 17; sample++)
            {
                var angle = sample == 0 ? 0 : ((sample + 1) / 2) * 15f * (sample % 2 == 0 ? -1 : 1);
                // Deck axes include the exact tangent of rotated rectangular hulls.
                var candidate = sample == 13 ? right : sample == 14 ? -right
                    : sample == 15 ? forward : sample == 16 ? -forward
                    : Quaternion.AngleAxis(angle, Vector3.up) * direction;
                for (var scale = 1f; scale >= 0.24f; scale *= 0.5f)
                {
                    var point = from + candidate * (distance * scale);
                    var offset = Vector3.ProjectOnPlane(point - from, Vector3.up);
                    var gain = toTarget.sqrMagnitude - (toTarget - offset).sqrMagnitude;
                    if (gain <= progress || !GroundAt(point, out var hit) ||
                        !HasContinuousGround(from, hit.point)) continue;
                    progress = gain;
                    best = hit;
                    break;
                }
            }
            return best.collider != null;
        }
    }
}
