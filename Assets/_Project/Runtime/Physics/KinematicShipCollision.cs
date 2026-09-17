using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;
using UnityEngine.SceneManagement;
using KinematicCharacterController;
using WaveByWave.Player;
using PhysicsCollider = Unity.Physics.Collider;
using EngineCollider = UnityEngine.Collider;
using EngineMeshCollider = UnityEngine.MeshCollider;

namespace WaveByWave.Collision
{
    /// <summary>
    /// Query-only triangle geometry. PhysX continues to support players on deck;
    /// a separate static query world prevents its solver from driving the ship.
    /// No mesh cooking, scene searches or managed allocations in ResolveMotion.
    /// </summary>
    [DisallowMultipleComponent]
    [BurstCompile]
    [RequireComponent(typeof(UnityEngine.Rigidbody))]
    public sealed class KinematicShipCollision : MonoBehaviour
    {
        [Tooltip("Solid model meshes. Empty uses the ship's visible meshes, excluding sails, flags and interaction markers.")]
        [SerializeField] private MeshFilter[] solidGeometry = Array.Empty<MeshFilter>();
        [SerializeField, Range(8, 32)] private int solverIterations = 24;
        [SerializeField, Range(0f, 0.3f)] private float restitution = 0.05f;
        [SerializeField, Range(0f, 0.5f)] private float contactFriction = 0.025f;
        [SerializeField, Min(0.01f)] private float maximumRecoveryStep = 0.15f;
        [Tooltip("Maximum geometric push-out speed for the entire tick, independent of solver iteration count.")]
        [SerializeField, Min(0.01f)] private float maximumRecoverySpeed = 0.75f;

        private NativeArray<HullNode> _hullNodes;
        private NativeArray<int> _queryStack;
        private int _lastContactLeaf = -1;
        private ContactConstraint _cachedContact;
        private FixedList512Bytes<ContactConstraint> _contactPlanes;
        private NativeArray<float3> _patchVertices;
        private PhysicsWorld _world;
        private readonly HashSet<BlobAssetReference<PhysicsCollider>> _obstacleGeometry = new();
        private readonly Dictionary<(Mesh, Vector3, bool), BlobAssetReference<PhysicsCollider>> _meshObstacleCache = new();
        private ShipCollisionMeshLibrary _library;
        private bool _hasWorld;
        private bool _sceneChanged;
        private int _obstacleLayers;
        private float _radius;
        private float _skin;

        public bool IsReady => _hullNodes.IsCreated && _hasWorld;
        public Vector3 HullHalfSize { get; private set; } = Vector3.one;

        private struct HullNode
        {
            public BlobAssetReference<PhysicsCollider> Bounds;
            public BlobAssetReference<PhysicsCollider> Surface;
            public int Left;
            public int Right;
            public float3 Center;
            public float3 HalfSize;
            public int VertexStart;
            public int VertexCount;
        }

        private struct ContactConstraint
        {
            public Vector3 Normal;
            public Vector3 Point;
            public float Distance;
            public int BodyIndex;
            public ColliderKey Key;
            public int Leaf;
            // Store native-boundary flags as bytes. A C# bool field is not
            // blittable for Burst's AOT direct-call ABI, even in a ref struct.
            private byte _valid;
            private byte _convexObstacle;
            public bool Valid
            {
                get => _valid != 0;
                set => _valid = (byte)(value ? 1 : 0);
            }
            public bool ConvexObstacle
            {
                get => _convexObstacle != 0;
                set => _convexObstacle = (byte)(value ? 1 : 0);
            }
        }

        private readonly struct HullTriangle
        {
            public readonly float3 A;
            public readonly float3 B;
            public readonly float3 C;
            public float3 Center => (A + B + C) / 3f;

            public HullTriangle(float3 a, float3 b, float3 c) { A = a; B = b; C = c; }
        }

        private void Awake()
        {
            var body = GetComponent<UnityEngine.Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            // Ship motion is swept by the query solver. Speculative PhysX CCD on the
            // passenger colliders adds contacts that cannot stop this kinematic body.
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            // Passenger colliders belong to the authored prefab and the KCC motor.
            // The native query hull must never rebuild or disable that collider graph.
        }
        public readonly struct Motion
        {
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            public readonly bool Blocked;
            public readonly Vector3 Normal;
            public readonly Vector3 Point;
            public readonly Vector3 Velocity;
            public readonly Vector3 AngularVelocity;

            public Motion(Vector3 position, Quaternion rotation, bool blocked,
                Vector3 normal, Vector3 point, Vector3 velocity = default, Vector3 angularVelocity = default)
            {
                Position = position;
                Rotation = rotation;
                Blocked = blocked;
                Normal = normal;
                Point = point;
                Velocity = velocity;
                AngularVelocity = angularVelocity;
            }
        }

        public bool Initialize(LayerMask obstacleLayers, float skin)
        {
            Release();
            _obstacleLayers = obstacleLayers.value;
            _skin = Mathf.Max(0.001f, skin);
            _library = Resources.Load<ShipCollisionMeshLibrary>(ShipCollisionMeshLibrary.ResourceName);
            try
            {
                BuildHull();
                RefreshStaticObstacles();
                SceneManager.sceneLoaded += OnSceneLoaded;
                SceneManager.sceneUnloaded += OnSceneUnloaded;
                return IsReady;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Wave by Wave] Ship collision geometry could not be prepared. " +
                    $"Movement is stopped to avoid passing through obstacles. {exception.Message}", this);
                Release();
                return false;
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => _sceneChanged = true;
        private void OnSceneUnloaded(Scene scene) => _sceneChanged = true;

