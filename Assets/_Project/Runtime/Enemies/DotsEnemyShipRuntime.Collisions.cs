using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;
using WaveByWave.Collision;
using WaveByWave.Items;
using WaveByWave.Ships;
using Collider = Unity.Physics.Collider;

namespace WaveByWave.Enemies
{
    [BurstCompile]
    public sealed partial class DotsEnemyShipRuntime
    {
        private BlobAssetReference<Collider> _authoredHull;
        private BlobAssetReference<Collider> _crewHull;
        private float _authoredHullRadius;
        private bool _hullFailed;
        private readonly List<int> _contactCandidates = new();
        private readonly Dictionary<GameObject, BlobAssetReference<Collider>> _targetHulls = new();

        private bool EnsureCrewPositions(DotsEnemyCatalog catalog)
        {
            if (_crewPositions != null) return _crewPositions.Count == Definition.CrewCount;
            // Validate the shared spawn layout once, not once for each ship. Never edit
            // markers or colliders on the user's prefab, and never query nearby ships.
            try
            {
                var view = Definition.ViewPrefab.GetComponent<EnemyShipView>();
                _crewPositions = ResolveCrewPositions(_crewHull,
                    view.GetCrewLocalPositions(Definition.CrewCount, Definition),
                    Definition.ViewPrefab.transform.localScale, catalog.BodyRadius, catalog.MaximumSlope);
            }
            catch (Exception error)
            {
                _crewPositions = Array.Empty<Vector3>();
                Debug.LogError($"[Enemy ships] No safe crew spawn surface: {error.Message}");
            }
            return _crewPositions.Count == Definition.CrewCount;
        }

        private static List<Vector3> ResolveCrewPositions(BlobAssetReference<Collider> hull,
            IReadOnlyList<Vector3> requested, Vector3 scale, float radius, float maximumSlope)
        {
            var result = new List<Vector3>(requested.Count);
            var resolved = new Dictionary<Vector3, Vector3>();
            var bounds = hull.Value.CalculateAabb();
            var inset = Mathf.Max(0.1f, radius) + 0.08f;
            var minimumNormalY = Mathf.Cos(maximumSlope * Mathf.Deg2Rad);
            foreach (var local in requested)
            {
                if (!resolved.TryGetValue(local, out var safe))
                {
                    var wanted = Vector3.Scale(local, scale);
                    var candidate = wanted;
                    candidate.x = Mathf.Clamp(candidate.x, bounds.Min.x, bounds.Max.x);
                    candidate.z = Mathf.Clamp(candidate.z, bounds.Min.z, bounds.Max.z);
                    var found = TryCrewFootprint(hull, bounds, candidate, inset, minimumNormalY, out safe);
                    var step = Mathf.Max(0.2f, inset * 0.5f);
                    var extent = math.length((bounds.Max - bounds.Min).xz);
                    var rings = Mathf.Min(96, Mathf.CeilToInt(extent / step));
                    for (var ring = 1; !found && ring <= rings; ring++)
                    for (var sample = 0; !found && sample < 16; sample++)
                    {
                        var angle = sample * (Mathf.PI * 2f / 16);
                        var probe = candidate + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * (ring * step);
                        if (probe.x < bounds.Min.x || probe.x > bounds.Max.x || probe.z < bounds.Min.z || probe.z > bounds.Max.z) continue;
                        found = TryCrewFootprint(hull, bounds, probe, inset, minimumNormalY, out safe);
                    }
                    if (!found) throw new InvalidOperationException($"No walkable footprint near crew point {local}.");
                    safe = new Vector3(safe.x / scale.x, safe.y / scale.y, safe.z / scale.z);
                    resolved.Add(local, safe);
                }
                result.Add(safe);
            }
            return result;
        }

        private struct CrewFloorCollector : ICollector<Unity.Physics.RaycastHit>
        {
            public bool EarlyOutOnFirstHit => false;
            public float MaxFraction => 1f;
            public int NumHits { get; private set; }
            public float Height, MinimumNormalY, Error;
            public float3 Point;
            public bool AddHit(Unity.Physics.RaycastHit hit)
            {
                if (hit.SurfaceNormal.y < MinimumNormalY) return false;
                var error = math.abs(hit.Position.y - Height);
                if (NumHits > 0 && error >= Error) return false;
                Point = hit.Position; Error = error; NumHits++;
                return true;
            }
        }

