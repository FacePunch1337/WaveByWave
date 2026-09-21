using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using StylizedWater3;
using WaveByWave.Player;
using WaveByWave.Ships;
using Object = UnityEngine.Object;
using Random = Unity.Mathematics.Random;

namespace WaveByWave.Enemies
{
    [DefaultExecutionOrder(9840)]
    public sealed class DotsEnemyShipRuntime : MonoBehaviour
    {
        public static DotsEnemyShipRuntime Instance { get; private set; }
        public EnemyShipDefinition Definition { get; private set; }
        public bool CanSimulate => Definition != null && NetworkManager.Singleton != null &&
                                   NetworkManager.Singleton.IsServer;

        private sealed class SpawnRequest
        {
            public EnemyShipSpawnPoint Point;
            public Vector3 Center;
            public float Radius;
            public int Remaining;
            public int Attempts;
            public Random Random;
        }

        private struct Target
        {
            public NetworkShipController Ship;
            public ShipCannonBattery Battery;
            public Vector3 Position;
        }

        private struct Projectile
        {
            public int ShipId;
            public uint Revision;
            public Vector3 Origin;
            public Vector3 Velocity;
            public Vector3 Previous;
            public float Started;
            public int Sample;
        }

        private readonly List<EnemyShipSpawnPoint> _points = new();
        private readonly HashSet<EnemyShipSpawnPoint> _activated = new();
        private readonly List<SpawnRequest> _spawns = new();
        private readonly List<Target> _targets = new();
        private readonly List<Projectile> _projectiles = new();
        private readonly Dictionary<int, Entity> _byId = new();
        private readonly Dictionary<int, EnemyShipView> _views = new();
        private readonly List<Entity> _remove = new();
        private World _serverWorld;
        private EntityQuery _ships;
        private EquipmentWaterQuery _water;
        private NetworkWindController _wind;
        private int _scene;
        private int _nextId = 1;
        private int _lastFrame = -1;
        private float _nextTargetRefresh;
        private bool _wasServer;
        private const float ProjectileStep = 1f / 60f;

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
            Definition = Resources.Load<EnemyShipDefinition>("EnemyShipDefinition");
            _water?.Dispose();
            _water = new EquipmentWaterQuery(Definition != null ? Definition.WaterProfile : null);
        }

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
            return true;
        }

        public void TickServer(EnemyShipServerSystem system)
        {
            if (_lastFrame == Time.frameCount || !AttachServer()) return;
            _lastFrame = Time.frameCount;
            if (_scene != DotsEnemyRuntime.SceneKey(SceneManager.GetActiveScene().name)) return;
            RefreshTargets();
            ActivatePoints();
            SpawnBatch();

            var targets = new NativeArray<EnemyShipTarget>(_targets.Count, Allocator.TempJob);
            using var targetLifetime = targets;
            for (var i = 0; i < _targets.Count; i++)
                targets[i] = new EnemyShipTarget { Position = _targets[i].Position, Index = i };
            system.Steer(targets, Definition);

            var manager = _serverWorld.EntityManager;
            using (var entities = _ships.ToEntityArray(Allocator.Temp))
            {
                foreach (var entity in entities)
                {
                    var state = manager.GetComponentData<DotsEnemyShipState>(entity);
                    var brain = manager.GetComponentData<DotsEnemyShipBrain>(entity);
                    if (state.Scene != _scene) { _remove.Add(entity); continue; }
                    Simulate(entity, ref state, ref brain);
                    manager.SetComponentData(entity, state);
                    manager.SetComponentData(entity, brain);
                }
            }
            SimulateProjectiles();
            foreach (var entity in _remove)
            {
                if (!manager.Exists(entity)) continue;
                var state = manager.GetComponentData<DotsEnemyShipState>(entity);
                _byId.Remove(state.Id);
                DotsEnemyRuntime.Instance?.DespawnGroup(DotsEnemyRuntime.CrewGroupForShip(state.Id));
                manager.DestroyEntity(entity);
            }
            _remove.Clear();
        }

        private void RefreshTargets()
        {
            if (Time.unscaledTime < _nextTargetRefresh) return;
            _nextTargetRefresh = Time.unscaledTime + 0.2f;
            _targets.Clear();
            foreach (var ship in Object.FindObjectsByType<NetworkShipController>(FindObjectsSortMode.None))
            {
                if (ship == null || !ship.IsSpawned || !ship.IsServer ||
                    !ship.TryGetComponent<ShipCannonBattery>(out var battery) || battery.Health <= 0) continue;
                _targets.Add(new Target { Ship = ship, Battery = battery,
                    Position = ship.TryGetComponent<Rigidbody>(out var body) ? body.position : ship.transform.position });
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

        public bool SpawnAt(Vector3 center, int count = 1, float radius = 5f, uint seed = 0)
        {
            if (!CanSimulate || !AttachServer()) return false;
            count = Mathf.Min(count, Definition.MaximumShips - AliveCount);
            if (count <= 0) return false;
            _spawns.Add(new SpawnRequest { Center = center, Radius = Mathf.Max(1f, radius),
                Remaining = count,
                Random = new Random((seed != 0 ? seed : (uint)Environment.TickCount) | 1u) });
            return true;
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
                var distance = math.sqrt(request.Random.NextFloat()) * request.Radius;
                var candidate = request.Center + new Vector3(math.cos(angle), 0, math.sin(angle)) * distance;
                request.Attempts++;
                if (_water.TryHeight(candidate, out var height))
                {
                    candidate.y = height + Definition.WaterlineOffset;
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
                            OrbitSide = (byte)(state.Seed & 1u)
                        });
                        _byId.Add(id, entity);
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
            var radius = Definition.AvoidanceRadius * 0.8f;
            foreach (var entity in _byId.Values)
            {
                if (!_serverWorld.EntityManager.Exists(entity)) continue;
                var other = _serverWorld.EntityManager.GetComponentData<DotsEnemyShipState>(entity);
                if (math.distancesq(other.Position.xz, ((float3)position).xz) < radius * radius) return false;
            }
            var overlaps = Physics.OverlapBox(position + Definition.CollisionCenter,
                Definition.CollisionHalfExtents + Vector3.one * Definition.CollisionSkin,
                Quaternion.identity, Definition.CollisionLayers, QueryTriggerInteraction.Ignore);
            foreach (var overlap in overlaps)
                if (overlap != null && overlap.GetComponentInParent<WaterObject>() == null) return false;
            return true;
        }

        private void Simulate(Entity entity, ref DotsEnemyShipState state, ref DotsEnemyShipBrain brain)
        {
            var now = Now;
            var deltaTime = Mathf.Clamp(now - brain.LastTick, 0f, 0.1f);
            brain.LastTick = now;
            if (deltaTime <= 0f) return;
            if (state.Health <= 0)
            {
                state.Position.y -= Definition.SinkSpeed * deltaTime;
                state.Rotation = math.mul(state.Rotation,
                    quaternion.RotateZ(math.radians(4f * deltaTime)));
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
            if (brain.Target < 0) targetSpeed *= 0.45f;
            var rate = targetSpeed > brain.Speed ? Definition.Acceleration : Definition.Deceleration;
            brain.Speed = Mathf.MoveTowards(brain.Speed, targetSpeed, rate * deltaTime);
            var velocity = forward * brain.Speed + wind * Definition.WindDriftSpeed;
            var displacement = velocity * deltaTime;
            displacement = ResolveCollision(state.Id, state.Position, state.Rotation, displacement);
            var next = (Vector3)state.Position + displacement;

            var yaw = Quaternion.Euler(0f, brain.Heading * Mathf.Rad2Deg, 0f);
            var targetRotation = yaw;
            if (_water.TrySurface(next, Definition.WaterSampleSize, out var waterHeight, out var normal))
            {
                next.y = Mathf.Lerp(state.Position.y, waterHeight + Definition.WaterlineOffset,
                    1f - Mathf.Exp(-Definition.WaterHeightResponse * deltaTime));
                targetRotation = Quaternion.FromToRotation(Vector3.up, normal) * yaw;
            }
            state.Position = next;
            state.Rotation = math.slerp(state.Rotation, targetRotation,
                1f - math.exp(-Definition.WaterTiltResponse * deltaTime));
            TryFire(ref state, ref brain, now);
        }

        private Vector3 ResolveCollision(int id, Vector3 position, Quaternion rotation, Vector3 displacement)
        {
            var distance = displacement.magnitude;
            if (distance < 0.0001f) return displacement;
            var center = position + rotation * Definition.CollisionCenter;
            var hits = Physics.BoxCastAll(center, Definition.CollisionHalfExtents, displacement / distance,
                rotation, distance + Definition.CollisionSkin, Definition.CollisionLayers, QueryTriggerInteraction.Ignore);
            var nearest = float.PositiveInfinity;
            foreach (var hit in hits)
            {
                if (hit.collider == null || hit.collider.GetComponentInParent<WaterObject>() != null) continue;
                var view = hit.collider.GetComponentInParent<EnemyShipView>();
                if (view != null && view.ShipId == id) continue;
                if (hit.distance < nearest) nearest = hit.distance;
            }
            if (float.IsPositiveInfinity(nearest)) return displacement;
            var accepted = Mathf.Max(0f, nearest - Definition.CollisionSkin);
            return displacement.normalized * Mathf.Min(distance, accepted);
        }

        private void TryFire(ref DotsEnemyShipState state, ref DotsEnemyShipBrain brain, float now)
        {
            if (brain.Target < 0 || brain.Target >= _targets.Count || now < brain.NextFire ||
                brain.TargetDistance > Definition.FireRange || _projectiles.Count >= 128) return;
            var target = _targets[brain.Target];
            if (target.Ship == null || target.Battery == null || target.Battery.Health <= 0) return;
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
            if (!HasLineOfFire(state.Id, origin, aim, target.Battery)) return;
            if (!TryBallisticVelocity(origin, aim, Definition.ProjectileSpeed, Definition.ProjectileGravity,
                    out var velocity))
                velocity = (aim - origin).normalized * Definition.ProjectileSpeed;
            state.ShotRevision++;
            state.ShotOrigin = origin;
            state.ShotVelocity = velocity;
            state.ShotStarted = now;
            _projectiles.Add(new Projectile { ShipId = state.Id, Revision = state.ShotRevision,
                Origin = origin, Previous = origin, Velocity = velocity, Started = now });
            var random = new Random(math.hash(new uint3(state.Seed, state.ShotRevision, (uint)state.Id)) | 1u);
            brain.NextFire = now + Definition.FireCooldown + random.NextFloat(0, Definition.FireCooldownJitter);
        }

        private bool HasLineOfFire(int shipId, Vector3 origin, Vector3 target, ShipCannonBattery intendedTarget)
        {
            var delta = target - origin;
            var distance = delta.magnitude;
            if (distance < 0.001f) return false;
            var hits = Physics.RaycastAll(origin, delta / distance, distance, Definition.CollisionLayers,
                QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (hit.collider == null || hit.collider.GetComponentInParent<WaterObject>() != null) continue;
                var view = hit.collider.GetComponentInParent<EnemyShipView>();
                if (view != null && view.ShipId == shipId) continue;
                var battery = hit.collider.GetComponentInParent<ShipCannonBattery>();
                return battery == intendedTarget;
            }
            return true;
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

        private void SimulateProjectiles()
        {
            var now = Now;
            var lastSample = Mathf.CeilToInt(Definition.ProjectileLifetime / ProjectileStep);
            for (var i = _projectiles.Count - 1; i >= 0; i--)
            {
                var projectile = _projectiles[i];
                var targetSample = Mathf.Min(lastSample,
                    Mathf.FloorToInt((now - projectile.Started) / ProjectileStep));
                var removed = false;
                for (var step = 0; step < 12 && projectile.Sample < targetSample; step++)
                {
                    projectile.Sample++;
                    var age = projectile.Sample * ProjectileStep;
                    var position = projectile.Origin + projectile.Velocity * age +
                                   Vector3.down * (0.5f * Definition.ProjectileGravity * age * age);
                    if (SampleProjectile(projectile, position, age))
                    {
                        _projectiles.RemoveAt(i);
                        removed = true;
                        break;
                    }
                    projectile.Previous = position;
                }
                if (removed) continue;
                if (projectile.Sample >= lastSample)
                {
                    SetImpact(projectile, projectile.Previous, Vector3.up, false, false,
                        projectile.Started + Definition.ProjectileLifetime);
                    _projectiles.RemoveAt(i);
                }
                else _projectiles[i] = projectile;
            }
        }

        private bool SampleProjectile(Projectile projectile, Vector3 position, float age)
        {
            var delta = position - projectile.Previous;
            var distance = delta.magnitude;
            var nearest = float.PositiveInfinity;
            var point = position;
            var normal = Vector3.up;
            ShipCannonBattery battery = null;
            var solid = false;
            if (distance > 0.0001f)
            {
                var hits = Physics.SphereCastAll(projectile.Previous, Definition.ProjectileRadius,
                    delta / distance, distance, Definition.CollisionLayers, QueryTriggerInteraction.Ignore);
                foreach (var hit in hits)
                {
                    if (hit.collider == null || hit.distance >= nearest ||
                        hit.collider.GetComponentInParent<WaterObject>() != null) continue;
                    var enemy = hit.collider.GetComponentInParent<EnemyShipView>();
                    if (enemy != null) continue;
                    nearest = hit.distance;
                    point = hit.point;
                    normal = hit.normal;
                    battery = hit.collider.GetComponentInParent<ShipCannonBattery>();
                    solid = true;
                }
            }
            var water = _water.Crossing(projectile.Previous, position, out var waterPoint, out var waterFraction) &&
                        waterFraction * distance <= nearest;
            if (!solid && !water) return false;
            if (water) point = waterPoint;
            else battery?.ApplyDamageServer(Definition.ProjectileDamage);
            var fraction = water ? waterFraction : Mathf.Clamp01(nearest / Mathf.Max(0.0001f, distance));
            SetImpact(projectile, point, water ? Vector3.up : normal, water, true,
                projectile.Started + (age - ProjectileStep) + ProjectileStep * fraction);
            return true;
        }

        private void SetImpact(Projectile projectile, Vector3 point, Vector3 normal, bool water, bool show, float at)
        {
            if (!_byId.TryGetValue(projectile.ShipId, out var entity) ||
                !_serverWorld.EntityManager.Exists(entity)) return;
            var state = _serverWorld.EntityManager.GetComponentData<DotsEnemyShipState>(entity);
            state.ImpactRevision++;
            state.ImpactShotRevision = projectile.Revision;
            state.ImpactPoint = point;
            state.ImpactNormal = normal;
            state.ImpactAt = at;
            state.ImpactFlags = (byte)((water ? 1 : 0) | (show ? 2 : 0));
            _serverWorld.EntityManager.SetComponentData(entity, state);
        }

        public bool Damage(int id, float damage, Vector3 source)
        {
            if (!CanSimulate || !float.IsFinite(damage) || damage <= 0 || !AttachServer() ||
                !_byId.TryGetValue(id, out var entity) || !_serverWorld.EntityManager.Exists(entity)) return false;
            var manager = _serverWorld.EntityManager;
            var state = manager.GetComponentData<DotsEnemyShipState>(entity);
            if (state.Health <= 0) return false;
            state.Health = Mathf.Max(0, state.Health - damage);
            state.HitRevision++;
            if (state.Health <= 0)
            {
                state.DeathAt = Now;
                DotsEnemyRuntime.Instance?.DespawnGroup(DotsEnemyRuntime.CrewGroupForShip(id));
            }
            manager.SetComponentData(entity, state);
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

        internal void EnsureCrew(int id, EnemyShipView view)
        {
            if (!CanSimulate || view == null || !_byId.TryGetValue(id, out var entity) ||
                !_serverWorld.EntityManager.Exists(entity)) return;
            var manager = _serverWorld.EntityManager;
            var brain = manager.GetComponentData<DotsEnemyShipBrain>(entity);
            if (brain.CrewSpawned != 0) return;
            var skeletons = DotsEnemyRuntime.EnsureInstance();
            if (skeletons == null || !skeletons.SpawnCrewOnSurface(id, view.transform,
                    view.GetCrewLocalPositions(Definition.CrewCount), Definition.CrewCombatType,
                    manager.GetComponentData<DotsEnemyShipState>(entity).Seed)) return;
            brain.CrewSpawned = 1;
            manager.SetComponentData(entity, brain);
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
            _views.Clear();
            _spawns.Clear();
            _projectiles.Clear();
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