        // Call after spawning, moving, enabling or removing static scenery at runtime.
        // The ordinary ocean scene is built once when the authoritative ship spawns.
        public void RefreshStaticObstacles()
        {
            ReleaseWorld();
            var candidates = UnityEngine.Object.FindObjectsByType<EngineCollider>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var bodies = new List<Unity.Physics.RigidBody>();
            foreach (var obstacle in candidates)
            {
                if (!obstacle.enabled || obstacle.isTrigger || obstacle.attachedRigidbody != null ||
                    obstacle.transform.IsChildOf(transform) ||
                    obstacle.GetComponentInParent<KinematicCharacterMotor>() != null ||
                    obstacle.GetComponentInParent<MovingPlatform>() != null ||
                    (_obstacleLayers & (1 << obstacle.gameObject.layer)) == 0 ||
                    UnityEngine.Physics.GetIgnoreLayerCollision(gameObject.layer, obstacle.gameObject.layer))
                    continue;

                var geometry = CreateObstacle(obstacle);
                if (!geometry.IsCreated)
                    throw new NotSupportedException($"Unsupported static obstacle: {obstacle.name} ({obstacle.GetType().Name}).");
                _obstacleGeometry.Add(geometry);
                bodies.Add(new Unity.Physics.RigidBody
                {
                    Collider = geometry,
                    WorldFromBody = new RigidTransform(ToQuaternion(obstacle.transform.rotation), obstacle.transform.position),
                    Scale = 1f,
                    Entity = Entity.Null
                });
            }

            _world = new PhysicsWorld(bodies.Count, 0, 0);
            _hasWorld = true;
            var staticBodies = _world.StaticBodies;
            for (var i = 0; i < bodies.Count; i++)
                staticBodies[i] = bodies[i];
            _world.CollisionWorld.BuildBroadphase(ref _world, Time.fixedDeltaTime, float3.zero);
            _sceneChanged = false;
        }

        public Motion ResolveMotion(Vector3 startPosition, Quaternion startRotation,
            Vector3 velocity, Vector3 angularVelocity, float deltaTime)
        {
            if (!IsReady)
                return new Motion(startPosition, startRotation, false, Vector3.zero, startPosition);
            if (_sceneChanged)
            {
                try { RefreshStaticObstacles(); }
                catch (Exception exception)
                {
                    Debug.LogError($"[Wave by Wave] Static collision world rebuild failed: {exception.Message}", this);
                    ReleaseWorld();
                    return new Motion(startPosition, startRotation, false, Vector3.zero, startPosition);
                }
            }

            var position = startPosition;
            var rotation = startRotation;
            var stepTime = Mathf.Max(0f, deltaTime);
            if (stepTime <= 0.000001f)
                return new Motion(position, rotation, false, Vector3.zero, position, velocity, angularVelocity);
            var recoveryRemaining = Mathf.Min(Mathf.Max(0.001f, maximumRecoveryStep),
                Mathf.Max(0.01f, maximumRecoverySpeed) * stepTime);
            var normal = Vector3.zero;
            var point = position;
            var blocked = false;
            for (var iteration = 0; iteration < Mathf.Clamp(solverIterations, 8, 32); iteration++)
            {
                var move = velocity * stepTime;
                var angularTravel = angularVelocity * stepTime;
                var travelBound = move.magnitude + angularTravel.magnitude * _radius;
                var pose = new RigidTransform(ToQuaternion(rotation), position);
                float3 nativeMove = move;
                float3 nativeAngularTravel = angularTravel;
                var collector = new MotionDistanceCollector(travelBound + _skin,
                    move, angularTravel, _patchVertices, iteration > 0);
                FindLocalDistance(ref _world.CollisionWorld, ref _hullNodes, ref _queryStack,
                    ref _patchVertices, ref pose, _skin, ref nativeMove, ref nativeAngularTravel,
                    ref _contactPlanes, _lastContactLeaf, ref collector, out var hitLeaf);
                if (collector.NumHits == 0)
                {
                    position += move;
                    rotation = IntegrateRotation(rotation, angularTravel);
                    return new Motion(position, rotation, blocked, normal, point, velocity, angularVelocity);
                }

                var hit = collector.Hit;
                blocked = true;
                normal = hit.SurfaceNormal;
                point = hit.Position;
                _lastContactLeaf = hitLeaf;
                _cachedContact = new ContactConstraint
                {
                    Normal = normal, Point = point, Distance = hit.Distance,
                    BodyIndex = hit.RigidBodyIndex, Key = hit.ColliderKey, Leaf = hitLeaf, Valid = true,
                    ConvexObstacle = _world.Bodies[hit.RigidBodyIndex].Collider.Value.CollisionType == CollisionType.Convex
                };
                CacheContactPlane(_cachedContact);

                // Solve the entire requested displacement, not a sequence of
                // almost-zero approach steps. A kinematic helm prescribes yaw:
                // contact transfers its inward arc into outward translation.
                // Removing yaw here would stall the helm again on the next tick.
                if (hit.Distance <= _skin)
                    SolveVelocity(normal, ref velocity);

                // Penetration recovery is geometric, separate from momentum. A wave
                // or spawn overlap can move outward without adding kinetic energy.
                var contactPosition = position;
                if (hit.Distance < 0.001f && recoveryRemaining > 0f)
                {
                    var correction = ContactMobility(normal);
                    var normalWeight = Vector3.Dot(correction, normal);
                    var amount = Mathf.Min(recoveryRemaining,
                        (_skin - hit.Distance + 0.0002f) / Mathf.Max(0.0001f, normalWeight));
                    var displacement = correction * amount;
                    position += displacement;
                    recoveryRemaining -= displacement.magnitude;
                }

                var recoveredDistance = hit.Distance + Vector3.Dot(position - contactPosition, normal);
                var clearance = SweptPatchClearance(_hullNodes[hitLeaf], _patchVertices,
                    new RigidTransform(ToQuaternion(rotation), position), velocity * stepTime,
                    angularVelocity * stepTime, normal, recoveredDistance, hit.QueryColliderKey, out _);
                var allowedDistance = Mathf.Min(0f, recoveredDistance);
                if (clearance < allowedDistance - 0.00001f)
                {
                    var mobility = ContactMobility(normal);
                    var normalWeight = Mathf.Max(0.0001f, Vector3.Dot(mobility, normal));
                    velocity += mobility * ((allowedDistance + 0.0002f - clearance) /
                        (stepTime * normalWeight));
                }
            }

            // A crowded corner may need more constraints than the main pass can
            // solve. Return a verified smaller step instead of storing a nonzero
            // velocity while holding the mover's pose. Never apply an unchecked
            // final displacement when the iteration limit is reached.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var move = velocity * stepTime;
                var angularTravel = angularVelocity * stepTime;
                var pose = new RigidTransform(ToQuaternion(rotation), position);
                float3 nativeMove = move;
                float3 nativeAngularTravel = angularTravel;
                var collector = new MotionDistanceCollector(move.magnitude + angularTravel.magnitude * _radius + _skin,
                    move, angularTravel, _patchVertices, true);
                FindLocalDistance(ref _world.CollisionWorld, ref _hullNodes, ref _queryStack,
                    ref _patchVertices, ref pose, _skin, ref nativeMove, ref nativeAngularTravel,
                    ref _contactPlanes, _lastContactLeaf, ref collector, out _);
                if (collector.NumHits == 0)
                {
                    var executedFraction = stepTime / deltaTime;
                    return new Motion(position + move, IntegrateRotation(rotation, angularTravel),
                        blocked, normal, point, velocity * executedFraction, angularVelocity * executedFraction);
                }
                stepTime *= 0.5f;
            }
            // Match the held pose. Do not carry an unexecuted contact reaction
            // into the passenger mover or the following propulsion step.
            return new Motion(position, rotation, blocked, normal, point, Vector3.zero, Vector3.zero);
        }