        private static bool CrewFloor(BlobAssetReference<Collider> hull, Aabb bounds,
            Vector3 wanted, float minimumNormalY, out Vector3 point)
        {
            var ray = new RaycastInput { Start = new float3(wanted.x, bounds.Max.y + 0.1f, wanted.z),
                End = new float3(wanted.x, bounds.Min.y - 0.1f, wanted.z), Filter = CollisionFilter.Default };
            var collector = new CrewFloorCollector { Height = wanted.y, MinimumNormalY = minimumNormalY };
            var found = hull.Value.CastRay(ray, ref collector);
            point = collector.Point;
            return found;
        }

        private static bool TryCrewFootprint(BlobAssetReference<Collider> hull, Aabb bounds,
            Vector3 wanted, float inset, float minimumNormalY, out Vector3 point)
        {
            if (!CrewFloor(hull, bounds, wanted, minimumNormalY, out point)) return false;
            var heightTolerance = inset * Mathf.Tan(Mathf.Acos(minimumNormalY)) + 0.04f;
            for (var sample = 0; sample < 8; sample++)
            {
                var angle = sample * (Mathf.PI * 2f / 8);
                var edge = point + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * inset;
                if (!CrewFloor(hull, bounds, edge, minimumNormalY, out var floor) ||
                    Mathf.Abs(floor.y - point.y) > heightTolerance) return false;
            }
            return true;
        }

        private bool EnsureCollisionGeometry()
        {
            if (_hullFailed) return false;
            try
            {
                if (!_authoredHull.IsCreated)
                {
                    _authoredHull = Unity.Physics.BoxCollider.Create(new BoxGeometry
                    {
                        Center = _contactBoundsCenter,
                        Size = _contactBoundsHalfExtents * 2f,
                        Orientation = quaternion.identity,
                        BevelRadius = 0f
                    });
                    _authoredHullRadius = math.length(math.abs((float3)_contactBoundsCenter) +
                        (float3)_contactBoundsHalfExtents);
                }
                if (Definition.CrewCount > 0 && !_crewHull.IsCreated)
                    _crewHull = AuthoredShipHull.Create(Definition.ViewPrefab, Definition.CollisionLayers);
                foreach (var target in _targets)
                    if (!_targetHulls.ContainsKey(target.Ship.gameObject))
                        _targetHulls.Add(target.Ship.gameObject, Unity.Physics.BoxCollider.Create(new BoxGeometry
                        {
                            Center = target.Ship.ContactBoundsCenter,
                            Size = target.Ship.ContactBoundsHalfExtents * 2f,
                            Orientation = quaternion.identity,
                            BevelRadius = 0f
                        }));
                return true;
            }
            catch (Exception exception)
            {
                _hullFailed = true;
                Debug.LogError($"[Enemy ships] Cannot prepare DOTS contact bounds: {exception.Message}");
                return false;
            }
        }

        private Vector3 ResolveHullMotion(int id, Vector3 position, Quaternion rotation,
            Vector3 movement, float deltaTime)
        {
            _fleet.CollectCandidates(id, position, movement, _contactCandidates);
            var accepted = Vector3.zero;
            var remaining = movement;
            // Resolve contact then slide along it, so side-by-side ships can advance.
            for (var pass = 0; pass < 3 && remaining.sqrMagnitude > 0.000001f; pass++)
            {
                var fraction = 1f;
                var normal = float3.zero;
                var contactedEnemy = 0;
                NetworkShipController contactedPlayer = null;
                var pose = new RigidTransform(rotation, position + accepted);
                foreach (var other in _contactCandidates)
                    if (_collisionPoses.TryGetValue(other, out var obstacle))
                    {
                        var candidateFraction = fraction;
                        var candidateNormal = normal;
                        CastHull(_authoredHull, _authoredHull, pose, obstacle, remaining,
                            ref candidateFraction, ref candidateNormal);
                        if (candidateFraction >= fraction - 0.000001f) continue;
                        fraction = candidateFraction;
                        normal = candidateNormal;
                        contactedEnemy = other;
                        contactedPlayer = null;
                    }
                foreach (var target in _targets)
                    if (_targetHulls.TryGetValue(target.Ship.gameObject, out var hull))
                    {
                        var frame = WorldItem.GetPhysicsFrame(target.Ship.NetworkObject);
                        var candidateFraction = fraction;
                        var candidateNormal = normal;
                        CastHull(_authoredHull, hull, pose, new RigidTransform(frame.rotation, (Vector3)frame.GetColumn(3)),
                            remaining, ref candidateFraction, ref candidateNormal);
                        if (candidateFraction >= fraction - 0.000001f) continue;
                        fraction = candidateFraction;
                        normal = candidateNormal;
                        contactedEnemy = 0;
                        contactedPlayer = target.Ship;
                    }
                var travel = Mathf.Max(0, fraction - (fraction < 1f ? 0.005f / remaining.magnitude : 0));
                accepted += remaining * travel;
                remaining *= 1f - travel;
                normal.y = 0;
                normal = math.normalizesafe(normal);
                if (math.lengthsq(normal) < 0.01f) break;
                var impactSpeed = math.max(0f, -math.dot((float3)remaining /
                    math.max(0.0001f, deltaTime), normal));
                if (impactSpeed > 0.01f)
                {
                    var push = (Vector3)(-normal * math.min(Definition.MaximumContactPushSpeed,
                        impactSpeed * Definition.ContactPushStrength));
                    if (contactedEnemy != 0) AddContactPush(contactedEnemy, push);
                    else contactedPlayer?.ApplyEnemyContactPush(push,
                        Definition.MaximumContactPushSpeed);
                }
                remaining = Vector3.ProjectOnPlane(remaining, normal);
            }
            return accepted;
        }

