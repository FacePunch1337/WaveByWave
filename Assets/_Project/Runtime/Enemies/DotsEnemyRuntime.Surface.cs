using System;
using System.Collections.Generic;
using Unity.Collections;
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
        private readonly struct MovementCrowdKey : IEquatable<MovementCrowdKey>
        {
            public readonly ulong Support;
            public readonly int2 Cell;
            public MovementCrowdKey(ulong support, int2 cell) { Support = support; Cell = cell; }
            public bool Equals(MovementCrowdKey other) => Support == other.Support && math.all(Cell == other.Cell);
            public override bool Equals(object value) => value is MovementCrowdKey other && Equals(other);
            public override int GetHashCode() => (int)math.hash(new uint4((uint)Support,
                (uint)(Support >> 32), (uint)Cell.x, (uint)Cell.y));
        }
        private struct MovementCrowdBody
        {
            public DotsEnemyState State;
            public MovementCrowdKey Key;
            public int CellIndex;
        }
        private readonly Dictionary<MovementCrowdKey, List<int>> _movementCrowdGrid = new();
        private readonly Dictionary<int, MovementCrowdBody> _movementCrowdBodies = new();
        private readonly Stack<List<int>> _movementCrowdPool = new();
        private float _movementCrowdCellSize = 1f;
        private bool _movementCrowdReady;

        private static float SmoothSurfaceHeight(float current, float target, float speed, float deltaTime) =>
            Mathf.MoveTowards(current, target, Mathf.Max(0.1f, speed) * deltaTime);

        private static float3 MovementCrowdPoint(in DotsEnemyState state) =>
            state.SupportId != 0 ? state.LocalPosition : state.Position;

        private MovementCrowdKey MovementCrowdKeyFor(in DotsEnemyState state)
        {
            var point = MovementCrowdPoint(in state);
            return new MovementCrowdKey(state.SupportId, (int2)math.floor(point.xz / _movementCrowdCellSize));
        }

        private void ClearMovementCrowdIndex()
        {
            foreach (var list in _movementCrowdGrid.Values)
            {
                list.Clear();
                _movementCrowdPool.Push(list);
            }
            _movementCrowdGrid.Clear();
            _movementCrowdBodies.Clear();
            _movementCrowdReady = false;
        }

        private void PrepareMovementCrowdIndex()
        {
            if (!Catalog.EnableCrowdCollisions)
            {
                if (_movementCrowdReady) ClearMovementCrowdIndex();
                return;
            }
            var minimum = math.max(Catalog.CrowdSeparationRadius, Catalog.BodyRadius * 2f + 0.04f);
            var cellSize = math.max(minimum, Catalog.MoveSpeed * 0.2f + minimum);
            if (_movementCrowdReady && math.abs(cellSize - _movementCrowdCellSize) < 0.0001f) return;
            ClearMovementCrowdIndex();
            _movementCrowdCellSize = cellSize;
            if (_serverWorld == null || !_serverWorld.IsCreated) return;
            using var bodies = _enemies.ToComponentDataArray<DotsEnemyState>(Allocator.Temp);
            for (var i = 0; i < bodies.Length; i++)
            {
                var state = bodies[i];
                if (state.Health <= 0 || state.Scene != _scene) continue;
                AddMovementCrowdBody(in state);
            }
            _movementCrowdReady = true;
        }

        private void AddMovementCrowdBody(in DotsEnemyState state)
        {
            var key = MovementCrowdKeyFor(in state);
            if (!_movementCrowdGrid.TryGetValue(key, out var list))
            {
                list = _movementCrowdPool.Count > 0 ? _movementCrowdPool.Pop() : new List<int>(8);
                _movementCrowdGrid.Add(key, list);
            }
            var body = new MovementCrowdBody { State = state, Key = key, CellIndex = list.Count };
            list.Add(state.Id);
            _movementCrowdBodies[state.Id] = body;
        }

        private void RemoveMovementCrowdBody(int id)
        {
            if (!_movementCrowdBodies.Remove(id, out var body) ||
                !_movementCrowdGrid.TryGetValue(body.Key, out var list)) return;
            var lastIndex = list.Count - 1;
            if (body.CellIndex < 0 || body.CellIndex > lastIndex) return;
            var movedId = list[lastIndex];
            list[body.CellIndex] = movedId;
            list.RemoveAt(lastIndex);
            if (movedId != id && _movementCrowdBodies.TryGetValue(movedId, out var moved))
            {
                moved.CellIndex = body.CellIndex;
                _movementCrowdBodies[movedId] = moved;
            }
            if (list.Count != 0) return;
            _movementCrowdGrid.Remove(body.Key);
            _movementCrowdPool.Push(list);
        }

        private void UpdateCrowdSnapshot(in DotsEnemyState state)
        {
            if (!_movementCrowdReady) return;
            if (state.Health <= 0 || state.Scene != _scene)
            {
                RemoveMovementCrowdBody(state.Id);
                return;
            }
            if (!_movementCrowdBodies.TryGetValue(state.Id, out var previous))
            {
                AddMovementCrowdBody(in state);
                return;
            }
            var key = MovementCrowdKeyFor(in state);
            if (key.Equals(previous.Key))
            {
                previous.State = state;
                _movementCrowdBodies[state.Id] = previous;
                return;
            }
            RemoveMovementCrowdBody(state.Id);
            AddMovementCrowdBody(in state);
        }

        private float3 SteerCrowdStep(in DotsEnemyState state, ref DotsEnemyBrain brain,
            float3 displacement, float deltaTime)
        {
            if (!_movementCrowdReady || !Catalog.EnableCrowdCollisions)
            {
                brain.ContactAvoidance = float2.zero;
                return displacement;
            }
            if (math.lengthsq(displacement.xz) < 0.000001f)
            {
                brain.ContactAvoidance *= math.exp(-4f * math.max(0.02f, deltaTime));
                return displacement;
            }
            var length = math.length(displacement.xz);
            var forward = displacement.xz / length;
            var surfaceRotation = quaternion.identity;
            var frame = Matrix4x4.identity;
            var hasSurfaceFrame = state.SupportId != 0 &&
                TryGetSurfaceFrame(state.SupportId, true, out frame);
            if (hasSurfaceFrame)
            {
                surfaceRotation = frame.rotation;
                var localForward = math.mul(math.inverse(surfaceRotation),
                    new float3(forward.x, 0, forward.y));
                forward = math.normalizesafe(localForward.xz, forward);
            }
            if (brain.ContactSupport != state.SupportId)
            {
                brain.ContactSupport = state.SupportId;
                brain.ContactAvoidance = float2.zero;
            }
            var side = brain.ContactSide;
            if (side == 0) side = (math.hash(new int2(state.Id, 1187)) & 1u) == 0 ? 1 : -1;
            brain.ContactSide = side;
            var right = new float2(forward.y, -forward.x);
            var point = MovementCrowdPoint(in state);
            var cell = (int2)math.floor(point.xz / _movementCrowdCellSize);
            var minimum = math.max(Catalog.CrowdSeparationRadius,
                Catalog.BodyRadius * 2f + 0.04f);
            var range = math.min(_movementCrowdCellSize, math.max(minimum * 2f, minimum + length));
            var rangeSq = range * range;
            var pressure = float2.zero;
            for (var z = -1; z <= 1; z++)
            for (var x = -1; x <= 1; x++)
            {
                var key = new MovementCrowdKey(state.SupportId, cell + new int2(x, z));
                if (!_movementCrowdGrid.TryGetValue(key, out var neighbours)) continue;
                var samples = math.min(32, neighbours.Count);
                var first = neighbours.Count > 0
                    ? (int)(math.hash(new int3(state.Id, key.Cell.x, key.Cell.y)) % (uint)neighbours.Count) : 0;
                for (var sample = 0; sample < samples; sample++)
                {
                    var otherId = neighbours[(first + sample) % neighbours.Count];
                    if (otherId == state.Id || !_movementCrowdBodies.TryGetValue(otherId, out var body)) continue;
                    var other = body.State;
                    var otherPoint = MovementCrowdPoint(in other);
                    if (other.Health <= 0 || other.SupportId != state.SupportId ||
                        math.abs(otherPoint.y - point.y) > Catalog.BodyHeight * 0.75f) continue;
                    var away = point.xz - otherPoint.xz;
                    var distanceSq = math.lengthsq(away);
                    if (distanceSq >= rangeSq) continue;
                    float distance;
                    if (distanceSq > 0.000001f)
                    {
                        distance = math.sqrt(distanceSq);
                        away /= distance;
                    }
                    else
                    {
                        var low = math.min(state.Id, otherId);
                        var high = math.max(state.Id, otherId);
                        var angle = (math.hash(new int2(low, high)) & 65535u) *
                            (math.PI * 2f / 65535f);
                        away = new float2(math.cos(angle), math.sin(angle));
                        if (state.Id > otherId) away = -away;
                        distance = 0;
                    }
                    var weight = 1f - distance / range;
                    var ahead = math.saturate(math.dot(-away, forward));
                    pressure += away * weight + right * (side * ahead * weight * 0.65f);
                }
            }
            var pressureLength = math.length(pressure);
            var targetAvoidance = pressureLength > 0.0001f
                ? pressure / pressureLength * math.saturate(pressureLength) : float2.zero;
            var response = 1f - math.exp(-6f * math.max(0.02f, deltaTime));
            brain.ContactAvoidance = math.lerp(brain.ContactAvoidance, targetAvoidance, response);
            var heading = math.normalizesafe(forward + brain.ContactAvoidance * 1.35f, forward);
            var worldHeading = new float3(heading.x, 0, heading.y);
            if (hasSurfaceFrame) worldHeading = math.mul(surfaceRotation, worldHeading);
            var horizontal = math.normalizesafe(worldHeading.xz, displacement.xz / length) * length;
            return new float3(horizontal.x, displacement.y, horizontal.y);
        }

        private void UpdateLocomotion(ref DotsEnemyState state, ref DotsEnemyBrain brain,
            float distance, float deltaTime, bool evaluated, bool wantsToMove, float now)
        {
            // Only actual relative movement counts. Carrying on waves and deferred surface
            // probes must not report a successful flank or an impassable one respectively.
            if (evaluated)
                brain.CrowdBlockedTime = wantsToMove && brain.Attacking == 0 && state.StunUntil <= now &&
                    distance < Mathf.Max(0.004f, Catalog.MoveSpeed * deltaTime * 0.15f)
                    ? brain.CrowdBlockedTime + deltaTime : 0;
            var animation = ResolveLocomotionAnimation(state.Animation, brain.Attacking != 0,
                state.StunUntil > now, distance, deltaTime, evaluated, wantsToMove,
                ref brain.LocomotionIdleTime);
            if (animation != state.Animation) SetAnimation(ref state, animation);
        }

        private static EnemyAnimationState ResolveLocomotionAnimation(EnemyAnimationState current,
            bool attacking, bool stunned, float distance, float deltaTime, bool evaluated,
            bool wantsToMove, ref float idleTime)
        {
            if (attacking || stunned) { idleTime = 0; return current; }
            // A deferred edge search is not evidence that the character has stopped.
            if (!evaluated) return current;
            if (distance >= Mathf.Max(0.004f, deltaTime * 0.08f))
            {
                idleTime = 0;
                return EnemyAnimationState.Run;
            }
            // Movement intent alone must not keep the run animation alive at an
            // impassable edge. Keep a short grace period so intermittent surface
            // probes do not alternate between run and idle every frame.
            idleTime += deltaTime;
            var grace = wantsToMove ? 0.3f : 0.2f;
            return idleTime >= grace ? EnemyAnimationState.Idle : current;
        }

        private void FaceTarget(ref DotsEnemyState state, DotsEnemyBrain brain, float deltaTime, float now)
        {
            if (brain.Target < 0 || state.StunUntil > now || brain.Attacking != 0 ||
                math.lengthsq(brain.Direction) < 0.0001f) return;
            var hasSurface = TryGetSurfaceFrame(state.SupportId, true, out var frame);
            EnemyFacing.Apply(ref state, brain, hasSurface ? (quaternion)frame.rotation : quaternion.identity,
                hasSurface, deltaTime, now);
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

        private static int GroundPathSteps(Vector3 from, Vector3 to) =>
            Mathf.CeilToInt(new Vector2(to.x - from.x, to.z - from.z).magnitude / 0.12f);

        private bool HasContinuousGround(Vector3 from, Vector3 to)
        {
            if (!Catalog.EnableSurfaceContinuityChecks) return true;
            // Validate the interior as well as the destination: a long frame must not
            // let a skeleton walk across water simply because the opposite deck is hit.
            // Vertical feet adjustment does not cross more water/ground cells.
            var steps = GroundPathSteps(from, to);
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
            if (!Catalog.EnableSurfaceTransfers) return false;
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
            var valid = Catalog.EnableSurfaceTransfers && !transfer.Returning && transfer.Source != null && transfer.Source.enabled && transfer.Source.gameObject.activeInHierarchy &&
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
            if (!Catalog.EnableSurfaceEdgeFollowing) return false;
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