        public static Quaternion IntegrateRotation(Quaternion rotation, Vector3 angularTravel)
        {
            var angle = angularTravel.magnitude;
            return angle > 0.000001f
                ? Quaternion.AngleAxis(angle * Mathf.Rad2Deg, angularTravel / angle) * rotation
                : rotation;
        }

        private void CacheContactPlane(ContactConstraint contact)
        {
            for (var i = 0; i < _contactPlanes.Length; i++)
            {
                var previous = _contactPlanes[i];
                if (previous.BodyIndex == contact.BodyIndex &&
                    (contact.ConvexObstacle || previous.Key.Equals(contact.Key)) &&
                    Vector3.Dot(previous.Normal, contact.Normal) > 0.995f)
                {
                    _contactPlanes[i] = contact;
                    return;
                }
            }
            if (_contactPlanes.Length >= 6) _contactPlanes.RemoveAt(0);
            _contactPlanes.Add(contact);
        }

        private static Vector3 ContactMobility(Vector3 normal)
        {
            var planar = new Vector3(normal.x, 0f, normal.z);
            // Wall reactions use surge/sway; only an almost horizontal support
            // surface may change heave. Contact impulses must not overturn deck
            // pitch/roll that the existing water/KCC pipeline already controls.
            return planar.sqrMagnitude >= 0.25f ? planar : normal;
        }

        private void SolveVelocity(Vector3 normal, ref Vector3 velocity)
        {
            var normalSpeed = Vector3.Dot(velocity, normal);
            if (normalSpeed >= 0f) return;
            var mobility = ContactMobility(normal);
            var effectiveMass = Vector3.Dot(mobility, normal);
            var bounce = normalSpeed < -1f ? restitution : 0f;
            var impulse = -(1f + bounce) * normalSpeed / Mathf.Max(0.0001f, effectiveMass);
            velocity += mobility * impulse;
            var tangent = Vector3.Cross(Vector3.up, normal);
            if (tangent.sqrMagnitude < 0.00001f)
                tangent = Vector3.ProjectOnPlane(velocity, Vector3.up);
            if (tangent.sqrMagnitude < 0.00001f) return;
            tangent.Normalize();
            var tangentSpeed = Vector3.Dot(velocity, tangent);
            if (tangentSpeed < 0f) { tangent = -tangent; tangentSpeed = -tangentSpeed; }
            if (tangentSpeed < 0.00001f) return;
            var frictionImpulse = Mathf.Min(contactFriction * impulse, tangentSpeed);
            velocity -= tangent * frictionImpulse;
        }