        private quaternion ResolveHullRotation(int id, float3 position, quaternion previous, quaternion proposed)
        {
            _fleet.CollectCandidates(id, position, Vector3.zero, _contactCandidates);
            foreach (var other in _contactCandidates)
                if (_collisionPoses.TryGetValue(other, out var obstacle) &&
                    RotationPenetrates(_authoredHull, _authoredHull, position, previous, proposed, obstacle)) return previous;
            foreach (var target in _targets)
                if (_targetHulls.TryGetValue(target.Ship.gameObject, out var hull))
                {
                    var frame = WorldItem.GetPhysicsFrame(target.Ship.NetworkObject);
                    if (RotationPenetrates(_authoredHull, hull, position, previous, proposed,
                            new RigidTransform(frame.rotation, (Vector3)frame.GetColumn(3)))) return previous;
                }
            return proposed;
        }

        private struct ApproachingContact : ICollector<ColliderCastHit>
        {
            public bool EarlyOutOnFirstHit => false;
            public float MaxFraction { get; private set; }
            public int NumHits { get; private set; }
            public float3 Direction;
            public float3 Normal;
            public ApproachingContact(float3 direction, float fraction)
            { Direction = direction; MaxFraction = fraction; NumHits = 0; Normal = default; }
            public bool AddHit(ColliderCastHit hit)
            {
                if (math.dot(Direction, hit.SurfaceNormal) >= -0.00001f) return false;
                MaxFraction = hit.Fraction;
                Normal = hit.SurfaceNormal;
                NumHits++;
                return true;
            }
        }

        [BurstCompile]
        private static unsafe void CastHull(in BlobAssetReference<Collider> source, in BlobAssetReference<Collider> target,
            in RigidTransform pose, in RigidTransform obstacle, in float3 displacement, ref float fraction, ref float3 normal)
        {
            var body = new Unity.Physics.RigidBody { Collider = target, WorldFromBody = obstacle, Scale = 1 };
            var input = new ColliderCastInput { Collider = (Collider*)source.GetUnsafePtr(), Orientation = pose.rot,
                Start = pose.pos, End = pose.pos + displacement, QueryColliderScale = 1 };
            var collector = new ApproachingContact(displacement, fraction);
            if (body.CastCollider(input, ref collector)) { fraction = collector.MaxFraction; normal = collector.Normal; }
        }

        [BurstCompile]
        private static bool RotationPenetrates(in BlobAssetReference<Collider> source, in BlobAssetReference<Collider> target,
            in float3 position, in quaternion previous, in quaternion proposed, in RigidTransform obstacle)
        {
            var body = new Unity.Physics.RigidBody { Collider = target, WorldFromBody = obstacle, Scale = 1 };
            if (!body.CalculateDistance(new ColliderDistanceInput(source, 0, new RigidTransform(proposed, position)), out DistanceHit next)) return false;
            var oldDistance = body.CalculateDistance(new ColliderDistanceInput(source, 0, new RigidTransform(previous, position)), out DistanceHit old)
                ? old.Distance : 0f;
            return next.Distance < math.min(-0.005f, oldDistance - 0.002f);
        }

        private void ReleaseCollisionGeometry()
        {
            if (_authoredHull.IsCreated) _authoredHull.Dispose();
            _authoredHull = default;
            if (_crewHull.IsCreated) _crewHull.Dispose();
            _crewHull = default;
            foreach (var hull in _targetHulls.Values) if (hull.IsCreated) hull.Dispose();
            _targetHulls.Clear();
            _hullFailed = false;
        }
    }
}
