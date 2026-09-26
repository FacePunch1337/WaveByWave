using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Profiling;
using StylizedWater3;
using WaveByWave.Player;
using WaveByWave.Combat;
using WaveByWave.Ships;
using Object = UnityEngine.Object;
using Random = Unity.Mathematics.Random;

namespace WaveByWave.Enemies
{
    [DefaultExecutionOrder(2500)]
    public sealed partial class DotsEnemyShipRuntime : MonoBehaviour
    {
        public static DotsEnemyShipRuntime Instance { get; private set; }
        public EnemyShipDefinition Definition { get; private set; }
        public bool CanSimulate => Definition != null && NetworkManager.Singleton != null &&
                                   NetworkManager.Singleton.IsServer;

        private sealed class SpawnRequest
        {
            public EnemyShipSpawnPoint Point;
            public Vector3 Center;
            public float Radius, MinimumRadius;
            public int Remaining;
            public int Attempts;
            public int WaveGroup;
            public Random Random;
        }

        private struct Target
        {
            public NetworkShipController Ship;
            public ShipCannonBattery Battery;
            public Vector3 Position;
            public float CollisionRadius;
        }

        private readonly List<EnemyShipSpawnPoint> _points = new();
        private readonly HashSet<EnemyShipSpawnPoint> _activated = new();
        private readonly List<SpawnRequest> _spawns = new();
        private readonly List<Target> _targets = new();
        private readonly Dictionary<int, Entity> _byId = new();
        private readonly Dictionary<int, EnemyShipView> _views = new();
        private readonly Dictionary<int, Matrix4x4> _physicsFrames = new();
        private readonly Dictionary<int, RigidTransform> _collisionPoses = new();
        private readonly Dictionary<int, EquipmentWaterQuery.CachedSurface> _shipWater = new();
        internal IReadOnlyDictionary<int, RigidTransform> CollisionPoses => _collisionPoses;
        internal uint CollisionRevision { get; private set; }
        private static readonly ProfilerMarker SimulationMarker = new("WaveByWave.Fleet.Simulation");
        private readonly List<Entity> _remove = new();
        private readonly EnemyShipSpatialIndex _fleet = new();
        private readonly List<int> _playerContactCandidates = new();
        private readonly List<int> _projectileContactCandidates = new();
        private bool _fleetInitialized;
        private RaycastHit[] _collisionHits = new RaycastHit[64];
        private readonly Collider[] _spawnOverlaps = new Collider[64];
        private readonly List<Vector3> _observers = new();
        private IReadOnlyList<Vector3> _crewPositions;
        private float _nextSimulation;
        private World _serverWorld;
        private EntityQuery _ships;
        private EquipmentWaterQuery _water;
        private NetworkWindController _wind;
        private int _scene;
        private int _nextId = 1;
        private int _lastFrame = -1;
        private int _simulationCursor;
        private bool _wasServer;
        private Vector3 _contactBoundsCenter;
        private Vector3 _contactBoundsHalfExtents;
        private Vector3 _buoyancyCenter;
        private Vector2 _buoyancySize;

        public int AliveCount => _byId.Count;
        public float Now => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening
            ? (float)NetworkManager.Singleton.ServerTime.Time : Time.time;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap() => EnsureInstance();

        public static DotsEnemyShipRuntime EnsureInstance()
        {
            if (Instance != null) return Instance;
            var root = new GameObject("DOTS Enemy Ship Runtime");
            return root.AddComponent<DotsEnemyShipRuntime>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            LoadDefinition();
            _scene = DotsEnemyRuntime.SceneKey(SceneManager.GetActiveScene().name);
        }

        private void LoadDefinition()
        {
            Definition = EnemyShipDefinition.Load();
            _water?.Dispose();
            _water = new EquipmentWaterQuery(Definition != null ? Definition.WaterProfile : null);
            _crewPositions = null;
            RefreshContactBounds();
        }