        private static unsafe float SweptPatchClearance(HullNode patch, NativeArray<float3> vertices,
            RigidTransform pose, float3 movement, float3 angular, float3 normal,
            float distance, ColliderKey queryKey, out float3 criticalLever)
        {
            var levers = new FixedList512Bytes<float3>();
            if (PhysicsCollider.GetLeafCollider(out var leaf, (PhysicsCollider*)patch.Surface.GetUnsafePtr(),
                queryKey, RigidTransform.identity) && leaf.Collider != null &&
                (leaf.Collider->Type == ColliderType.Triangle || leaf.Collider->Type == ColliderType.Quad))
            {
                var polygon = (PolygonCollider*)leaf.Collider;
                var polygonVertices = polygon->Vertices;
                for (var i = 0; i < polygonVertices.Length; i++)
                    levers.Add(math.mul(pose.rot, math.transform(leaf.TransformFromChild, polygonVertices[i])));
            }
            else
            {
                for (var i = 0; i < patch.VertexCount; i++)
                    levers.Add(math.mul(pose.rot, vertices[patch.VertexStart + i]));
            }

            var minimumInitial = float.PositiveInfinity;
            var minimumFinal = float.PositiveInfinity;
            var translation = math.dot(movement, normal);
            var angleSquared = math.lengthsq(angular);
            criticalLever = float3.zero;
            for (var i = 0; i < levers.Length; i++)
            {
                var lever = levers[i];
                var initial = math.dot(lever, normal);
                minimumInitial = math.min(minimumInitial, initial);
                var orbitRadius = angleSquared > 1e-12f
                    ? math.length(math.cross(angular * math.rsqrt(angleSquared), lever)) : 0f;
                var curvature = AngularCurvatureAllowance(angular, new float3(orbitRadius, 0f, 0f), normal);
                var final = initial + translation + math.dot(math.cross(angular, lever), normal) - curvature;
                if (final < minimumFinal) { minimumFinal = final; criticalLever = lever; }
            }
            // Anchor clearance to the native distance witness. A reconstructed
            // initial plane can disagree slightly with it; repeatedly treating
            // that fixed disagreement as a new penetration prevents any step.
            return math.min(distance, distance + minimumFinal - minimumInitial);
        }
        private static float MinimumAngularMotion(HullNode patch, quaternion rotation, float3 angular, float3 normal)
        {
            var gradient = math.cross(normal, angular);
            var localGradient = math.mul(math.inverse(rotation), gradient);
            return math.dot(math.mul(rotation, patch.Center), gradient) -
                math.csum(math.abs(localGradient) * patch.HalfSize);
        }

        private static float MinimumAngularMotion(HullNode patch, NativeArray<float3> vertices,
            quaternion rotation, float3 angular, float3 normal)
        {
            if (!patch.Surface.IsCreated)
                return MinimumAngularMotion(patch, rotation, angular, normal);
            // Empty corners of the patch's AABB must not prevent the real hull
            // triangles from departing a contact during a turn.
            var localGradient = math.mul(math.inverse(rotation), math.cross(normal, angular));
            var minimum = float.PositiveInfinity;
            for (var i = 0; i < patch.VertexCount; i++)
                minimum = math.min(minimum, math.dot(vertices[patch.VertexStart + i], localGradient));
            return minimum;
        }

        private static bool ContactAllowsMotion(float distance, float minimumMotion)
        {
            return distance >= 0f && distance + minimumMotion >= 0f;
        }

        private static float AngularCurvatureAllowance(float3 angular, float3 radius, float3 normal)
        {
            var angleSquared = math.lengthsq(angular);
            if (angleSquared < 1e-12f) return 0f;
            // Rotation about a floor's normal cannot change clearance above that floor.
            var alignment = math.dot(angular * math.rsqrt(angleSquared), normal);
            var normalAcrossAxis = math.sqrt(math.max(0f, 1f - alignment * alignment));
            return 0.5f * angleSquared * math.length(radius) * normalAcrossAxis;
        }

        [BurstCompile]
        // The managed-to-Burst entry point uses references for every struct.
        // Player AOT compilation cannot pass these structs or vectors by value.
        private static void FindLocalDistance(ref CollisionWorld world, ref NativeArray<HullNode> nodes,
            ref NativeArray<int> stack, ref NativeArray<float3> vertices, ref RigidTransform pose,
            float skin, ref float3 movement, ref float3 angular,
            ref FixedList512Bytes<ContactConstraint> planes, int preferredLeaf,
            ref MotionDistanceCollector collector,
            out int hitLeaf)
        {
            hitLeaf = -1;
            // Persistent contact is normally answered by a single small-patch query.
            // The complete hull is never submitted as a mesh distance query.
            if (preferredLeaf >= 0 && preferredLeaf < nodes.Length)
            {
                collector.SetPatch(nodes[preferredLeaf], pose);
                var input = new ColliderDistanceInput(nodes[preferredLeaf].Surface, collector.MaxFraction, pose);
                if (world.CalculateDistance(input, ref collector))
                {
                    hitLeaf = preferredLeaf;
                    if (collector.Hit.Distance <= skin + 0.0001f) return;
                }
            }

            var stackCount = 1;
            stack[0] = 0;
            while (stackCount > 0)
            {
                var index = stack[--stackCount];
                var node = nodes[index];
                var localRadius = math.length(math.abs(node.Center) + node.HalfSize);
                var nodeDistance = math.min(collector.MaxFraction,
                    math.length(movement) + math.length(angular) * localRadius + skin);
                var boundsInput = new ColliderDistanceInput(node.Bounds, nodeDistance, pose);
                uint separatedPlanes = 0;
                for (var i = 0; i < planes.Length; i++)
                    if (PlaneSeparatesPatch(node, vertices, pose, movement, angular, localRadius, planes[i]))
                        separatedPlanes |= 1u << i;
                var any = new BoundsDistanceCollector(nodeDistance, planes, separatedPlanes,
                    node, pose.rot, movement, angular, localRadius);
                if (!world.CalculateDistance(boundsInput, ref any)) continue;

                if (!node.Surface.IsCreated)
                {
                    stack[stackCount++] = node.Right;
                    stack[stackCount++] = node.Left;
                    continue;
                }
                if (index == preferredLeaf) continue;
                // Unity Physics requires collector.MaxFraction <= input.MaxDistance.
                // Use a local collector for this shorter patch query; narrowing
                // the shared collector without a hit would hide contacts on later
                // patches whose angular travel requires a larger search distance.
                var surfaceCollector = collector;
                surfaceCollector.SetPatch(node, pose);
                surfaceCollector.LimitQueryDistance(nodeDistance);
                var surfaceInput = new ColliderDistanceInput(node.Surface, nodeDistance, pose);
                if (!world.CalculateDistance(surfaceInput, ref surfaceCollector)) continue;
                collector = surfaceCollector;
                hitLeaf = index;
                if (collector.Hit.Distance <= skin + 0.0001f) return;
            }
        }

        private static bool PlaneSeparatesPatch(HullNode node, NativeArray<float3> vertices,
            RigidTransform pose, float3 movement, float3 angular, float radius, ContactConstraint plane)
        {
            if (!plane.Valid) return false;
            var localNormal = math.mul(math.inverse(pose.rot), (float3)plane.Normal);
            var minimum = math.dot(node.Center, localNormal) - math.csum(math.abs(localNormal) * node.HalfSize);
            if (node.Surface.IsCreated)
            {
                minimum = float.PositiveInfinity;
                for (var i = 0; i < node.VertexCount; i++)
                    minimum = math.min(minimum, math.dot(vertices[node.VertexStart + i], localNormal));
            }
            minimum += math.dot(pose.pos - (float3)plane.Point, (float3)plane.Normal);
            var motion = math.dot(movement, (float3)plane.Normal) +
                MinimumAngularMotion(node, vertices, pose.rot, angular, plane.Normal) -
                AngularCurvatureAllowance(angular, new float3(radius, 0f, 0f), plane.Normal);
            // This plane may prune another patch only when that entire patch is
            // outside the obstacle. Penetration recovery needs its own exact hit.
            return ContactAllowsMotion(minimum, motion);
        }

        private struct BoundsDistanceCollector : ICollector<DistanceHit>
        {
            public bool EarlyOutOnFirstHit => true;
            public float MaxFraction { get; }
            public int NumHits => 0;
            private readonly FixedList512Bytes<ContactConstraint> _planes;
            private readonly uint _separatedPlanes;
            private readonly HullNode _node;
            private readonly quaternion _rotation;
            private readonly float3 _movement;
            private readonly float3 _angular;
            private readonly float _radius;

            public BoundsDistanceCollector(float distance, FixedList512Bytes<ContactConstraint> planes,
                uint separatedPlanes,
                HullNode node, quaternion rotation, float3 movement, float3 angular, float radius)
            {
                MaxFraction = distance;
                _planes = planes;
                _separatedPlanes = separatedPlanes;
                _node = node;
                _rotation = rotation;
                _movement = movement;
                _angular = angular;
                _radius = radius;
            }

            public bool AddHit(DistanceHit hit)
            {
                for (var i = 0; i < _planes.Length; i++)
                {
                    var plane = _planes[i];
                    if ((_separatedPlanes & (1u << i)) != 0 && hit.RigidBodyIndex == plane.BodyIndex &&
                        (plane.ConvexObstacle || hit.ColliderKey.Equals(plane.Key)))
                        return false;
                }
                // Prune clear, non-approaching bounds too, so leaving a rock does
                // not spend the precise-query budget on every nearby hull patch.
                // An overlapping box cannot prove that its real triangles overlap.
                if (hit.Distance < 0f) return true;
                var motion = math.dot(_movement, hit.SurfaceNormal) +
                    MinimumAngularMotion(_node, _rotation, _angular, hit.SurfaceNormal) -
                    AngularCurvatureAllowance(_angular, new float3(_radius, 0f, 0f), hit.SurfaceNormal);
                return !ContactAllowsMotion(hit.Distance, motion);
            }
        }