        private void RefreshContactBounds()
        {
            if (Definition == null) return;
            var center = Definition.CollisionCenter;
            var halfExtents = Definition.CollisionHalfExtents;
            var buoyancyCenter = Vector3.zero;
            var buoyancySize = Definition.WaterSampleSize;
            if (Definition.ViewPrefab != null &&
                Definition.ViewPrefab.TryGetComponent<EnemyShipView>(out var view))
            {
                center = view.ContactBoundsCenter;
                halfExtents = view.ContactBoundsHalfExtents;
                buoyancyCenter = view.BuoyancyCenter;
                buoyancySize = view.BuoyancySize;
            }
            var scale = Definition.ViewPrefab != null
                ? Definition.ViewPrefab.transform.localScale : Vector3.one;
            _contactBoundsCenter = Vector3.Scale(center, scale);
            _contactBoundsHalfExtents = Vector3.Max(Vector3.Scale(halfExtents,
                new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z))),
                Vector3.one * 0.05f);
            _buoyancyCenter = Vector3.Scale(buoyancyCenter, scale);
            _buoyancySize = new Vector2(Mathf.Max(0.1f, buoyancySize.x * Mathf.Abs(scale.x)),
                Mathf.Max(0.1f, buoyancySize.y * Mathf.Abs(scale.z)));
        }

        internal Vector3 ContactBoundsCenter => _contactBoundsCenter;
        internal Vector3 ContactBoundsHalfExtents => _contactBoundsHalfExtents;

        public void Register(EnemyShipSpawnPoint point)
        {
            if (!_points.Contains(point)) _points.Add(point);
        }

        public void Unregister(EnemyShipSpawnPoint point)
        {
            _points.Remove(point);
            _activated.Remove(point);
            _spawns.RemoveAll(request => request.Point == point);
        }

        private void Update()
        {
            var scene = DotsEnemyRuntime.SceneKey(SceneManager.GetActiveScene().name);
            var server = CanSimulate;
            if (_scene != scene || _wasServer && !server)
            {
                ClearServer();
                _activated.Clear();
                _scene = scene;
                DotsEnemyShipPresentation.Clear();
            }
            _wasServer = server;
            if (Definition == null) LoadDefinition();
        }

        private void LateUpdate() => DotsEnemyShipPresentation.Update(this);

        private bool AttachServer()
        {
            var world = ClientServerBootstrap.ServerWorld;
            if (world == null || !world.IsCreated) return false;
            if (_serverWorld == world) return true;
            _serverWorld = world;
            _ships = world.EntityManager.CreateEntityQuery(typeof(DotsEnemyShipState), typeof(DotsEnemyShipBrain));
            _byId.Clear();
            _physicsFrames.Clear();
            _collisionPoses.Clear();
            _shipWater.Clear();
            CollisionRevision++;
            _fleet.Clear();
            _fleetInitialized = false;
            return true;
        }

        public void TickServer(EnemyShipServerSystem system)
        {
            using var scope = SimulationMarker.Auto();
            if (_lastFrame == Time.frameCount || !AttachServer()) return;
            _lastFrame = Time.frameCount;
            if (_scene != DotsEnemyRuntime.SceneKey(SceneManager.GetActiveScene().name)) return;
            RefreshTargets();
            ActivatePoints();
            if (_ships.IsEmptyIgnoreFilter && _spawns.Count == 0) return;
            var now = Now;
            if (now < _nextSimulation)
            {
                RestorePhysicsViews();
                return;
            }
            _nextSimulation = now + 1f / Mathf.Max(1, Definition.SimulationRate);
            if (!EnsureCollisionGeometry()) return;
            if (!_fleetInitialized)
            {
                using var states = _ships.ToComponentDataArray<DotsEnemyShipState>(Allocator.Temp);
                _fleet.Rebuild(states, _authoredHullRadius);
                _fleetInitialized = true;
            }
            SpawnBatch();

            var targets = new NativeArray<EnemyShipTarget>(_targets.Count, Allocator.TempJob);
            using var targetLifetime = targets;
            for (var i = 0; i < _targets.Count; i++)
                targets[i] = new EnemyShipTarget { Position = _targets[i].Position, Index = i };
            system.Steer(targets, Definition);

            var manager = _serverWorld.EntityManager;
            using (var entities = _ships.ToEntityArray(Allocator.Temp))
            {
                var entityCount = entities.Length;
                var budget = Mathf.Min(entityCount,
                    Mathf.Clamp(Definition.SimulationBudgetPerTick, 16, 512));
                var start = entityCount > 0 ? _simulationCursor % entityCount : 0;
                var scanned = 0;
                var simulated = 0;
                while (scanned < entityCount && simulated < budget)
                {
                    var entity = entities[(start + scanned) % entityCount];
                    scanned++;
                    var state = manager.GetComponentData<DotsEnemyShipState>(entity);
                    var brain = manager.GetComponentData<DotsEnemyShipBrain>(entity);
                    if (state.Scene != _scene) { _remove.Add(entity); continue; }
                    if (!SimulationDue(state, brain, now)) continue;
                    if (state.Health > 0 && brain.CrewSpawned == 0 && EnsureCrew(state.Id, state.Seed, brain.WaveGroup))
                        brain.CrewSpawned = 1;
                    Simulate(entity, ref state, ref brain, now);
                    simulated++;
                    if (state.Health > 0) _fleet.Set(state.Id, state.Position.xz);
                    else _fleet.Remove(state.Id);
                    manager.SetComponentData(entity, state);
                    manager.SetComponentData(entity, brain);
                    CachePhysicsFrame(state);
                    if (_views.TryGetValue(state.Id, out var view) && view != null)
                        view.SetSimulationPose(state.Position, state.Rotation);
                }
                if (entityCount > 0) _simulationCursor = (start + scanned) % entityCount;
                else _simulationCursor = 0;
            }
            RestorePhysicsViews();
            foreach (var entity in _remove)
            {
                if (!manager.Exists(entity)) continue;
                var state = manager.GetComponentData<DotsEnemyShipState>(entity);
                _byId.Remove(state.Id);
                _fleet.Remove(state.Id);
                _physicsFrames.Remove(state.Id);
                _collisionPoses.Remove(state.Id);
                _shipWater.Remove(state.Id);
                CollisionRevision++;
                DotsEnemyRuntime.Instance?.DespawnGroup(DotsEnemyRuntime.CrewGroupForShip(state.Id));
                manager.DestroyEntity(entity);
            }
            _remove.Clear();
        }

        private void RefreshTargets()
        {
            _targets.Clear();
            _observers.Clear();
            foreach (var client in NetworkManager.Singleton.ConnectedClientsList)
                if (client.PlayerObject != null) _observers.Add(client.PlayerObject.transform.position);
            foreach (var ship in NetworkShipController.ServerShips)
            {
                if (ship == null || !ship.IsSpawned || !ship.IsServer) continue;
                ship.TryGetComponent<ShipCannonBattery>(out var battery);
                _targets.Add(new Target { Ship = ship, Battery = battery,
                    Position = ship.SimulationPosition,
                    CollisionRadius = ship.ContactBoundsCenter.magnitude +
                        ship.ContactBoundsHalfExtents.magnitude });
                _observers.Add(ship.SimulationPosition);
            }
            _wind ??= Object.FindFirstObjectByType<NetworkWindController>();
        }

        private void ActivatePoints()
        {
            foreach (var point in _points)
            {
                if (point == null || !point.isActiveAndEnabled || _activated.Contains(point) ||
                    DotsEnemyRuntime.SceneKey(point.gameObject.scene.name) != _scene) continue;
                var activate = point.Mode == EnemySpawnMode.WhenPointAppears;
                if (!activate)
                    foreach (var target in _targets)
                        if ((target.Position - point.transform.position).sqrMagnitude <=
                            point.ActivationRadius * point.ActivationRadius) { activate = true; break; }
                if (!activate) continue;
                _activated.Add(point);
                var seed = point.Seed != 0 ? (uint)point.Seed : (uint)Environment.TickCount;
                _spawns.Add(new SpawnRequest { Point = point, Center = point.transform.position,
                    Radius = point.SpawnRadius, Remaining = point.Count, Random = new Random(seed | 1u) });
            }
        }

        public bool SpawnAt(Vector3 center, int count = 1, float radius = 5f, uint seed = 0,
            int waveGroup = 0, float minimumRadius = 0f)
        {
            if (!CanSimulate || !AttachServer()) return false;
            count = Mathf.Min(count, Definition.MaximumShips - AliveCount);
            if (count <= 0) return false;
            var maximumRadius = Mathf.Max(1f, radius);
            _spawns.Add(new SpawnRequest { Center = center, Radius = maximumRadius,
                MinimumRadius = Mathf.Clamp(minimumRadius, 0f, maximumRadius),
                Remaining = count, WaveGroup = waveGroup,
                Random = new Random((seed != 0 ? seed : (uint)Environment.TickCount) | 1u) });
            return true;
        }

        public int NightWaveRemaining(int group)
        {
            if (!CanSimulate || _serverWorld == null || !_serverWorld.IsCreated) return 0;
            var count = 0;
            foreach (var request in _spawns) if (request.WaveGroup == group) count += request.Remaining;
            var manager = _serverWorld.EntityManager;
            foreach (var entity in _byId.Values)
                if (manager.Exists(entity) && manager.GetComponentData<DotsEnemyShipBrain>(entity).WaveGroup == group &&
                    manager.GetComponentData<DotsEnemyShipState>(entity).Health > 0f) count++;
            return count;
        }

        public int NightWavePendingCrew(int group)
        {
            if (!CanSimulate || Definition == null || Definition.CrewCount <= 0 ||
                _serverWorld == null || !_serverWorld.IsCreated) return 0;
            var count = 0;
            foreach (var request in _spawns)
                if (request.WaveGroup == group) count += request.Remaining * Definition.CrewCount;
            var manager = _serverWorld.EntityManager;
            foreach (var entity in _byId.Values)
            {
                if (!manager.Exists(entity)) continue;
                var state = manager.GetComponentData<DotsEnemyShipState>(entity);
                var brain = manager.GetComponentData<DotsEnemyShipBrain>(entity);
                if (state.Health > 0f && brain.WaveGroup == group && brain.CrewSpawned == 0)
                    count += Definition.CrewCount;
            }
            return count;
        }

        public void DespawnGroup(int group)
        {
            _spawns.RemoveAll(request => request.WaveGroup == group);
            if (!CanSimulate || !AttachServer()) return;
            var manager = _serverWorld.EntityManager;
            using var entities = _ships.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                if (!manager.Exists(entity) ||
                    manager.GetComponentData<DotsEnemyShipBrain>(entity).WaveGroup != group) continue;
                var state = manager.GetComponentData<DotsEnemyShipState>(entity);
                _byId.Remove(state.Id);
                _fleet.Remove(state.Id);
                _physicsFrames.Remove(state.Id);
                _collisionPoses.Remove(state.Id);
                _shipWater.Remove(state.Id);
                DotsEnemyRuntime.Instance?.DespawnGroup(DotsEnemyRuntime.CrewGroupForShip(state.Id));
                manager.DestroyEntity(entity);
            }
            CollisionRevision++;
        }

        private void SpawnBatch()
        {
            if (_spawns.Count == 0 || _byId.Count >= Definition.MaximumShips) return;
            var manager = _serverWorld.EntityManager;
            using var prefabQuery = manager.CreateEntityQuery(typeof(DotsEnemyShipPrefab));
            if (prefabQuery.IsEmptyIgnoreFilter) return;
            var prefab = prefabQuery.GetSingleton<DotsEnemyShipPrefab>().Value;
            var budget = Mathf.Clamp(Definition.SpawnsPerFrame, 1, 32);
            while (budget-- > 0 && _spawns.Count > 0 && _byId.Count < Definition.MaximumShips)
            {
                var request = _spawns[0];
                var angle = request.Random.NextFloat(0, math.PI * 2);
                var minimumRadius = Mathf.Clamp(request.MinimumRadius, 0f, request.Radius);
                var distance = math.sqrt(math.lerp(minimumRadius * minimumRadius,
                    request.Radius * request.Radius, request.Random.NextFloat()));
                var candidate = request.Center + new Vector3(math.cos(angle), 0, math.sin(angle)) * distance;
                request.Attempts++;
                if (_water.TryHeight(candidate, out var height))
                {
                    candidate.y = height + Definition.WaterlineOffset - _buoyancyCenter.y;
                    if (IsSpawnClear(candidate))
                    {
                        var heading = request.Random.NextFloat(0, math.PI * 2);
                        var id = _nextId++;
                        var state = new DotsEnemyShipState
                        {
                            Id = id,
                            Scene = _scene,
                            Seed = request.Random.NextUInt() | 1u,
                            Position = candidate,
                            Rotation = quaternion.RotateY(heading),
                            Health = Definition.MaximumHealth
                        };
                        var entity = manager.Instantiate(prefab);
                        manager.SetComponentData(entity, state);
                        manager.SetComponentData(entity, new DotsEnemyShipBrain
                        {
                            Target = -1,
                            Heading = heading,
                            LastTick = Now,
                            NextFire = Now + request.Random.NextFloat(1f, Definition.FireCooldown),
                            OrbitSide = (byte)(state.Seed & 1u),
                            WaveGroup = request.WaveGroup
                        });
                        _byId.Add(id, entity);
                        _shipWater[id] = _water.CacheSurface(candidate, Definition.WaterProfile);
                        CachePhysicsFrame(state);
                        _fleet.Set(id, state.Position.xz);
                        request.Remaining--;
                        request.Attempts = 0;
                    }
                }
                if (request.Remaining <= 0 || request.Attempts > 128)
                {
                    if (request.Remaining > 0)
                        Debug.LogWarning($"[Enemy ships] Could not find clear water for {request.Remaining} ships.");
                    _spawns.RemoveAt(0);
                }
            }
        }

        private bool IsSpawnClear(Vector3 position)
        {
            if (!_fleet.IsClear(new float2(position.x, position.z))) return false;
            var count = Physics.OverlapBoxNonAlloc(position + _contactBoundsCenter,
                _contactBoundsHalfExtents + Vector3.one * Definition.CollisionSkin,
                _spawnOverlaps, Quaternion.identity, Definition.CollisionLayers, QueryTriggerInteraction.Ignore);
            if (count == _spawnOverlaps.Length) return false;
            for (var i = 0; i < count; i++)
            {
                var overlap = _spawnOverlaps[i];
                if (overlap != null && overlap.GetComponentInParent<WaterObject>() == null) return false;
            }
            return true;
        }

        private bool SimulationDue(in DotsEnemyShipState state, in DotsEnemyShipBrain brain, float now)
        {
            var near = DistanceToObserversSquared(state.Position) <=
                Definition.DetailedSimulationDistance * Definition.DetailedSimulationDistance;
            var rate = state.Health > 0 && near
                ? Definition.SimulationRate : Definition.DistantSimulationRate;
            return now - brain.LastTick >= 1f / Mathf.Max(1, rate) - 0.0001f;
        }

        private void Simulate(Entity entity, ref DotsEnemyShipState state,
            ref DotsEnemyShipBrain brain, float now)
        {
            var deltaTime = Mathf.Clamp(now - brain.LastTick, 0f, 1f);
            brain.LastTick = now;
            if (deltaTime <= 0f) return;
            if (state.Health <= 0)
            {
                var elapsed = math.max(0f, now - state.DeathAt);
                var t = math.saturate(elapsed / math.max(0.1f, Definition.SinkDuration));
                var eased = t * t * (3f - 2f * t);
                state.Position = state.DeathPosition + new float3(0f,
                    -Definition.SinkSpeed * Definition.SinkDuration * eased, 0f);
                state.Rotation = math.mul(state.DeathRotation,
                    quaternion.RotateZ(math.radians(4f * Definition.SinkDuration * eased)));
                if (now >= state.DeathAt + Definition.SinkDuration) _remove.Add(entity);
                return;
            }

            if (math.lengthsq(brain.DesiredDirection) > 0.001f)
            {
                var desiredHeading = math.atan2(brain.DesiredDirection.x, brain.DesiredDirection.z);
                var turn = DeltaAngle(brain.Heading, desiredHeading);
                brain.Heading += math.clamp(turn, -math.radians(Definition.TurnSpeed) * deltaTime,
                    math.radians(Definition.TurnSpeed) * deltaTime);
            }
            var forward = new Vector3(Mathf.Sin(brain.Heading), 0f, Mathf.Cos(brain.Heading));
            var wind = _wind != null ? Vector3.ProjectOnPlane(_wind.Direction, Vector3.up).normalized : Vector3.forward;
            var capture = Mathf.Abs(Vector3.Dot(wind, forward));
            var targetSpeed = Mathf.Min(Definition.MaximumSpeed,
                Definition.BaseSpeed + Definition.WindSpeedBonus * capture);
            var radial = brain.Target >= 0 && brain.Target < _targets.Count
                ? Vector3.ProjectOnPlane(_targets[brain.Target].Position - (Vector3)state.Position, Vector3.up).normalized
                : Vector3.zero;
            targetSpeed *= brain.Target < 0 ? 0f : Mathf.Clamp01(Vector3.Dot(forward, brain.DesiredDirection));
            var rate = targetSpeed > brain.Speed ? Definition.Acceleration : Definition.Deceleration;
            brain.Speed = Mathf.MoveTowards(brain.Speed, targetSpeed, rate * deltaTime);
            var pursuitVelocity = brain.Target < 0 ? Vector3.zero :
                forward * brain.Speed + wind * Definition.WindDriftSpeed;
            // Turning around and crosswind cannot carry the pursuer away indefinitely.
            var outward = Vector3.Dot(pursuitVelocity, radial);
            if (outward < 0) pursuitVelocity -= radial * outward;
            var velocity = pursuitVelocity + (Vector3)brain.PushVelocity;
            brain.PushVelocity *= math.exp(-Definition.ContactPushDamping * deltaTime);
            var displacement = velocity * deltaTime;
            displacement = ResolveCollision(state.Id, state.Position, state.Rotation, displacement, deltaTime);
            var next = (Vector3)state.Position + displacement;

            var yaw = Quaternion.Euler(0f, brain.Heading * Mathf.Rad2Deg, 0f);
            var targetRotation = yaw;
            var waterHeight = 0f;
            var normal = Vector3.up;
            if (!_shipWater.TryGetValue(state.Id, out var waterSource) || waterSource == null)
            {
                waterSource = _water.CacheSurface(next, Definition.WaterProfile);
                if (waterSource != null) _shipWater[state.Id] = waterSource;
            }
            var hasWater = waterSource != null &&
                _water.TrySurface(next + yaw * _buoyancyCenter, _buoyancySize, yaw,
                    Definition.WaterRollAmount, waterSource,
                    out waterHeight, out normal);
            if (hasWater)
            {
                next.y = Mathf.Lerp(state.Position.y, waterHeight + Definition.WaterlineOffset - _buoyancyCenter.y,
                    1f - Mathf.Exp(-Definition.WaterHeightResponse * deltaTime));
                targetRotation = Quaternion.FromToRotation(Vector3.up, normal) * yaw;
            }
            state.Position = next;
            var previousRotation = state.Rotation;
            var desiredRotation = math.slerp(state.Rotation, targetRotation,
                1f - math.exp(-Definition.WaterTiltResponse * deltaTime));
            state.Rotation = ResolveHullRotation(state.Id, next, previousRotation, desiredRotation);
            if (math.dot(state.Rotation.value, desiredRotation.value) < 0.99999f)
                brain.Heading = ((Quaternion)state.Rotation).eulerAngles.y * Mathf.Deg2Rad;
            TryFire(ref state, ref brain, now);
        }

        private Vector3 ResolveCollision(int id, Vector3 position, Quaternion rotation,
            Vector3 displacement, float deltaTime)
        {
            displacement = ResolveHullMotion(id, position, rotation, displacement, deltaTime);
            var distance = displacement.magnitude;
            if (distance < 0.0001f) return displacement;
            var center = position + rotation * _contactBoundsCenter;
            int count;
            while (true)
            {
                count = Physics.BoxCastNonAlloc(center, _contactBoundsHalfExtents, displacement / distance,
                    _collisionHits, rotation, distance + Definition.CollisionSkin, Definition.CollisionLayers, QueryTriggerInteraction.Ignore);
                if (count < _collisionHits.Length || _collisionHits.Length >= 8192) break;
                // Ignored fleet colliders must not fill the query buffer and form an
                // invisible wall. Grow only on saturation and reuse the allocation.
                Array.Resize(ref _collisionHits, _collisionHits.Length * 2);
            }
            if (count == _collisionHits.Length) return Vector3.zero;
            var nearest = float.PositiveInfinity;
            for (var i = 0; i < count; i++)
            {
                var hit = _collisionHits[i];
                if (hit.collider == null || hit.collider.GetComponentInParent<WaterObject>() != null) continue;
                var view = hit.collider.GetComponentInParent<EnemyShipView>();
                if (view != null || hit.collider.GetComponentInParent<NetworkShipController>() != null ||
                    hit.collider.GetComponentInParent<NetworkPlayerController>() != null) continue;
                if (hit.distance < nearest) nearest = hit.distance;
            }
            if (float.IsPositiveInfinity(nearest)) return displacement;
            var accepted = Mathf.Max(0f, nearest - Definition.CollisionSkin);
            return displacement.normalized * Mathf.Min(distance, accepted);
        }

        private void TryFire(ref DotsEnemyShipState state, ref DotsEnemyShipBrain brain, float now)
        {
            if (brain.Target < 0 || brain.Target >= _targets.Count || now < brain.NextFire ||
                brain.TargetDistance > Definition.FireRange) return;
            var target = _targets[brain.Target];
            if (target.Ship == null) return;
            var rotation = (Quaternion)state.Rotation;
            var right = rotation * Vector3.right;
            var toTarget = target.Position - (Vector3)state.Position;
            toTarget.y = 0;
            if (toTarget.sqrMagnitude < 1f) return;
            var side = Mathf.Sign(Vector3.Dot(right, toTarget));
            var broadside = Mathf.Abs(Vector3.Dot(right, toTarget.normalized));
            if (broadside < Mathf.Cos(Definition.BroadsideFireAngle * Mathf.Deg2Rad)) return;
            var origin = (Vector3)state.Position + rotation * Vector3.up * Definition.CannonHeight +
                         right * (side * Definition.CannonSideOffset);
            if (_views.TryGetValue(state.Id, out var view) && view != null &&
                view.TryGetMuzzle(side, state.ShotRevision + 1, out var configuredOrigin))
                origin = configuredOrigin;
            var aim = target.Position + Vector3.up * 0.7f;
            if (!HasLineOfFire(state.Id, origin, aim, target.Battery))
            { brain.NextFire = now + 0.25f; return; }
            if (!TryBallisticVelocity(origin, aim, Definition.ProjectileSpeed, Definition.ProjectileGravity,
                    out var velocity))
                velocity = (aim - origin).normalized * Definition.ProjectileSpeed;
            var revision = state.ShotRevision + 1;
            var random = new Random(math.hash(new uint3(state.Seed, revision, (uint)state.Id)) | 1u);
            var nextFire = now + Definition.FireCooldown + random.NextFloat(0, Definition.FireCooldownJitter);
            velocity = ApplyAimSpread(velocity, Definition.CannonAccuracy, Definition.MaximumAimSpreadAngle, ref random);
            if (!DotsCannonProjectileSystem.Spawn(new DotsCannonProjectile
                {
                    Position = origin, Previous = origin, Origin = origin, Velocity = velocity,
                    Gravity = Vector3.down * Definition.ProjectileGravity, Started = now,
                    Lifetime = Definition.ProjectileLifetime, Radius = Definition.ProjectileRadius,
                    Damage = Definition.ProjectileDamage, EnemyTeam = 1,
                    EnemyShipId = state.Id, ShotRevision = revision
                })) return;
            state.ShotRevision = revision;
            state.ShotOrigin = origin;
            state.ShotVelocity = velocity;
            state.ShotStarted = now;
            brain.NextFire = nextFire;
        }

        private static Vector3 ApplyAimSpread(Vector3 velocity, float accuracy, float maximumAngle, ref Random random)
        {
            var angle = math.radians(math.clamp(maximumAngle, 0f, 45f) *
                                     (1f - math.saturate(accuracy / 100f)));
            var speed = math.length((float3)velocity);
            if (angle <= 0f || speed <= 0.0001f) return velocity;

            // Uniform directions inside a cone around the ballistic solution. Change only
            // the direction; projectile speed, gravity and damage remain authored values.
            var direction = (float3)velocity / speed;
            var reference = math.abs(direction.y) < 0.999f ? new float3(0, 1, 0) : new float3(1, 0, 0);
            var right = math.normalize(math.cross(reference, direction));
            var up = math.cross(direction, right);
            var cosTheta = math.lerp(1f, math.cos(angle), random.NextFloat());
            var sinTheta = math.sqrt(math.max(0f, 1f - cosTheta * cosTheta));
            math.sincos(random.NextFloat(0f, math.PI * 2f), out var sinPhi, out var cosPhi);
            var scattered = direction * cosTheta + (right * cosPhi + up * sinPhi) * sinTheta;
            return math.normalize(scattered) * speed;
        }

        private bool HasLineOfFire(int shipId, Vector3 origin, Vector3 target, ShipCannonBattery intendedTarget)
        {
            var delta = target - origin;
            var distance = delta.magnitude;
            if (distance < 0.001f) return false;
            var count = Physics.RaycastNonAlloc(origin, delta / distance, _collisionHits, distance,
                Definition.CollisionLayers, QueryTriggerInteraction.Ignore);
            if (count == _collisionHits.Length) return false;
            var nearest = float.PositiveInfinity;
            ShipCannonBattery first = null;
            for (var i = 0; i < count; i++)
            {
                var hit = _collisionHits[i];
                if (hit.distance >= nearest) continue;
                if (hit.collider == null || hit.collider.GetComponentInParent<WaterObject>() != null) continue;
                var view = hit.collider.GetComponentInParent<EnemyShipView>();
                if (view != null && view.ShipId == shipId) continue;
                nearest = hit.distance;
                first = hit.collider.GetComponentInParent<ShipCannonBattery>();
            }
            return float.IsPositiveInfinity(nearest) || first == intendedTarget;
        }

        private static bool TryBallisticVelocity(Vector3 origin, Vector3 target, float speed, float gravity,
            out Vector3 velocity)
        {
            velocity = default;
            var flat = Vector3.ProjectOnPlane(target - origin, Vector3.up);
            var x = flat.magnitude;
            var y = target.y - origin.y;
            if (x < 0.01f || gravity <= 0.001f) return false;
            var speedSq = speed * speed;
            var root = speedSq * speedSq - gravity * (gravity * x * x + 2f * y * speedSq);
            if (root < 0f) return false;
            var angle = Mathf.Atan((speedSq - Mathf.Sqrt(root)) / (gravity * x));
            velocity = flat.normalized * (Mathf.Cos(angle) * speed) + Vector3.up * (Mathf.Sin(angle) * speed);
            return true;
        }

        internal void ReportProjectileImpact(int shipId, uint revision, Vector3 point,
            Vector3 normal, bool water, bool show)
        {
            if (!_byId.TryGetValue(shipId, out var entity) || _serverWorld == null ||
                !_serverWorld.IsCreated || !_serverWorld.EntityManager.Exists(entity)) return;
            var manager = _serverWorld.EntityManager;
            var state = manager.GetComponentData<DotsEnemyShipState>(entity);
            state.ImpactRevision++;
            state.ImpactShotRevision = revision;
            state.ImpactPoint = point;
            state.ImpactNormal = normal;
            state.ImpactAt = Now;
            state.ImpactFlags = (byte)((water ? 1 : 0) | (show ? 2 : 0));
            manager.SetComponentData(entity, state);
        }

        internal bool DamageFromPlayerCannon(int id, float damage, Vector3 source, Vector3? impactPoint = null)
        {
            if (!CanSimulate || !float.IsFinite(damage) || damage <= 0 || !AttachServer() ||
                !_byId.TryGetValue(id, out var entity) || !_serverWorld.EntityManager.Exists(entity)) return false;
            var manager = _serverWorld.EntityManager;
            var state = manager.GetComponentData<DotsEnemyShipState>(entity);
            if (state.Health <= 0) return false;
            WaveByWave.Combat.DotsDamagePopups.ReportServer(impactPoint ?? (Vector3)state.Position, damage);
            var remainingHealth = Mathf.Max(0, state.Health - damage);
            state.HitRevision++;
            if (remainingHealth <= 0)
            {
                BeginSinking(ref state);
                DotsEnemyRuntime.Instance?.DespawnGroup(DotsEnemyRuntime.CrewGroupForShip(id));
            }
            else state.Health = remainingHealth;
            manager.SetComponentData(entity, state);
            CachePhysicsFrame(state);
            return true;
        }

        internal void SinkAfterCrewDefeated(int id)
        {
            if (!CanSimulate || !Definition.SinkWhenCrewDefeated ||
                _serverWorld == null || !_serverWorld.IsCreated ||
                !_byId.TryGetValue(id, out var entity)) return;
            var manager = _serverWorld.EntityManager;
            if (!manager.Exists(entity)) return;
            var state = manager.GetComponentData<DotsEnemyShipState>(entity);
            if (state.Scene != _scene || !BeginSinking(ref state)) return;
            manager.SetComponentData(entity, state);
            CachePhysicsFrame(state);
            // Every crew member is already dead. Keep their normal corpse/loot cleanup
            // instead of running DespawnGroup's full enemy scan on the final kill.
        }

        private bool BeginSinking(ref DotsEnemyShipState state)
        {
            if (state.Health <= 0) return false;
            state.Health = 0;
            state.DeathAt = Now;
            state.DeathPosition = state.Position;
            state.DeathRotation = state.Rotation;
            _fleet.Remove(state.Id);
            return true;
        }

        internal void RegisterView(int id, EnemyShipView view)
        {
            if (view != null) _views[id] = view;
        }

        internal void UnregisterView(int id, EnemyShipView view)
        {
            if (_views.TryGetValue(id, out var current) && current == view) _views.Remove(id);
        }

        internal void ApplyPlayerContactPush(int id, Vector3 velocity)
            => AddContactPush(id, velocity);

        private void AddContactPush(int id, Vector3 velocity)
        {
            if (_serverWorld == null || !_serverWorld.IsCreated ||
                !_byId.TryGetValue(id, out var entity) ||
                !_serverWorld.EntityManager.Exists(entity)) return;
            var manager = _serverWorld.EntityManager;
            var brain = manager.GetComponentData<DotsEnemyShipBrain>(entity);
            velocity.y = 0f;
            var combined = (Vector3)brain.PushVelocity + velocity;
            brain.PushVelocity = Vector3.ClampMagnitude(combined,
                Definition.MaximumContactPushSpeed);
            manager.SetComponentData(entity, brain);
        }

        private bool EnsureCrew(int id, uint seed, int waveGroup)
        {
            if (Definition.CrewCount == 0) return true;
            var skeletons = DotsEnemyRuntime.EnsureInstance();
            if (skeletons == null || skeletons.Catalog == null || !EnsureCrewPositions(skeletons.Catalog)) return false;
            return skeletons.SpawnCrewOnShip(id, _crewPositions,
                Definition.CrewCombatType, seed, waveGroup);
        }

        internal float DistanceToObserversSquared(Vector3 position)
        {
            var best = float.PositiveInfinity;
            foreach (var observer in _observers) best = Mathf.Min(best, (observer - position).sqrMagnitude);
            return best;
        }

        // Player-ship broad phase only. Pursuit still updates every enemy independent
        // of range; a distant box cannot touch this hull during the current step.
        internal bool FillCollisionPosesNear(Vector3 position, float radius,
            Dictionary<int, RigidTransform> result)
        {
            _fleet.CollectWithinRadius(new float2(position.x, position.z), radius,
                _playerContactCandidates);
            var changed = false;
            var count = 0;
            foreach (var id in _playerContactCandidates)
            {
                if (!_collisionPoses.TryGetValue(id, out var pose)) continue;
                count++;
                if (!result.TryGetValue(id, out var old) ||
                    math.any(old.pos != pose.pos) || math.any(old.rot.value != pose.rot.value))
                    changed = true;
            }
            changed |= result.Count != count;
            if (!changed) return false;
            result.Clear();
            foreach (var id in _playerContactCandidates)
                if (_collisionPoses.TryGetValue(id, out var pose)) result.Add(id, pose);
            return true;
        }

        public bool ProjectileHit(Vector3 from, Vector3 to, float radius,
            out int shipId, out float fraction, out Vector3 normal)
        {
            shipId = 0;
            fraction = 1f;
            normal = Vector3.up;
            if (!CanSimulate) return false;
            _fleet.CollectCandidates(-1, from, to - from, _projectileContactCandidates);
            var half = _contactBoundsHalfExtents + Vector3.one * radius;
            foreach (var id in _projectileContactCandidates)
            {
                if (!_collisionPoses.TryGetValue(id, out var pose)) continue;
                var rotation = (Quaternion)pose.rot;
                var inverse = Quaternion.Inverse(rotation);
                var start = inverse * (from - (Vector3)pose.pos) - _contactBoundsCenter;
                var end = inverse * (to - (Vector3)pose.pos) - _contactBoundsCenter;
                if (!SegmentBox(start, end, half, out var hit, out var face) || hit >= fraction) continue;
                shipId = id;
                fraction = hit;
                normal = rotation * face;
            }
            return shipId != 0;
        }

        private static bool SegmentBox(Vector3 from, Vector3 to, Vector3 half,
            out float fraction, out Vector3 normal)
        {
            fraction = 0f;
            normal = Vector3.up;
            var delta = to - from;
            var exit = 1f;
            for (var axis = 0; axis < 3; axis++)
            {
                var start = from[axis];
                var movement = delta[axis];
                if (Mathf.Abs(movement) < 0.000001f)
                {
                    if (start < -half[axis] || start > half[axis]) return false;
                    continue;
                }
                var a = (-half[axis] - start) / movement;
                var b = (half[axis] - start) / movement;
                var face = Vector3.zero;
                face[axis] = movement > 0f ? -1f : 1f;
                if (a > b) (a, b) = (b, a);
                if (a > fraction) { fraction = a; normal = face; }
                exit = Mathf.Min(exit, b);
                if (fraction > exit) return false;
            }
            return exit >= 0f && fraction <= 1f;
        }

        private void RestorePhysicsViews()
        {
            var restored = false;
            foreach (var view in _views.Values)
            {
                if (view == null) continue;
                view.RestoreSimulationPose();
                restored = true;
            }
            if (restored) Physics.SyncTransforms();
        }

        public bool TryGetPhysicsFrame(int id, out Matrix4x4 frame)
            => _physicsFrames.TryGetValue(id, out frame);

        private void CachePhysicsFrame(DotsEnemyShipState state)
        {
            _physicsFrames[state.Id] = Matrix4x4.TRS(state.Position, state.Rotation,
                Definition.ViewPrefab != null ? Definition.ViewPrefab.transform.localScale : Vector3.one);
            if (state.Health > 0) _collisionPoses[state.Id] = new RigidTransform(state.Rotation, state.Position);
            else _collisionPoses.Remove(state.Id);
            CollisionRevision++;
        }

        private static float DeltaAngle(float current, float target) =>
            math.atan2(math.sin(target - current), math.cos(target - current));

        public World ServerWorld => _serverWorld;
        public EntityQuery ServerQuery => _ships;

        private void ClearServer()
        {
            if (_serverWorld != null && _serverWorld.IsCreated)
                _serverWorld.EntityManager.DestroyEntity(_ships);
            _byId.Clear();
            _physicsFrames.Clear();
            _collisionPoses.Clear();
            _shipWater.Clear();
            CollisionRevision++;
            _views.Clear();
            _spawns.Clear();
            _fleet.Clear();
            _fleetInitialized = false;
            ReleaseCollisionGeometry();
            _nextSimulation = 0;
            _simulationCursor = 0;
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            ClearServer();
            DotsEnemyShipPresentation.Dispose();
            _water?.Dispose();
            Instance = null;
        }
    }
}