        private struct MotionDistanceCollector : ICollector<DistanceHit>
        {
            public bool EarlyOutOnFirstHit => false;
            public float MaxFraction { get; private set; }
            public int NumHits { get; private set; }
            public DistanceHit Hit;
            private readonly float3 _movement;
            private readonly float3 _angularTravel;
            private readonly NativeArray<float3> _vertices;
            private readonly byte _allowOverlapSeparation;
            private HullNode _patch;
            private RigidTransform _pose;

            public MotionDistanceCollector(float maximumDistance,
                float3 movement, float3 angularTravel, NativeArray<float3> vertices, bool allowOverlapSeparation)
            {
                MaxFraction = maximumDistance;
                NumHits = 0;
                Hit = default;
                _movement = movement;
                _angularTravel = angularTravel;
                _vertices = vertices;
                _allowOverlapSeparation = (byte)(allowOverlapSeparation ? 1 : 0);
                _patch = default;
                _pose = RigidTransform.identity;
            }

            public void SetPatch(HullNode patch, RigidTransform pose)
            { _patch = patch; _pose = pose; }

            public void LimitQueryDistance(float maximumDistance)
            {
                MaxFraction = math.max(0f, math.min(MaxFraction, maximumDistance));
            }

            public bool AddHit(DistanceHit hit)
            {
                if (NumHits > 0 && hit.Distance >= Hit.Distance) return false;
                var clearance = SweptPatchClearance(_patch, _vertices, _pose, _movement,
                    _angularTravel, hit.SurfaceNormal, hit.Distance, hit.QueryColliderKey, out _);
                // Soft skin permits safe tangential motion. Real penetration is
                // handled separately, even when the hull has no requested motion.
                if (hit.Distance >= 0f && clearance >= -0.00005f)
                    return false;
                // The first query handles positional recovery. Subsequent sweeps
                // allow sliding/escape while an old overlap is being removed over
                // several ticks, rather than welding the mover to its start pose.
                if (_allowOverlapSeparation != 0 && hit.Distance < 0f && clearance >= hit.Distance - 0.00005f)
                    return false;
                Hit = hit;
                NumHits = 1;
                // Native broadphase expands its query AABB by MaxFraction.
                // A signed penetration depth must never become that horizon:
                // it would shrink bounds and conceal other nearby hull contacts.
                MaxFraction = math.max(0f, hit.Distance);
                return true;
            }
        }

        private void BuildHull()
        {
            var filters = solidGeometry.Length > 0
                ? solidGeometry : GetComponentsInChildren<MeshFilter>(false);
            var vertices = new List<float3>();
            var triangles = new List<int3>();
            var rootFrame = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one).inverse;
            foreach (var filter in filters)
            {
                if (filter == null || filter.sharedMesh == null || !filter.gameObject.activeInHierarchy)
                    continue;
                if (solidGeometry.Length == 0 && !IsSolidModel(filter))
                    continue;
                var data = ReadMesh(filter.sharedMesh);
                var matrix = rootFrame * filter.transform.localToWorldMatrix;
                AppendMesh(data, matrix, vertices, triangles);
            }

            if (triangles.Count == 0)
                throw new InvalidOperationException("No solid ship meshes were found.");
            var surfaces = new List<HullTriangle>(triangles.Count);
            foreach (var indices in triangles)
            {
                var a = vertices[indices.x];
                var b = vertices[indices.y];
                var c = vertices[indices.z];
                if (math.lengthsq(math.cross(b - a, c - a)) > 1e-12f)
                    surfaces.Add(new HullTriangle(a, b, c));
            }
            if (surfaces.Count == 0)
                throw new InvalidOperationException("The ship meshes contain no usable surface triangles.");

            var surfaceArray = surfaces.ToArray();
            var nodes = new List<HullNode>();
            var patchVertices = new List<float3>(surfaces.Count * 3);
            try
            {
                BuildHullNode(surfaceArray, 0, surfaceArray.Length, nodes, patchVertices);
                _hullNodes = new NativeArray<HullNode>(nodes.ToArray(), Allocator.Persistent);
                _queryStack = new NativeArray<int>(nodes.Count, Allocator.Persistent);
                _patchVertices = new NativeArray<float3>(patchVertices.ToArray(), Allocator.Persistent);
                var bounds = nodes[0].Bounds.Value.CalculateAabb();
                HullHalfSize = bounds.Extents * 0.5f;
                _radius = math.length(math.max(math.abs(bounds.Min), math.abs(bounds.Max)));
                _lastContactLeaf = -1;
                _cachedContact = default;
            }
            catch
            {
                if (!_hullNodes.IsCreated)
                    foreach (var node in nodes)
                    {
                        if (node.Bounds.IsCreated) node.Bounds.Dispose();
                        if (node.Surface.IsCreated) node.Surface.Dispose();
                    }
                throw;
            }
        }

        private static int BuildHullNode(HullTriangle[] triangles, int start, int count,
            List<HullNode> nodes, List<float3> patchVertices)
        {
            var minimum = new float3(float.PositiveInfinity);
            var maximum = new float3(float.NegativeInfinity);
            for (var i = start; i < start + count; i++)
            {
                var triangle = triangles[i];
                minimum = math.min(minimum, math.min(triangle.A, math.min(triangle.B, triangle.C)));
                maximum = math.max(maximum, math.max(triangle.A, math.max(triangle.B, triangle.C)));
            }

            var index = nodes.Count;
            var node = new HullNode
            {
                Bounds = Unity.Physics.BoxCollider.Create(new BoxGeometry
                {
                    Center = (minimum + maximum) * 0.5f,
                    Size = math.max(maximum - minimum, new float3(0.0002f)),
                    Orientation = quaternion.identity,
                    BevelRadius = 0f
                }),
                Left = -1,
                Right = -1,
                Center = (minimum + maximum) * 0.5f,
                HalfSize = math.max(maximum - minimum, new float3(0.0002f)) * 0.5f
            };
            nodes.Add(node);
            const int trianglesPerPatch = 8;
            if (count <= trianglesPerPatch)
            {
                var points = new float3[count * 3];
                var indices = new int3[count];
                for (var i = 0; i < count; i++)
                {
                    var triangle = triangles[start + i];
                    points[i * 3] = triangle.A;
                    points[i * 3 + 1] = triangle.B;
                    points[i * 3 + 2] = triangle.C;
                    indices[i] = new int3(i * 3, i * 3 + 1, i * 3 + 2);
                }
                using var nativePoints = new NativeArray<float3>(points, Allocator.Temp);
                using var nativeIndices = new NativeArray<int3>(indices, Allocator.Temp);
                node.Surface = Unity.Physics.MeshCollider.Create(nativePoints, nativeIndices);
                node.VertexStart = patchVertices.Count;
                node.VertexCount = points.Length;
                patchVertices.AddRange(points);
            }
            else
            {
                var size = maximum - minimum;
                var axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
                Array.Sort(triangles, start, count, Comparer<HullTriangle>.Create(
                    (a, b) => a.Center[axis].CompareTo(b.Center[axis])));
                var half = count / 2;
                node.Left = BuildHullNode(triangles, start, half, nodes, patchVertices);
                node.Right = BuildHullNode(triangles, start + half, count - half, nodes, patchVertices);
            }
            nodes[index] = node;
            return index;
        }

        public static bool IsSolidModel(MeshFilter filter)
        {
            var renderer = filter.GetComponent<MeshRenderer>();
            if (renderer == null || !renderer.enabled)
                return false;
            var label = filter.name;
            if (label.IndexOf("Sail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                label.IndexOf("Flag", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            foreach (var collider in filter.GetComponents<EngineCollider>())
                if (collider.isTrigger)
                    return false;
            return true;
        }

        private ShipCollisionMeshLibrary.Entry ReadMesh(Mesh mesh)
        {
            if (_library != null && _library.TryGet(mesh, out var entry))
                return entry;
            if (!mesh.isReadable)
                throw new InvalidOperationException($"Mesh '{mesh.name}' has no baked collision data. " +
                    "Use Tools > Wave by Wave > Physics > Bake Ship Collision Geometry before building.");
            return new ShipCollisionMeshLibrary.Entry
                { source = mesh, vertices = mesh.vertices, triangles = mesh.triangles };
        }

        private static void AppendMesh(ShipCollisionMeshLibrary.Entry data, Matrix4x4 matrix,
            List<float3> vertices, List<int3> triangles)
        {
            var offset = vertices.Count;
            foreach (var vertex in data.vertices)
                vertices.Add(matrix.MultiplyPoint3x4(vertex));
            for (var i = 0; i + 2 < data.triangles.Length; i += 3)
                triangles.Add(new int3(offset + data.triangles[i], offset + data.triangles[i + 1],
                    offset + data.triangles[i + 2]));
        }

        private BlobAssetReference<PhysicsCollider> CreateObstacle(EngineCollider obstacle)
        {
            var signedScale = obstacle.transform.lossyScale;
            var scale = new Vector3(Mathf.Abs(signedScale.x), Mathf.Abs(signedScale.y), Mathf.Abs(signedScale.z));
            switch (obstacle)
            {
                case UnityEngine.BoxCollider box:
                    return Unity.Physics.BoxCollider.Create(new BoxGeometry
                    {
                        Center = Vector3.Scale(box.center, signedScale),
                        Size = Vector3.Scale(box.size, scale),
                        Orientation = quaternion.identity,
                        BevelRadius = 0f
                    });
                case UnityEngine.SphereCollider sphere:
                    return Unity.Physics.SphereCollider.Create(new SphereGeometry
                    {
                        Center = Vector3.Scale(sphere.center, signedScale),
                        Radius = sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z))
                    });
                case UnityEngine.CapsuleCollider capsule:
                    var axis = capsule.direction == 0 ? Vector3.right : capsule.direction == 1 ? Vector3.up : Vector3.forward;
                    var along = scale[capsule.direction];
                    var across = capsule.direction == 0 ? Mathf.Max(scale.y, scale.z)
                        : capsule.direction == 1 ? Mathf.Max(scale.x, scale.z) : Mathf.Max(scale.x, scale.y);
                    var radius = capsule.radius * across;
                    var center = Vector3.Scale(capsule.center, signedScale);
                    var offset = axis * Mathf.Max(0f, capsule.height * along * 0.5f - radius);
                    return Unity.Physics.CapsuleCollider.Create(new CapsuleGeometry
                        { Vertex0 = center - offset, Vertex1 = center + offset, Radius = radius });
                case EngineMeshCollider mesh when mesh.sharedMesh != null:
                    var key = (mesh.sharedMesh, signedScale, mesh.convex);
                    if (_meshObstacleCache.TryGetValue(key, out var cached))
                        return cached;
                    var data = ReadMesh(mesh.sharedMesh);
                    BlobAssetReference<PhysicsCollider> result;
                    var vertices = new NativeArray<float3>(data.vertices.Length, Allocator.Temp);
                    try
                    {
                        for (var i = 0; i < vertices.Length; i++)
                            vertices[i] = Vector3.Scale(data.vertices[i], signedScale);
                        if (mesh.convex)
                        {
                            var parameters = ConvexHullGenerationParameters.Default;
                            parameters.BevelRadius = 0f;
                            parameters.SimplificationTolerance = 0f;
                            parameters.MinimumAngle = 0f;
                            result = Unity.Physics.ConvexCollider.Create(vertices, parameters);
                        }
                        else
                        {
                            var triangles = new NativeArray<int3>(data.triangles.Length / 3, Allocator.Temp);
                            try
                            {
                                for (var i = 0; i < triangles.Length; i++)
                                    triangles[i] = new int3(data.triangles[i * 3], data.triangles[i * 3 + 1], data.triangles[i * 3 + 2]);
                                result = Unity.Physics.MeshCollider.Create(vertices, triangles);
                            }
                            finally
                            {
                                triangles.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        vertices.Dispose();
                    }
                    _meshObstacleCache.Add(key, result);
                    return result;
                case UnityEngine.TerrainCollider terrain when terrain.terrainData != null:
                    return CreateTerrain(terrain, signedScale);
                default:
                    return default;
            }
        }

        private static BlobAssetReference<PhysicsCollider> CreateTerrain(UnityEngine.TerrainCollider collider, Vector3 scale)
        {
            var terrain = collider.terrainData;
            var resolution = terrain.heightmapResolution;
            var heights = terrain.GetHeights(0, 0, resolution, resolution);
            var size = Vector3.Scale(terrain.size, scale);
            var holes = terrain.GetHoles(0, 0, terrain.holesResolution, terrain.holesResolution);
            var hasHoles = false;
            foreach (var present in holes)
                if (!present) { hasHoles = true; break; }
            if (!hasHoles && size.x > 0f && size.y > 0f && size.z > 0f)
            {
                // Keep ordinary terrain as a heightfield instead of expanding it into
                // millions of triangle indices. Triangle mode preserves its surface.
                var nativeHeights = new NativeArray<float>(resolution * resolution, Allocator.Temp);
                try
                {
                    for (var z = 0; z < resolution; z++)
                    for (var x = 0; x < resolution; x++)
                        nativeHeights[z * resolution + x] = heights[z, x];
                    return Unity.Physics.TerrainCollider.Create(nativeHeights, new int2(resolution),
                        new float3(size.x / (resolution - 1), size.y, size.z / (resolution - 1)),
                        Unity.Physics.TerrainCollider.CollisionMethod.Triangles);
                }
                finally
                {
                    nativeHeights.Dispose();
                }
            }

            // A holed heightfield needs explicit cells; omit the actual terrain holes.
            var vertices = new NativeArray<float3>(resolution * resolution, Allocator.Temp);
            try
            {
                var triangles = new List<int3>();
                for (var z = 0; z < resolution; z++)
                for (var x = 0; x < resolution; x++)
                {
                    vertices[z * resolution + x] = new float3(x * size.x / (resolution - 1),
                        heights[z, x] * size.y, z * size.z / (resolution - 1));
                    if (x == resolution - 1 || z == resolution - 1 ||
                        !holes[z * terrain.holesResolution / (resolution - 1), x * terrain.holesResolution / (resolution - 1)])
                        continue;
                    var a = z * resolution + x;
                    triangles.Add(new int3(a, a + resolution, a + 1));
                    triangles.Add(new int3(a + 1, a + resolution, a + resolution + 1));
                }
                using var nativeTriangles = new NativeArray<int3>(triangles.ToArray(), Allocator.Temp);
                return Unity.Physics.MeshCollider.Create(vertices, nativeTriangles);
            }
            finally
            {
                vertices.Dispose();
            }
        }

        private static quaternion ToQuaternion(Quaternion value) => new(value.x, value.y, value.z, value.w);

        private void ReleaseWorld()
        {
            if (_hasWorld)
                _world.Dispose();
            _hasWorld = false;
            _cachedContact = default;
            _contactPlanes.Clear();
            _lastContactLeaf = -1;
            foreach (var geometry in _obstacleGeometry)
                if (geometry.IsCreated) geometry.Dispose();
            _obstacleGeometry.Clear();
            _meshObstacleCache.Clear();
        }

        public void Release()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            ReleaseWorld();
            if (_hullNodes.IsCreated)
            {
                foreach (var node in _hullNodes)
                {
                    if (node.Bounds.IsCreated) node.Bounds.Dispose();
                    if (node.Surface.IsCreated) node.Surface.Dispose();
                }
                _hullNodes.Dispose();
            }
            if (_queryStack.IsCreated) _queryStack.Dispose();
            if (_patchVertices.IsCreated) _patchVertices.Dispose();
            _hullNodes = default;
            _queryStack = default;
            _patchVertices = default;
            _lastContactLeaf = -1;
            _cachedContact = default;
        }

        private void OnDestroy() => Release();
    }
}
