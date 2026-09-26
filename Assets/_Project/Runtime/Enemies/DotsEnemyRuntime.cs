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
using WaveByWave.Combat;
using WaveByWave.Generation;
using WaveByWave.Items;
using WaveByWave.Player;
using WaveByWave.Ships;
using Object = UnityEngine.Object;
using Random = Unity.Mathematics.Random;

namespace WaveByWave.Enemies
{
    [DefaultExecutionOrder(9850)]
    public sealed partial class DotsEnemyRuntime : MonoBehaviour
    {
        public static DotsEnemyRuntime Instance { get; private set; }
        public DotsEnemyCatalog Catalog { get; private set; }
        private readonly DotsEnemyCatalog[] _species = new DotsEnemyCatalog[4];
        public DotsEnemyCatalog GetCatalog(EnemyKind kind) => kind == EnemyKind.Skeleton ? Catalog :
            (uint)kind < _species.Length ? _species[(int)kind] : null;
        public bool CanSimulate => Catalog != null && NetworkManager.Singleton != null &&
                                   NetworkManager.Singleton.IsServer;
        public const int MaximumStressCount = 3000;
        private sealed class SpawnRequest
        {
            public EnemySpawnPoint Point;
            public NetworkPlayerController FollowPlayer;
            public Vector3 Center;
            public float Radius, MinimumRadius;
            public int Remaining, Group, Attempts;
            public EnemyCombatType Type;
            public EnemyKind Kind;
            public EnemyHealthBarMode HealthBar;
            public Random Random;
            public bool AutomaticShark;
            public bool UseFixedCenter;
        }
        private struct PlayerTarget
        {
            public NetworkPlayerController Player;
            public PlayerEquipment Equipment;
            public NetworkHealth Health;
            public ulong ClientId;
        }
        private struct Probe
        {
            public Entity Entity;
            public float DeltaTime;
            public bool WantsToMove;
            public RaycastHit Ground;
            public int ContinuityStart, ContinuityCount;
        }
        private readonly List<EnemySpawnPoint> _points = new();
        private readonly HashSet<EnemySpawnPoint> _activated = new();
        private readonly List<SpawnRequest> _spawns = new();
        private readonly List<PlayerTarget> _players = new();
        private readonly List<ulong> _oceanPlayerIds = new();
        private readonly List<byte> _oceanPlayerStates = new();
        private readonly HashSet<ulong> _playersInOcean = new();
        private readonly HashSet<ulong> _playersInOceanNow = new();
        private readonly Dictionary<int, Entity> _byId = new();
        // Wave membership is independent from SpawnGroup: boarding crew may leave
        // their original ship group, but still blocks the next wave fragment.
        private readonly Dictionary<int, int> _waveGroupByEnemy = new();
        private readonly Dictionary<int2, List<ProjectileTarget>> _projectileGrid = new();
        private readonly Stack<List<ProjectileTarget>> _projectileGridPool = new();
        private int _projectileGridFrame = -1;
        private const float ProjectileCellSize = 4f;

        private struct ProjectileTarget
        {
            public int Id;
            public Vector3 Bottom, Top;
            public float Radius;
        }
        private float _projectileGridPadding;
        private readonly Dictionary<ulong, Transform> _surfaces = new();
        private readonly HashSet<int> _crewGroups = new();
        private readonly Dictionary<int, int> _livingCrew = new();
        private readonly List<Entity> _remove = new();
        private readonly List<Probe> _probes = new();
        private readonly RaycastHit[] _hits = new RaycastHit[48];
        private World _serverWorld;
        private EntityQuery _enemies;
        private EquipmentWaterQuery _water;
        private int _scene, _nextId = 1, _nextGroup = 1, _surfaceCursor, _lastFrame = -1;
        private readonly HashSet<int> _automaticSharkIds = new();
        private readonly List<int> _deadAutomaticSharks = new();
        private int _automaticSharkWaveSize = 1, _automaticSharkWaveSpawned;
        private float _nextAutomaticSharkCheck, _automaticSharkRetryAt;
        private bool _automaticSharkPending;
        private const int AutomaticSharkGroup = -1000;
        internal const ulong NoOceanEntrant = ulong.MaxValue;
        private float _nextPlayerRefresh, _nextSurfaceRefresh;
        private bool _wasServer;
        private NativeArray<RaycastCommand> _groundCommands;
        private NativeArray<RaycastHit> _groundResults;
        private NativeArray<RaycastCommand> _continuityCommands;
        private NativeArray<RaycastHit> _continuityResults;
        private const int GroundHitsPerProbe = 8;
        private int _probeCapacity, _continuityCapacity;
        public int StressCount { get; private set; }
        public int AliveCount => _byId.Count;
        public float Now => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening
            ? (float)NetworkManager.Singleton.ServerTime.Time : Time.time;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap() => EnsureInstance();
        public static DotsEnemyRuntime EnsureInstance()
        {
            if (Instance != null) return Instance;
            var root = new GameObject("DOTS Enemy Runtime");
            return root.AddComponent<DotsEnemyRuntime>();
        }
        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Catalog = Resources.Load<DotsEnemyCatalog>(DotsEnemyCatalog.SkeletonResourcePath);
            _species[1] = Resources.Load<DotsEnemyCatalog>("Enemies/TrollEnemyCatalog");
            _species[2] = Resources.Load<DotsEnemyCatalog>("Enemies/SharkEnemyCatalog");
            _species[3] = Resources.Load<DotsEnemyCatalog>("Enemies/AmphibianEnemyCatalog");
            _water = new EquipmentWaterQuery(Catalog != null ? Catalog.WaterProfile : null);
            _scene = SceneKey(SceneManager.GetActiveScene().name);
        }
        public static int SceneKey(string value)
        {
            unchecked { uint hash = 2166136261; foreach (var c in value) hash = (hash ^ c) * 16777619; return (int)hash; }
        }
        public void Register(EnemySpawnPoint point)
        {
            if (!_points.Contains(point)) _points.Add(point);
        }
        public void Unregister(EnemySpawnPoint point)
        {
            _points.Remove(point);
            _activated.Remove(point);
            _spawns.RemoveAll(r => r.Point == point);
            // Streamed-out islands must not leave their population suspended over the ocean.
            if (point.IsIslandPoint && point.SpawnGroup != 0 && _serverWorld != null && _serverWorld.IsCreated)
            {
                var manager = _serverWorld.EntityManager;
                using var entities = _enemies.ToEntityArray(Allocator.Temp);
                foreach (var entity in entities)
                {
                    if (manager.GetComponentData<DotsEnemyBrain>(entity).SpawnGroup != point.SpawnGroup) continue;
                    var id = manager.GetComponentData<DotsEnemyState>(entity).Id;
                    _byId.Remove(id);
                    _waveGroupByEnemy.Remove(id);
                    _surfaceTransfers.Remove(id);
                    RemoveMovementCrowdBody(id);
                    manager.DestroyEntity(entity);
                }
            }
            point.SpawnGroup = 0;
        }
        private void Update()
        {
            var scene = SceneKey(SceneManager.GetActiveScene().name);
            var server = CanSimulate;
            if (_scene != scene || _wasServer && !server)
            {
                ClearServer();
                _activated.Clear();
                _surfaces.Clear();
                _scene = scene;
                DotsEnemyPresentation.Clear();
            }
            _wasServer = server;
            if (Catalog == null) Catalog = Resources.Load<DotsEnemyCatalog>(DotsEnemyCatalog.SkeletonResourcePath);
        }
        private void LateUpdate() => DotsEnemyPresentation.Update(this);

        private bool AttachServer()
        {
            var world = ClientServerBootstrap.ServerWorld;
            if (world == null || !world.IsCreated) return false;
            if (_serverWorld != world)
            {
                ClearMovementCrowdIndex();
                _serverWorld = world;
                _enemies = world.EntityManager.CreateEntityQuery(typeof(DotsEnemyState), typeof(DotsEnemyBrain));
                _byId.Clear();
                _waveGroupByEnemy.Clear();
                _crewGroups.Clear();
                _livingCrew.Clear();
            }
            return true;
        }
        public void TickServer(EnemyServerSystem system)
        {
            // NFE can catch up multiple ticks during one rendered frame. The PhysX bridge must
            // never repeat the same expensive surface batch for those catch-up ticks.
            if (_lastFrame == Time.frameCount || !AttachServer()) return;
            _lastFrame = Time.frameCount;
            if (_scene != SceneKey(SceneManager.GetActiveScene().name)) return;
            RefreshPlayers();
            var now = Now;
            var targets = new NativeArray<EnemyTarget>(_players.Count, Allocator.TempJob);
            using var targetLifetime = targets;
            for (var i = 0; i < _players.Count; i++)
            {
                var feet = Feet(_players[i].Player);
                targets[i] = new EnemyTarget { Position = feet, Index = i,
                    InWater = IsInOcean(feet)
                        ? (byte)1 : (byte)0 };
            }
            QueueAutomaticShark(targets);
            ActivatePoints(targets);
            SpawnBatch();
            var manager = _serverWorld.EntityManager;
            using var entities = _enemies.ToEntityArray(Allocator.Temp);
            system.CarryPassengers(this, _scene);
            system.Seek(targets, now, Catalog.CrowdSeparationRadius, Catalog.BodyHeight * 0.75f,
                Catalog.EnableCrowdAvoidance ? Mathf.Clamp(Catalog.CrowdAvoidanceLookAhead, 0.3f, 4f) : 0,
                Catalog.BodyRadius, Catalog.MoveSpeed, Catalog.MeleeRange * 0.82f,
                Catalog.EnableCrowdSeparation, Catalog.EnableTargetSlots,
                Catalog.TargetSlotSpacing, Catalog.MaximumEnemies);
            system.FaceTargets(now, Mathf.Min(Time.unscaledDeltaTime, 0.2f), _scene);
            StressCount = 0;
            foreach (var entity in entities)
            {
                var state = manager.GetComponentData<DotsEnemyState>(entity);
                var brain = manager.GetComponentData<DotsEnemyBrain>(entity);
                if (state.Scene != _scene) { _remove.Add(entity); continue; }
                if (state.Health <= 0)
                {
                    _surfaceTransfers.Remove(state.Id);
                    RemoveMovementCrowdBody(state.Id);
                    if (now > state.DeathAt + 1f) _remove.Add(entity);
                    continue;
                }
                if (brain.SpawnGroup == -1) StressCount++;
                TickCombat(ref state, ref brain, now);
                manager.SetComponentData(entity, state);
                manager.SetComponentData(entity, brain);
            }
            MoveBatch(entities, now);
            foreach (var entity in _remove)
                if (manager.Exists(entity))
                {
                    var id = manager.GetComponentData<DotsEnemyState>(entity).Id;
                    _byId.Remove(id);
                    _waveGroupByEnemy.Remove(id);
                    _surfaceTransfers.Remove(id);
                    RemoveMovementCrowdBody(id);
                    manager.DestroyEntity(entity);
                }
            _remove.Clear();
        }
        private void RefreshPlayers()
        {
            if (Time.unscaledTime < _nextPlayerRefresh) return;
            _nextPlayerRefresh = Time.unscaledTime + 0.2f;
            _players.Clear();
            foreach (var client in NetworkManager.Singleton.ConnectedClientsList)
            {
                var obj = client.PlayerObject;
                if (obj == null || !obj.TryGetComponent<NetworkPlayerController>(out var player)) continue;
                var health = obj.GetComponent<NetworkHealth>();
                if (health == null || health.IsDead) continue;
                _players.Add(new PlayerTarget { Player = player, Health = health,
                    Equipment = obj.GetComponent<PlayerEquipment>(), ClientId = client.ClientId });
            }
        }

        private bool IsInOcean(Vector3 feet)
        {
            if (ShipFlooding.CompartmentAt(feet) != null) return false;
            return _water.TrySurface(feet, Vector2.one * 0.25f, out var height, out _) &&
                   feet.y < height - 0.15f;
        }

        private void QueueAutomaticShark(NativeArray<EnemyTarget> targets)
        {
            if (Now < _nextAutomaticSharkCheck) return;
            _nextAutomaticSharkCheck = Now + 0.25f;
            var shark = GetCatalog(EnemyKind.Shark);
            if (shark == null) return;
            RefreshAutomaticSharkState(shark);
            _oceanPlayerIds.Clear();
            _oceanPlayerStates.Clear();
            for (var i = 0; i < _players.Count; i++)
            {
                _oceanPlayerIds.Add(_players[i].ClientId);
                _oceanPlayerStates.Add(targets[i].InWater);
            }
            var entrant = UpdateOceanEntries(_oceanPlayerIds, _oceanPlayerStates,
                _playersInOcean, _playersInOceanNow, false);
            if (entrant == NoOceanEntrant || !shark.SpawnWhenPlayerEntersOcean)
            {
                _spawns.RemoveAll(request => request.AutomaticShark);
                _automaticSharkPending = false;
                return;
            }
            var desired = Mathf.Clamp(_automaticSharkWaveSize, 1, Mathf.Max(1, shark.OceanEncounterMaximumSharks));
            if (_automaticSharkPending || _automaticSharkWaveSpawned >= desired ||
                Now < _automaticSharkRetryAt || !shark.IsBaked ||
                !shark.CanSpawnType(EnemyCombatType.Random) || _byId.Count >= Catalog.MaximumEnemies) return;
            for (var i = 0; i < _players.Count; i++)
            {
                if (_players[i].ClientId != entrant) continue;
                var minimum = Mathf.Max(0f, shark.OceanEncounterMinimumDistance);
                var maximum = Mathf.Max(minimum + 0.1f, shark.OceanEncounterMaximumDistance);
                _spawns.Add(new SpawnRequest
                {
                    Center = Feet(_players[i].Player), FollowPlayer = _players[i].Player,
                    MinimumRadius = minimum, Radius = maximum,
                    Remaining = desired - _automaticSharkWaveSpawned, Group = AutomaticSharkGroup, Type = EnemyCombatType.Random,
                    Kind = EnemyKind.Shark, HealthBar = EnemyHealthBarMode.Profile, AutomaticShark = true,
                    Random = new Random(unchecked((uint)(Environment.TickCount ^ (int)entrant)) | 1u)
                });
                _automaticSharkPending = true;
                return;
            }
        }

        private void RefreshAutomaticSharkState(DotsEnemyCatalog shark)
        {
            _deadAutomaticSharks.Clear();
            foreach (var id in _automaticSharkIds)
                if (!_byId.TryGetValue(id, out var entity) || !_serverWorld.EntityManager.Exists(entity) ||
                    _serverWorld.EntityManager.GetComponentData<DotsEnemyState>(entity).Health <= 0)
                    _deadAutomaticSharks.Add(id);
            foreach (var id in _deadAutomaticSharks) _automaticSharkIds.Remove(id);
            if (_automaticSharkPending || _automaticSharkIds.Count > 0 || _automaticSharkWaveSpawned == 0) return;
            _automaticSharkWaveSize = Mathf.Min(_automaticSharkWaveSize + 1,
                Mathf.Max(1, shark.OceanEncounterMaximumSharks));
            _automaticSharkWaveSpawned = 0;
            _automaticSharkRetryAt = Now + Mathf.Max(0f, shark.OceanEncounterRespawnDelay);
        }

        internal static ulong UpdateOceanEntries(IReadOnlyList<ulong> ids, IReadOnlyList<byte> inOcean,
            HashSet<ulong> previous, HashSet<ulong> current, bool encounterOccupied)
        {
            current.Clear();
            var entrant = NoOceanEntrant;
            var count = Mathf.Min(ids.Count, inOcean.Count);
            for (var i = 0; i < count; i++)
            {
                if (inOcean[i] == 0) continue;
                current.Add(ids[i]);
                // Eligibility is continuous: a failed spawn or defeated group must be able
                // to retry even when nobody left and re-entered the ocean.
                if (!encounterOccupied && entrant == NoOceanEntrant)
                    entrant = ids[i];
            }
            previous.Clear();
            foreach (var id in current) previous.Add(id);
            return entrant;
        }
        public static Vector3 Feet(NetworkPlayerController player)
        {
            if (player == null) return Vector3.zero;
            if (player.TryGetEnemyShipPositionOnServer(out var enemyPosition)) return enemyPosition;
            var ship = player.GetSupportingShipOnServer();
            if (ship != null && player.TryGetPositionOnPlatform(ship.NetworkObject, out var position))
                return WorldItem.GetPhysicsFrame(ship.NetworkObject)
                    .MultiplyPoint3x4(ship.transform.InverseTransformPoint(position));
            return player.transform.position;
        }
        private void ActivatePoints(NativeArray<EnemyTarget> targets)
        {
            foreach (var point in _points)
            {
                if (point == null || !point.isActiveAndEnabled || !point.CanActivate || _activated.Contains(point) ||
                    SceneKey(point.gameObject.scene.name) != _scene) continue;
                var activate = point.Mode == EnemySpawnMode.WhenPointAppears;
                if (!activate)
                    foreach (var target in targets)
                        if (math.distancesq(target.Position, (float3)point.transform.position) <=
                            point.ActivationRadius * point.ActivationRadius) { activate = true; break; }
                if (!activate) continue;
                _activated.Add(point);
                var seed = point.Seed != 0 ? (uint)point.Seed : (uint)Environment.TickCount;
                point.SpawnGroup = _nextGroup++;
                var random = new Random(seed | 1u);
                var settings = point.IsIslandPoint && point.OwnerIsland != null ? point.OwnerIsland.Settings : null;
                var rule = settings != null ? settings.EnemyRuleForDay(NightWaveController.Active?.CurrentDay ?? 1) : null;
                if (rule == null)
                {
                    // A configured schedule must not fall back to unrestricted enemies before its first day.
                    if (settings?.EnemySpawnDays != null && settings.EnemySpawnDays.Count > 0) continue;
                    QueueIslandPoint(point, point, point.Kind, ref random);
                    continue;
                }
                if (rule.Enemies == null) continue;
                // Reservoir selection avoids allocating a temporary species list per point.
                var choices = 0;
                var selected = EnemyKind.Skeleton;
                foreach (var entry in rule.Enemies)
                {
                    if (entry == null) continue;
                    var kind = entry.EnemyType;
                    var catalog = GetCatalog(kind);
                    if (catalog == null || !catalog.IsBaked ||
                        !catalog.CanSpawnType(EnemyCombatType.Random)) continue;
                    if (!rule.Random) QueueIslandPoint(point, IslandPointTemplate(settings, point, kind), kind, ref random);
                    else if (random.NextInt(++choices) == 0) selected = kind;
                }
                if (rule.Random && choices > 0)
                    QueueIslandPoint(point, IslandPointTemplate(settings, point, selected), selected, ref random);
            }
        }

        private static EnemySpawnPoint IslandPointTemplate(OceanGenerationSettings settings, EnemySpawnPoint fallback,
            EnemyKind kind)
        {
            if (settings.EnemySpawnPointPrefabs == null) return fallback;
            foreach (var prefab in settings.EnemySpawnPointPrefabs)
                if (prefab != null && prefab.Kind == kind) return prefab;
            return fallback;
        }

        private void QueueIslandPoint(EnemySpawnPoint point, EnemySpawnPoint template, EnemyKind kind, ref Random random)
        {
            var catalog = GetCatalog(kind);
            // Aquatic groups start outside the dry island; normal spawn probes still reject
            // shallow water, terrain and hulls. Land/amphibious groups start at the authored point.
            var water = point.IsIslandPoint && catalog != null && catalog.Habitat == EnemyHabitat.Water;
            var radius = water ? point.OwnerIsland.Diameter * 0.5f + 8f : template.SpawnRadius;
            _spawns.Add(new SpawnRequest { Point = point,
                Center = water ? point.OwnerIsland.transform.position : point.transform.position,
                UseFixedCenter = water, MinimumRadius = water ? point.OwnerIsland.Diameter * 0.5f + 2f : 0f,
                Radius = radius, Remaining = template.Count,
                Type = template.Kind == kind ? template.CombatType : EnemyCombatType.Random,
                Kind = kind, HealthBar = template.HealthBar, Group = point.SpawnGroup,
                Random = new Random(random.NextUInt() | 1u) });
        }
        public bool SetStressTarget(int count, Vector3 center, float radius)
        {
            if (!CanSimulate || !AttachServer() || !Catalog.IsBaked) return false;
            count = Mathf.Clamp(count, 0, MaximumStressCount);
            _spawns.RemoveAll(r => r.Group == -1);
            using var entities = _enemies.ToEntityArray(Allocator.Temp);
            var found = 0;
            foreach (var entity in entities)
            {
                if (_serverWorld.EntityManager.GetComponentData<DotsEnemyBrain>(entity).SpawnGroup != -1) continue;
                var state = _serverWorld.EntityManager.GetComponentData<DotsEnemyState>(entity);
                if (state.Health <= 0) continue;
                if (found++ < count) continue;
                _byId.Remove(state.Id);
                _waveGroupByEnemy.Remove(state.Id);
                _surfaceTransfers.Remove(state.Id);
                RemoveMovementCrowdBody(state.Id);
                _serverWorld.EntityManager.DestroyEntity(entity);
            }
            StressCount = Mathf.Min(found, count);
            if (count > found) _spawns.Add(new SpawnRequest
            {
                Center = center, Radius = Mathf.Clamp(radius, 2, 100), Remaining = count - found,
                FollowPlayer = NetworkManager.Singleton.LocalClient?.PlayerObject?.GetComponent<NetworkPlayerController>(),
                Group = -1, Type = EnemyCombatType.Random, Random = new Random((uint)Environment.TickCount | 1u)
            });
            return true;
        }

        public bool QueueNightWave(Vector3 center, int count, float minimumRadius, float maximumRadius,
            EnemyCombatType type, EnemyKind kind, int group)
        {
            var catalog = GetCatalog(kind);
            if (count <= 0 || !CanSimulate || !AttachServer() || Catalog == null ||
                catalog == null || !catalog.IsBaked || !catalog.CanSpawnType(type)) return false;
            minimumRadius = Mathf.Max(1f, minimumRadius);
            _spawns.Add(new SpawnRequest { Center = center, MinimumRadius = minimumRadius,
                Radius = Mathf.Max(minimumRadius + 1f, maximumRadius),
                Remaining = count, Group = group, Type = type, Kind = kind,
                Random = new Random(unchecked((uint)(group * 2654435761L + count)) | 1u) });
            return true;
        }

        public int NightWaveRemaining(int group)
        {
            if (!CanSimulate || _serverWorld == null || !_serverWorld.IsCreated) return 0;
            var count = 0;
            foreach (var request in _spawns) if (request.Group == group) count += request.Remaining;
            var manager = _serverWorld.EntityManager;
            foreach (var entity in _byId.Values)
            {
                if (!manager.Exists(entity)) continue;
                var state = manager.GetComponentData<DotsEnemyState>(entity);
                if (_waveGroupByEnemy.TryGetValue(state.Id, out var waveGroup) &&
                    waveGroup == group && state.Health > 0) count++;
            }
            return count;
        }
        private void SpawnBatch()
        {
            if (_spawns.Count == 0 || !Catalog.IsBaked) return;
            var manager = _serverWorld.EntityManager;
            using var prefabQuery = manager.CreateEntityQuery(typeof(DotsEnemyPrefab));
            if (prefabQuery.IsEmptyIgnoreFilter) return;
            var prefab = prefabQuery.GetSingleton<DotsEnemyPrefab>().Value;
            var budget = Mathf.Clamp(Catalog.SpawnsPerFrame, 1, 512);
            while (budget-- > 0 && _spawns.Count > 0 && _byId.Count < Catalog.MaximumEnemies)
            {
                var request = _spawns[0];
                // Disabled requests must not block the queue or waste ground probes.
                var catalog = GetCatalog(request.Kind);
                if (catalog == null || !catalog.IsBaked || !catalog.CanSpawnType(request.Type))
                {
                    if (request.AutomaticShark) _automaticSharkPending = false;
                    _spawns.RemoveAt(0);
                    continue;
                }
                var center = request.UseFixedCenter ? request.Center : request.Point != null ? request.Point.transform.position :
                    request.FollowPlayer != null ? Feet(request.FollowPlayer) : request.Center;
                var angle = request.Random.NextFloat(0, math.PI * 2);
                var minimumRadius = Mathf.Clamp(request.MinimumRadius, 0f, request.Radius);
                var distance = math.sqrt(math.lerp(minimumRadius * minimumRadius,
                    request.Radius * request.Radius, request.Random.NextFloat()));
                var candidate = center + new Vector3(math.cos(angle), 0, math.sin(angle)) * distance;
                request.Attempts++;
                if (TrySpawnPosition(candidate, catalog, out var spawnPosition, out var hit, out var swimming) &&
                    catalog.TrySelectSpawnType(request.Type, ref request.Random, out var type))
                {
                    var state = new DotsEnemyState
                    {
                        Id = _nextId++, Seed = request.Random.NextUInt() | 1u, Scene = _scene, CombatType = type,
                        Kind = request.Kind, HealthBar = request.HealthBar == EnemyHealthBarMode.Profile
                            ? _healthBarOverrides[(int)request.Kind] : request.HealthBar, Swimming = swimming,
                        Health = catalog.MaximumHealth, Position = spawnPosition,
                        Rotation = quaternion.RotateY(request.Random.NextFloat(0, math.PI * 2)),
                        Animation = EnemyAnimationState.Idle, AnimationStarted = Now,
                        AnimationDuration = catalog.Duration(EnemyAnimationState.Idle)
                    };
                    if (hit.collider != null) AttachSurface(ref state, hit.collider);
                    else UpdateLocal(ref state);
                    var entity = manager.Instantiate(prefab);
                    manager.SetComponentData(entity, state);
                    manager.SetComponentData(entity, new DotsEnemyBrain { Target = -1, SpawnGroup = request.Group,
                        SpeciesSpeed = swimming != 0 ? catalog.SwimSpeed : catalog.MoveSpeed,
                        SpeciesStoppingDistance = catalog.MeleeRange * 0.82f, SpeciesSpacing = catalog.BodyRadius * 2 + .04f,
                        LastSurfaceTime = Now, NextAttack = Now + request.Random.NextFloat(0.5f, 1.5f) });
                    _byId.Add(state.Id, entity);
                    if (request.Group != 0) _waveGroupByEnemy[state.Id] = request.Group;
                    if (request.AutomaticShark)
                    {
                        _automaticSharkIds.Add(state.Id);
                        _automaticSharkWaveSpawned++;
                    }
                    UpdateCrowdSnapshot(in state);
                    request.Remaining--;
                    request.Attempts = 0;
                }
                if (request.Remaining <= 0 || request.Attempts > 256)
                {
                    if (request.Remaining > 0)
                        Debug.LogWarning($"[Enemies] {catalog.DisplayName}: no suitable {catalog.Habitat} surface in this area; {request.Remaining} spawns skipped.");
                    if (request.AutomaticShark)
                    {
                        _automaticSharkPending = false;
                        if (request.Remaining > 0) _automaticSharkRetryAt = Now + 2f;
                    }
                    _spawns.RemoveAt(0);
                }
            }
        }
        private void TickCombat(ref DotsEnemyState state, ref DotsEnemyBrain brain, float now)
        {
            var catalog = GetCatalog(state.Kind) ?? Catalog;
            brain.SpeciesSpeed = state.Swimming != 0 ? catalog.SwimSpeed : catalog.MoveSpeed;
            brain.SpeciesStoppingDistance = catalog.MeleeRange * 0.82f;
            brain.SpeciesSpacing = catalog.BodyRadius * 2 + .04f;
            if (_surfaceTransfers.ContainsKey(state.Id)) return;
            if (state.StunUntil > now)
            {
                brain.Attacking = 0;
                SetAnimation(ref state, EnemyAnimationState.Stunned, state.StunUntil - now);
                return;
            }
            if (state.Animation == EnemyAnimationState.Stunned) SetAnimation(ref state, EnemyAnimationState.Idle);
            if (!catalog.EnableCombat)
            {
                if (brain.Attacking != 0)
                {
                    brain.Attacking = 0;
                    brain.StrikeAt = 0;
                    SetAnimation(ref state, EnemyAnimationState.Idle);
                }
                return;
            }
            if (brain.Attacking != 0)
            {
                if (brain.StrikeAt > 0 && now >= brain.StrikeAt)
                {
                    brain.StrikeAt = 0;
                    Strike(ref state, ref brain);
                }
                if (state.StunUntil > now) return;
                if (now < state.AnimationStarted + state.AnimationDuration) return;
                brain.Attacking = 0;
                SetAnimation(ref state, EnemyAnimationState.Idle);
            }
            if (brain.Target < 0 || brain.Target >= _players.Count || now < brain.NextAttack) return;
            var range = state.CombatType == EnemyCombatType.Melee ? catalog.MeleeRange : catalog.RangedMaximumRange;
            if (brain.TargetDistance > range || !CanSee(state, Feet(_players[brain.Target].Player), catalog)) return;
            var attackUp = TryGetSurfaceFrame(state.SupportId, true, out var attackFrame)
                ? (float3)(attackFrame.rotation * Vector3.up) : math.up();
            state.Rotation = quaternion.LookRotationSafe(
                brain.Direction - attackUp * math.dot(brain.Direction, attackUp), attackUp);
            UpdateLocal(ref state);
            SetAnimation(ref state, catalog.AttackAnimation(state.CombatType), catalog.Duration(catalog.AttackAnimation(state.CombatType)));
            brain.Attacking = 1;
            brain.StrikeAt = now + state.AnimationDuration * catalog.AttackHitTime(state.CombatType);
            brain.NextAttack = now + Mathf.Max(state.AnimationDuration, catalog.AttackCooldown(state.CombatType));
        }
        private void Strike(ref DotsEnemyState state, ref DotsEnemyBrain brain)
        {
            var catalog = GetCatalog(state.Kind) ?? Catalog;
            if (brain.Target < 0 || brain.Target >= _players.Count) return;
            var target = _players[brain.Target];
            if (target.Player == null || target.Health == null || target.Health.IsDead) return;
            var feet = Feet(target.Player);
            var range = state.CombatType == EnemyCombatType.Melee ? catalog.MeleeRange + 0.3f : catalog.RangedMaximumRange;
            if (Vector3.Distance(state.Position, feet) > range || !CanSee(state, feet, catalog)) return;
            var damage = catalog.AttackDamage(state.CombatType);
            if (state.CombatType == EnemyCombatType.Melee && target.Equipment != null &&
                target.Equipment.TryBlockHitServer(damage, state.Position))
            {
                state.StunUntil = Now + catalog.ParryStunDuration;
                brain.Knockback = math.normalizesafe(state.Position - (float3)feet) * catalog.ParryKnockback;
                brain.Attacking = 0;
                brain.NextAttack = state.StunUntil + 0.3f;
                SetAnimation(ref state, EnemyAnimationState.Stunned, catalog.ParryStunDuration);
                return;
            }
            target.Health.ApplyDamageServer(damage, state.Position);
        }
        private void SetAnimation(ref DotsEnemyState state, EnemyAnimationState animation, float duration = 0)
        {
            var catalog = GetCatalog(state.Kind) ?? Catalog;
            if (state.Animation == animation) return;
            state.Animation = animation;
            state.AnimationStarted = Now;
            state.AnimationDuration = duration > 0 ? duration : catalog.Duration(animation);
        }
        private void EnsureProbeCapacity(int capacity)
        {
            if (_probeCapacity >= capacity) return;
            DisposeProbes();
            _probeCapacity = math.ceilpow2(Math.Max(16, capacity));
            _groundCommands = new NativeArray<RaycastCommand>(_probeCapacity, Allocator.Persistent);
            _groundResults = new NativeArray<RaycastHit>(_probeCapacity * GroundHitsPerProbe, Allocator.Persistent);
        }
        private void MoveBatch(NativeArray<Entity> entities, float now)
        {
            if (entities.Length == 0) return;
            var manager = _serverWorld.EntityManager;
            _deckMaps.Clear();
            PrepareMovementCrowdIndex();
            var count = Mathf.Min(entities.Length, Catalog.SurfaceProbesPerFrame);
            EnsureProbeCapacity(count);
            _probes.Clear();
            var edgeSearchesRemaining = Mathf.Clamp(Catalog.EdgeSearchesPerFrame, 1, 128);
            var query = new QueryParameters(Catalog.SurfaceLayers, false, QueryTriggerInteraction.Ignore, false);
            for (var i = 0; i < count; i++)
            {
                var entity = entities[(_surfaceCursor + i) % entities.Length];
                var state = manager.GetComponentData<DotsEnemyState>(entity);
                if (state.Health <= 0 || state.Scene != _scene) continue;
                var brain = manager.GetComponentData<DotsEnemyBrain>(entity);
                var dt = Mathf.Clamp(now - brain.LastSurfaceTime, 0, 0.2f);
                if (dt < 0.02f) continue;
                brain.LastSurfaceTime = now;
                state.MovementUpdatedAt = now;
                if (AdvanceSurfaceTransfer(ref state, ref brain, dt, now))
                {
                    UpdateCrowdSnapshot(in state);
                    manager.SetComponentData(entity, state);
                    manager.SetComponentData(entity, brain);
                    continue;
                }
                var catalog = GetCatalog(state.Kind) ?? Catalog;
                var direction = Catalog.EnableCrowdAvoidance ? brain.CrowdDirection : brain.MoveDirection;
                var stoppingDistance = catalog.MeleeRange * 0.82f;
                if (brain.Attacking != 0 || !Catalog.EnableTargetSlots && brain.TargetDistance <= stoppingDistance)
                    direction = float3.zero;
                var separation = brain.Separation;
                var separationAmount = math.saturate(math.length(separation));
                if (separationAmount > 0.0001f)
                {
                    var away = separation / separationAmount;
                    // Pursuit can be tangent to a neighbour, but may not point into it.
                    direction += away * math.max(0, -math.dot(direction, away));
                    direction = math.normalizesafe(direction + away *
                        (math.max(0.25f, separationAmount) * Catalog.CrowdSeparationStrength));
                }
                if (state.StunUntil > now) { direction = float3.zero; separation = float3.zero; }
                var speed = state.Swimming != 0 ? catalog.SwimSpeed : catalog.MoveSpeed;
                var movement = direction * speed;
                var movementLength = math.length(movement);
                if (movementLength > speed) movement *= speed / movementLength;
                var displacement = (movement + brain.Knockback) * dt;
                var wantsToMove = math.lengthsq(movement) > 0.0001f;
                displacement = SteerCrowdStep(in state, ref brain, displacement, dt);
                brain.Knockback *= math.exp(-7f * dt);
                manager.SetComponentData(entity, brain);
                if (TryBoardPlayerShip(ref state, ref brain, displacement, dt, wantsToMove, now, catalog))
                {
                    UpdateCrowdSnapshot(in state);
                    manager.SetComponentData(entity, state);
                    manager.SetComponentData(entity, brain);
                    continue;
                }
                // Idle passengers already have an exact local pose. Re-projecting them onto
                // last frame's rendered collider introduces drift and wastes surface probes.
                if (state.SupportId != 0 && math.lengthsq(displacement) < 0.000001f)
                {
                    UpdateLocomotion(ref state, ref brain, 0, dt, true, wantsToMove, now);
                    manager.SetComponentData(entity, state);
                    manager.SetComponentData(entity, brain);
                    continue;
                }
                if (catalog.Habitat != EnemyHabitat.Land &&
                    MoveInWater(ref state, ref brain, displacement, dt, wantsToMove, now, catalog))
                {
                    UpdateCrowdSnapshot(in state);
                    manager.SetComponentData(entity, state);
                    manager.SetComponentData(entity, brain);
                    continue;
                }
                if (MoveOnBakedDeck(ref state, ref brain, displacement, dt, wantsToMove, now, ref edgeSearchesRemaining))
                {
                    manager.SetComponentData(entity, state);
                    manager.SetComponentData(entity, brain);
                    continue;
                }
                if (IsShipSurface(state.SupportId) && ResolveSurface(state.SupportId) == null) continue;
                Vector3 from = state.Position;
                var to = from + (Vector3)displacement;
                var index = _probes.Count;
                _probes.Add(new Probe { Entity = entity, DeltaTime = dt, WantsToMove = wantsToMove });
                _groundCommands[index] = new RaycastCommand(to + Vector3.up * (Catalog.StepHeight + 0.08f),
                    Vector3.down, query, Catalog.StepHeight + Catalog.MaximumDrop + 0.1f);
            }
            _surfaceCursor = NextSurfaceCursor(_surfaceCursor, count, entities.Length, Catalog.EdgeSearchesPerFrame);
            count = _probes.Count;
            if (count == 0) return;
            // Pursuit is deliberately ground-following, not a capsule character controller.
            // Props must not stop a crowd. Only a valid dry supporting surface is required;
            // personal-space pressure has already adjusted the requested destination.
            RaycastCommand.ScheduleBatch(_groundCommands.GetSubArray(0, count),
                _groundResults.GetSubArray(0, count * GroundHitsPerProbe), 32, GroundHitsPerProbe).Complete();
            // Interior ground samples used to issue individual PhysX queries on the main
            // thread. Collect them once and run the entire corridor batch on workers.
            var continuityCount = 0;
            for (var i = 0; i < count; i++)
            {
                var probe = _probes[i];
                probe.Ground = SelectBatchGround(_groundResults, i * GroundHitsPerProbe, false);
                probe.ContinuityStart = continuityCount;
                if (probe.Ground.collider != null && Catalog.EnableSurfaceContinuityChecks)
                {
                    var from = manager.GetComponentData<DotsEnemyState>(probe.Entity).Position;
                    probe.ContinuityCount = Mathf.Max(0, GroundPathSteps(from, probe.Ground.point) - 1);
                    continuityCount += probe.ContinuityCount;
                }
                _probes[i] = probe;
            }
            if (continuityCount > 0)
            {
                EnsureContinuityCapacity(continuityCount);
                foreach (var probe in _probes)
                {
                    if (probe.ContinuityCount == 0) continue;
                    Vector3 from = manager.GetComponentData<DotsEnemyState>(probe.Entity).Position;
                    for (var sample = 0; sample < probe.ContinuityCount; sample++)
                    {
                        var point = Vector3.Lerp(from, probe.Ground.point,
                            (sample + 1f) / (probe.ContinuityCount + 1f));
                        _continuityCommands[probe.ContinuityStart + sample] = new RaycastCommand(
                            point + Vector3.up * (Catalog.StepHeight + 0.08f), Vector3.down,
                            query, Catalog.StepHeight + Catalog.MaximumDrop + 0.1f);
                    }
                }
                RaycastCommand.ScheduleBatch(_continuityCommands.GetSubArray(0, continuityCount),
                    _continuityResults.GetSubArray(0, continuityCount * GroundHitsPerProbe),
                    32, GroundHitsPerProbe).Complete();
            }
            for (var i = 0; i < count; i++)
            {
                var probe = _probes[i];
                var state = manager.GetComponentData<DotsEnemyState>(probe.Entity);
                var brain = manager.GetComponentData<DotsEnemyBrain>(probe.Entity);
                state.MovementUpdatedAt = now;
                var ground = probe.Ground;
                for (var sample = 0; sample < probe.ContinuityCount; sample++)
                {
                    if (SelectBatchGround(_continuityResults,
                            (probe.ContinuityStart + sample) * GroundHitsPerProbe, true).collider != null) continue;
                    ground = default;
                    break;
                }
                var movedDistance = 0f;
                var movementEvaluated = true;
                if (ground.collider != null) brain.SurfaceEdgeSide = 0;
                if (ground.collider == null && (Catalog.EnableSurfaceTransfers || Catalog.EnableSurfaceEdgeFollowing) &&
                    brain.Target >= 0 && brain.Target < _players.Count && brain.Attacking == 0 &&
                    state.StunUntil <= now)
                {
                    if (edgeSearchesRemaining > 0)
                    {
                        edgeSearchesRemaining--;
                        var transferGoal = Feet(_players[brain.Target].Player);
                        if (SearchSurfaceTransfer(state, ref brain, transferGoal, now))
                        {
                            AdvanceSurfaceTransfer(ref state, ref brain, probe.DeltaTime, now);
                            UpdateCrowdSnapshot(in state);
                            manager.SetComponentData(probe.Entity, state);
                            manager.SetComponentData(probe.Entity, brain);
                            continue;
                        }
                        TryFollowEdge(state, ref brain, brain.MoveTarget, probe.DeltaTime, out ground);
                    }
                    else movementEvaluated = false;
                }
                if (ground.collider != null)
                {
                    if (BeginGroundStep(state, ground, probe.DeltaTime))
                    {
                        AdvanceSurfaceTransfer(ref state, ref brain, probe.DeltaTime, now);
                        UpdateCrowdSnapshot(in state);
                        manager.SetComponentData(probe.Entity, state);
                        manager.SetComponentData(probe.Entity, brain);
                        continue;
                    }
                    var previous = state.Position;
                    state.Position = new float3(ground.point.x,
                        SmoothSurfaceHeight(previous.y, ground.point.y,
                            Catalog.SurfaceVerticalSpeed, probe.DeltaTime), ground.point.z);
                    movedDistance = math.distance(previous.xz, state.Position.xz);
                    if (math.lengthsq(brain.Direction) > 0.01f && state.StunUntil <= now && brain.Attacking == 0)
                        state.Rotation = math.slerp(state.Rotation, quaternion.LookRotationSafe(
                            (float3)Vector3.ProjectOnPlane(brain.Direction, ground.normal), ground.normal),
                            1 - math.exp(-12 * probe.DeltaTime));
                    AttachSurface(ref state, ground.collider);
                    ReleaseBoardedCrew(ref brain, state);
                    UpdateCrowdSnapshot(in state);
                }
                UpdateLocomotion(ref state, ref brain, movedDistance, probe.DeltaTime,
                    movementEvaluated, probe.WantsToMove, now);
                manager.SetComponentData(probe.Entity, state);
                manager.SetComponentData(probe.Entity, brain);
            }
        }
        private void EnsureContinuityCapacity(int count)
        {
            if (_continuityCapacity >= count) return;
            if (_continuityCommands.IsCreated) _continuityCommands.Dispose();
            if (_continuityResults.IsCreated) _continuityResults.Dispose();
            _continuityCapacity = math.ceilpow2(Mathf.Max(16, count));
            _continuityCommands = new NativeArray<RaycastCommand>(_continuityCapacity, Allocator.Persistent);
            _continuityResults = new NativeArray<RaycastHit>(_continuityCapacity * GroundHitsPerProbe, Allocator.Persistent);
        }

        private RaycastHit SelectBatchGround(NativeArray<RaycastHit> results, int first, bool ignoreMinor)
        {
            var ground = default(RaycastHit);
            for (var h = 0; h < GroundHitsPerProbe; h++)
            {
                var hit = results[first + h];
                if (hit.collider == null) break;
                if (ground.collider != null && hit.distance >= ground.distance) continue;
                if (ValidSolid(hit.collider) &&
                    !(ignoreMinor ? IsMinorObstacle(hit.collider) : IsIslandDecoration(hit.collider)) &&
                    TryWalkableFace(ref hit, Catalog.StepHeight + Catalog.MaximumDrop + 0.1f, Catalog) &&
                    (ground.collider == null || hit.distance < ground.distance))
                    ground = hit;
            }
            return ground;
        }

        private static bool ValidSolid(Collider collider) => collider != null && !collider.isTrigger &&
            collider.GetComponentInParent<WaterObject>() == null &&
            collider.GetComponentInParent<NetworkPlayerController>() == null &&
            collider.GetComponentInParent<WorldItem>() == null;
        private bool IsMinorObstacle(Collider collider, DotsEnemyCatalog navigation = null)
        {
            navigation ??= Catalog;
            // Generated/baked island decorations are known explicitly. Never classify an
            // island terrain chunk by its bounds: digging can make a chunk arbitrarily small.
            if (IsIslandDecoration(collider)) return true;
            if (collider is TerrainCollider || collider.attachedRigidbody != null ||
                collider.GetComponentInParent<MovingPlatform>() != null ||
                collider.GetComponentInParent<EnemySurfaceAnchor>() != null) return false;
            var size = collider.bounds.size;
            return navigation.IgnoredObstacleWidth > 0 &&
                Mathf.Max(size.x, size.z) <= navigation.IgnoredObstacleWidth;
        }
        private static bool IsIslandDecoration(Collider collider)
        {
            var island = collider.GetComponentInParent<ProceduralIsland>();
            return island != null && island.IsDecorationCollider(collider);
        }
        private bool Walkable(RaycastHit hit, DotsEnemyCatalog navigation = null)
        {
            navigation ??= Catalog;
            if (hit.collider == null ||
                hit.normal.y < Mathf.Cos(navigation.MaximumSlope * Mathf.Deg2Rad)) return false;
            if (_water.TryWaterLevel(hit.point, out var level) && hit.point.y < level - 0.05f) return false;
            return true;
        }
        private bool TryGround(Vector3 origin, float distance, out RaycastHit result)
            => TryGround(origin, distance, Catalog, out result);

        private bool TryGround(Vector3 origin, float distance, DotsEnemyCatalog navigation,
            out RaycastHit result)
        {
            navigation ??= Catalog;
            result = default;
            var count = Physics.RaycastNonAlloc(origin, Vector3.down, _hits, distance,
                navigation.SurfaceLayers, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if ((result.collider == null || hit.distance < result.distance) &&
                    ValidSolid(hit.collider) && !IsMinorObstacle(hit.collider, navigation) &&
                    TryWalkableFace(ref hit, distance, navigation) &&
                    (result.collider == null || hit.distance < result.distance))
                    result = hit;
            }
            return result.collider != null;
        }

        private bool TryWalkableFace(ref RaycastHit hit, float rayDistance, DotsEnemyCatalog navigation)
        {
            if (Walkable(hit, navigation)) return true;
            // A horizontal face rejected only because it is underwater cannot reveal
            // dry ground farther down. Do not spend recovery probes on the seabed.
            if (hit.normal.y >= Mathf.Cos(navigation.MaximumSlope * Mathf.Deg2Rad)) return false;
            // PhysX returns only one face per MeshCollider. A steep hull/railing face
            // must not hide the deck in that very same mesh. Retry only this collider,
            // only after rejecting a face, with a fixed limit and no allocations.
            if (hit.collider is not MeshCollider mesh || mesh.convex) return false;
            for (var face = 0; face < GroundHitsPerProbe; face++)
            {
                var travelled = hit.distance + 0.002f;
                if (travelled >= rayDistance || !mesh.Raycast(
                        new Ray(hit.point + Vector3.down * 0.002f, Vector3.down),
                        out var next, rayDistance - travelled)) return false;
                next.distance += travelled;
                hit = next;
                if (Walkable(hit, navigation)) return true;
            }
            return false;
        }
        // Player melee keeps its physical occlusion rules; only enemy perception ignores props.
        public bool SolidBetween(Vector3 from, Vector3 to) => SolidBetween(from, to, false);
        private bool SolidBetween(Vector3 from, Vector3 to, bool ignoreMinorObstacles,
            Collider ignoredCollider = null, Transform ignoredRoot = null,
            DotsEnemyCatalog navigation = null)
        {
            navigation ??= Catalog;
            var delta = to - from;
            if (delta.sqrMagnitude < 0.00001f) return false;
            var count = Physics.RaycastNonAlloc(from, delta.normalized, _hits, delta.magnitude,
                navigation.SurfaceLayers, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                var collider = _hits[i].collider;
                var belongsToIgnoredRoot = ignoredRoot != null && collider != null &&
                    (collider.transform == ignoredRoot || collider.transform.IsChildOf(ignoredRoot));
                if (collider != ignoredCollider && !belongsToIgnoredRoot && ValidSolid(collider) &&
                    (!ignoreMinorObstacles || !IsMinorObstacle(collider, navigation)))
                    return true;
            }
            return false;
        }
        private void AttachSurface(ref DotsEnemyState state, Collider collider)
        {
            Transform root = null;
            var network = collider.attachedRigidbody != null
                ? collider.attachedRigidbody.GetComponentInParent<NetworkObject>() : collider.GetComponentInParent<NetworkObject>();
            if (network != null && network.IsSpawned)
            {
                state.SupportId = network.NetworkObjectId + 1;
                root = network.transform;
            }
            else
            {
                var anchor = collider.GetComponentInParent<EnemySurfaceAnchor>();
                var platform = collider.GetComponentInParent<MovingPlatform>();
                root = anchor != null ? anchor.transform : platform != null ? platform.transform :
                    collider.attachedRigidbody != null ? collider.attachedRigidbody.transform : null;
                state.SupportId = root != null ? SceneSurfaceKey(root) : 0;
            }
            if (root != null) _surfaces[state.SupportId] = root;
            UpdateLocal(ref state);
        }
        internal static ulong SceneSurfaceKey(Transform root)
        {
            if (root.TryGetComponent<EnemyShipView>(out var ship)) return ShipSurfaceKey(ship.ShipId);
            var anchor = root.GetComponent<EnemySurfaceAnchor>();
            var key = anchor != null ? anchor.Key : null;
            if (string.IsNullOrEmpty(key))
            {
                key = root.gameObject.scene.name;
                for (var node = root; node != null; node = node.parent)
                    key += "/" + node.name + ":" + node.GetSiblingIndex();
            }
            return (1UL << 63) | (uint)SceneKey(key);
        }
        public void RegisterSurface(Transform root)
        {
            if (root == null) return;
            _surfaces[SceneSurfaceKey(root)] = root;
        }

        public static ulong ShipSurfaceKey(int id) => (3UL << 62) | (uint)id;
        public static bool IsShipSurface(ulong key) => (key >> 62) == 3;

        public bool TryGetSurfaceFrame(ulong key, bool physics, out Matrix4x4 frame)
        {
            frame = Matrix4x4.identity;
            if (key == 0) return false;
            if (IsShipSurface(key))
            {
                var id = unchecked((int)(uint)key);
                return physics && DotsEnemyShipRuntime.Instance != null && DotsEnemyShipRuntime.Instance.CanSimulate
                    ? DotsEnemyShipRuntime.Instance.TryGetPhysicsFrame(id, out frame)
                    : DotsEnemyShipPresentation.TryGetFrame(id, out frame);
            }
            var root = ResolveSurface(key);
            if (root == null) return false;
            frame = physics ? PhysicsFrame(root) : root.localToWorldMatrix;
            return true;
        }

        public static int CrewGroupForShip(int shipId) => -1000000 - Mathf.Max(0, shipId);

        public bool SpawnCrewOnShip(int shipId, IReadOnlyList<Vector3> localPositions,
            EnemyCombatType combatType, uint seed, int waveGroup = 0)
        {
            if (!CanSimulate || localPositions == null || localPositions.Count == 0 ||
                !AttachServer() || Catalog == null || !Catalog.IsBaked) return false;
            var group = CrewGroupForShip(shipId);
            if (_crewGroups.Contains(group)) return true;
            if (!Catalog.CanSpawnType(combatType))
            {
                // Intentionally disabled crews are handled, not retried every ship tick.
                _crewGroups.Add(group);
                return true;
            }
            if (_byId.Count + localPositions.Count > Catalog.MaximumEnemies) return false;
            var manager = _serverWorld.EntityManager;
            using var prefabQuery = manager.CreateEntityQuery(typeof(DotsEnemyPrefab));
            if (prefabQuery.IsEmptyIgnoreFilter) return false;
            var prefab = prefabQuery.GetSingleton<DotsEnemyPrefab>().Value;
            var surface = ShipSurfaceKey(shipId);
            if (!TryGetSurfaceFrame(surface, true, out var frame)) return false;
            var random = new Random(seed | 1u);
            var now = Now;
            foreach (var localPosition in localPositions)
            {
                if (_byId.Count >= Catalog.MaximumEnemies) break;
                if (!Catalog.TrySelectSpawnType(combatType, ref random, out var type)) break;
                var localRotation = Quaternion.Euler(0f, random.NextFloat(0f, 360f), 0f);
                var state = new DotsEnemyState
                {
                    Id = _nextId++,
                    Seed = random.NextUInt() | 1u,
                    Scene = _scene,
                    CombatType = type,
                    Health = Catalog.MaximumHealth, HealthBar = _healthBarOverrides[0],
                    Position = frame.MultiplyPoint3x4(localPosition),
                    Rotation = frame.rotation * localRotation,
                    Animation = EnemyAnimationState.Idle,
                    AnimationStarted = now,
                    AnimationDuration = Catalog.Duration(EnemyAnimationState.Idle),
                    SupportId = surface,
                    LocalPosition = localPosition,
                    LocalRotation = localRotation
                };
                var entity = manager.Instantiate(prefab);
                manager.SetComponentData(entity, state);
                manager.SetComponentData(entity, new DotsEnemyBrain
                {
                    Target = -1,
                    SpawnGroup = group,
                    CrewShipId = shipId,
                    LastSurfaceTime = now,
                    NextAttack = now + random.NextFloat(0.5f, 1.5f)
                });
                _byId.Add(state.Id, entity);
                if (waveGroup != 0) _waveGroupByEnemy[state.Id] = waveGroup;
                RegisterCrewMember(shipId);
                UpdateCrowdSnapshot(in state);
            }
            _crewGroups.Add(group);
            return true;
        }

        private void RegisterCrewMember(int shipId)
        {
            _livingCrew.TryGetValue(shipId, out var alive);
            _livingCrew[shipId] = alive + 1;
        }

        private int RecordCrewDeath(ref DotsEnemyBrain brain)
        {
            var shipId = brain.CrewShipId;
            brain.CrewShipId = 0;
            if (shipId == 0 || !_livingCrew.TryGetValue(shipId, out var alive)) return 0;
            if (alive > 1)
            {
                _livingCrew[shipId] = alive - 1;
                return 0;
            }
            _livingCrew.Remove(shipId);
            return shipId;
        }

        public void DespawnGroup(int group)
        {
            _crewGroups.Remove(group);
            if (group <= -1000000) _livingCrew.Remove(-1000000 - group);
            _spawns.RemoveAll(request => request.Group == group);
            if (!AttachServer()) return;
            var manager = _serverWorld.EntityManager;
            using var entities = _enemies.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                if (!manager.Exists(entity)) continue;
                var brain = manager.GetComponentData<DotsEnemyBrain>(entity);
                var id = manager.GetComponentData<DotsEnemyState>(entity).Id;
                var belongsToWave = _waveGroupByEnemy.TryGetValue(id, out var waveGroup) && waveGroup == group;
                if (brain.SpawnGroup != group && !belongsToWave) continue;
                _byId.Remove(id);
                _waveGroupByEnemy.Remove(id);
                _surfaceTransfers.Remove(id);
                RemoveMovementCrowdBody(id);
                manager.DestroyEntity(entity);
            }
        }

        public Transform ResolveSurface(ulong key)
        {
            if (key == 0) return null;
            if (IsShipSurface(key)) return DotsEnemyShipPresentation.GetView(unchecked((int)(uint)key))?.transform;
            if (_surfaces.TryGetValue(key, out var cached) && cached != null) return cached;
            if ((key & (1UL << 63)) == 0)
            {
                var manager = NetworkManager.Singleton;
                if (manager != null && manager.SpawnManager != null &&
                    manager.SpawnManager.SpawnedObjects.TryGetValue(key - 1, out var obj))
                    return _surfaces[key] = obj.transform;
            }
            else if (Time.unscaledTime >= _nextSurfaceRefresh)
            {
                _nextSurfaceRefresh = Time.unscaledTime + 1;
                foreach (var anchor in Object.FindObjectsByType<EnemySurfaceAnchor>(FindObjectsSortMode.None))
                    _surfaces[SceneSurfaceKey(anchor.transform)] = anchor.transform;
                foreach (var body in Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
                    _surfaces[SceneSurfaceKey(body.transform)] = body.transform;
                foreach (var platform in Object.FindObjectsByType<MovingPlatform>(FindObjectsSortMode.None))
                    _surfaces[SceneSurfaceKey(platform.transform)] = platform.transform;
                _surfaces.TryGetValue(key, out cached);
                return cached;
            }
            return null;
        }
        public static Matrix4x4 PhysicsFrame(Transform root)
        {
            if (root.TryGetComponent<EnemyShipView>(out var ship)) return ship.SimulationFrame;
            var obj = root.GetComponent<NetworkObject>();
            if (obj != null) return WorldItem.GetPhysicsFrame(obj);
            if (root.TryGetComponent<MovingPlatform>(out var platform) && platform.UsesKccMover)
                return Matrix4x4.TRS(platform.KccMover.TransientPosition, platform.KccMover.TransientRotation, root.lossyScale);
            if (root.TryGetComponent<Rigidbody>(out var body))
                return Matrix4x4.TRS(body.position, body.rotation, root.lossyScale);
            return root.localToWorldMatrix;
        }
        private void UpdateLocal(ref DotsEnemyState state)
        {
            if (!TryGetSurfaceFrame(state.SupportId, true, out var frame))
            { state.LocalPosition = state.Position; state.LocalRotation = state.Rotation; return; }
            state.LocalPosition = frame.inverse.MultiplyPoint3x4(state.Position);
            state.LocalRotation = Quaternion.Inverse(frame.rotation) * (Quaternion)state.Rotation;
        }
        private void Carry(ref DotsEnemyState state)
        {
            if (!TryGetSurfaceFrame(state.SupportId, true, out var frame)) return;
            state.Position = frame.MultiplyPoint3x4(state.LocalPosition);
            state.Rotation = frame.rotation * (Quaternion)state.LocalRotation;
        }
        public bool Damage(int id, float damage, Vector3 attacker, Vector3? impactPoint = null)
        {
            if (!CanSimulate || !float.IsFinite(damage) || damage <= 0 || !AttachServer() ||
                !_byId.TryGetValue(id, out var entity) || !_serverWorld.EntityManager.Exists(entity)) return false;
            var manager = _serverWorld.EntityManager;
            var state = manager.GetComponentData<DotsEnemyState>(entity);
            if (state.Health <= 0) return false;
            var catalog = GetCatalog(state.Kind) ?? Catalog;
            Carry(ref state);
            var brain = manager.GetComponentData<DotsEnemyBrain>(entity);
            WaveByWave.Combat.DotsDamagePopups.ReportServer(impactPoint ?? (Vector3)state.Position + Vector3.up * catalog.BodyHeight * .5f, damage);
            state.Health = Mathf.Max(0, state.Health - damage);
            state.HitRevision++;
            brain.Knockback = math.normalizesafe(state.Position - (float3)attacker) * catalog.DamageKnockback;
            var defeatedCrewShip = 0;
            if (state.Health <= 0)
            {
                state.DeathAt = Now;
                brain.Attacking = 0;
                defeatedCrewShip = RecordCrewDeath(ref brain);
                if (catalog.LootDrops != null && catalog.LootDrops.Length > 0)
                {
                    var random = new Random(state.Seed | 1u);
                    var drop = catalog.LootDrops[random.NextInt(0, catalog.LootDrops.Length)];
                    if (drop != null)
                    {
                        var support = ResolveSurface(state.SupportId);
                        LootStressTest.SpawnWorldItemServer(drop, state.Position, Vector3.up * 0.4f +
                            new Vector3(random.NextFloat(-1, 1), 0, random.NextFloat(-1, 1)),
                            support != null ? support.GetComponent<NetworkObject>() : null);
                    }
                }
            }
            manager.SetComponentData(entity, state);
            manager.SetComponentData(entity, brain);
            // Publish the death before notifying the ship; sinking must not invalidate
            // an enemy entity while its damage result is still being written.
            if (defeatedCrewShip != 0) DotsEnemyShipRuntime.Instance?.SinkAfterCrewDefeated(defeatedCrewShip);
            return true;
        }
        public void Melee(Vector3 origin, Vector3 direction, float range, float damage)
        {
            if (!CanSimulate || !AttachServer()) return;
            using var states = _enemies.ToComponentDataArray<DotsEnemyState>(Allocator.Temp);
            foreach (var snapshot in states)
            {
                if (snapshot.Health <= 0 || snapshot.Scene != _scene) continue;
                var state = snapshot;
                Carry(ref state);
                var catalog = GetCatalog(state.Kind) ?? Catalog;
                HitCapsule(state, catalog, out var bottom, out var top, out var hitRadius);
                var center = Vector3.Lerp(bottom, top, 0.5f);
                var toward = center - origin;
                var axis = top - bottom;
                var closest = Vector3.Lerp(bottom, top,
                    Mathf.Clamp01(Vector3.Dot(origin - bottom, axis) / Mathf.Max(0.0001f, axis.sqrMagnitude)));
                if (Vector3.Distance(origin, closest) > range + hitRadius ||
                    Vector3.Dot(toward.normalized, direction) < 0.35f || SolidBetween(origin, center)) continue;
                Damage(state.Id, damage, origin);
            }
        }
        public bool RayHit(Vector3 from, Vector3 to, float radius, out int id, out float fraction)
        {
            id = 0; fraction = 1;
            if (!CanSimulate || !AttachServer()) return false;
            PrepareProjectileGrid();
            var clearance = _projectileGridPadding + radius;
            var min = (int2)math.floor((new float2(math.min(from.x, to.x), math.min(from.z, to.z)) - clearance) /
                ProjectileCellSize);
            var max = (int2)math.floor((new float2(math.max(from.x, to.x), math.max(from.z, to.z)) + clearance) /
                ProjectileCellSize);
            for (var z = min.y; z <= max.y; z++)
            for (var x = min.x; x <= max.x; x++)
            {
                if (!_projectileGrid.TryGetValue(new int2(x, z), out var targets)) continue;
                foreach (var target in targets)
                {
                    if (!_byId.TryGetValue(target.Id, out var entity) ||
                        !_serverWorld.EntityManager.Exists(entity) ||
                        _serverWorld.EntityManager.GetComponentData<DotsEnemyState>(entity).Health <= 0) continue;
                    if (EnemyHitGeometry.SegmentCapsule(from, to, target.Bottom, target.Top,
                            target.Radius + radius, out var hit) && hit < fraction)
                    { fraction = hit; id = target.Id; }
                }
            }
            return id != 0;
        }

        private void PrepareProjectileGrid()
        {
            if (_projectileGridFrame == Time.frameCount) return;
            foreach (var list in _projectileGrid.Values) { list.Clear(); _projectileGridPool.Push(list); }
            _projectileGrid.Clear();
            _projectileGridPadding = 0;
            _projectileGridFrame = Time.frameCount;
            using var states = _enemies.ToComponentDataArray<DotsEnemyState>(Allocator.Temp);
            foreach (var snapshot in states)
            {
                if (snapshot.Health <= 0 || snapshot.Scene != _scene) continue;
                var state = snapshot;
                Carry(ref state);
                var key = (int2)math.floor(state.Position.xz / ProjectileCellSize);
                if (!_projectileGrid.TryGetValue(key, out var list))
                {
                    list = _projectileGridPool.Count > 0 ? _projectileGridPool.Pop() : new List<ProjectileTarget>(8);
                    _projectileGrid.Add(key, list);
                }
                HitCapsule(state, GetCatalog(state.Kind) ?? Catalog, out var bottom, out var top, out var radius);
                _projectileGridPadding = Mathf.Max(_projectileGridPadding, radius + Mathf.Max(
                    math.distance(state.Position.xz, new float2(bottom.x, bottom.z)),
                    math.distance(state.Position.xz, new float2(top.x, top.z))));
                list.Add(new ProjectileTarget { Id = state.Id, Bottom = bottom, Top = top, Radius = radius });
            }
        }
        public EntityQuery ServerQuery => _enemies;
        public World ServerWorld => _serverWorld;
        private void ClearServer()
        {
            if (_serverWorld != null && _serverWorld.IsCreated) _serverWorld.EntityManager.DestroyEntity(_enemies);
            _byId.Clear(); _crewGroups.Clear(); _spawns.Clear(); _surfaceTransfers.Clear();
            _waveGroupByEnemy.Clear();
            _oceanPlayerIds.Clear(); _oceanPlayerStates.Clear();
            _playersInOcean.Clear(); _playersInOceanNow.Clear();
            _automaticSharkIds.Clear(); _deadAutomaticSharks.Clear(); _automaticSharkPending = false;
            _automaticSharkWaveSize = 1; _automaticSharkWaveSpawned = 0;
            _nextAutomaticSharkCheck = _automaticSharkRetryAt = 0f;
            _livingCrew.Clear();
            foreach (var list in _projectileGrid.Values) { list.Clear(); _projectileGridPool.Push(list); }
            _projectileGrid.Clear(); _projectileGridFrame = -1;
            ClearMovementCrowdIndex(); StressCount = 0;
        }
        private void DisposeProbes()
        {
            if (_groundCommands.IsCreated) _groundCommands.Dispose();
            if (_groundResults.IsCreated) _groundResults.Dispose();
            if (_continuityCommands.IsCreated) _continuityCommands.Dispose();
            if (_continuityResults.IsCreated) _continuityResults.Dispose();
            _probeCapacity = 0;
            _continuityCapacity = 0;
        }
        private void OnDestroy()
        {
            if (Instance != this) return;
            ClearServer(); DotsEnemyPresentation.Dispose(); DisposeProbes(); _water?.Dispose(); Instance = null;
        }
    }

    public static class EnemyHitGeometry
    {
        public static bool SegmentCapsule(Vector3 from, Vector3 to, Vector3 a, Vector3 b, float radius, out float fraction)
        {
            fraction = 1;
            var delta = to - from;
            var length = delta.magnitude;
            if (length < 0.000001f) return false;
            var rd = delta / length; var ba = b - a; var oa = from - a;
            var baba = Vector3.Dot(ba, ba); var bard = Vector3.Dot(ba, rd); var baoa = Vector3.Dot(ba, oa);
            var rdoa = Vector3.Dot(rd, oa); var oaoa = Vector3.Dot(oa, oa);
            var axisT = baba > 0.000001f ? Mathf.Clamp01(baoa / baba) : 0f;
            if ((from - Vector3.Lerp(a, b, axisT)).sqrMagnitude <= radius * radius) { fraction = 0; return true; }
            var aa = baba - bard * bard;
            var bb = baba * rdoa - baoa * bard;
            var cc = baba * oaoa - baoa * baoa - radius * radius * baba;
            var best = float.PositiveInfinity;
            var discriminant = bb * bb - aa * cc;
            if (Mathf.Abs(aa) > 0.000001f && discriminant >= 0)
            {
                var t = (-bb - Mathf.Sqrt(discriminant)) / aa; var y = baoa + t * bard;
                if (t >= 0 && y > 0 && y < baba) best = t;
            }
            best = Mathf.Min(best, Sphere(from, rd, a, radius), Sphere(from, rd, b, radius));
            if (best > length) return false;
            fraction = Mathf.Clamp01(best / length); return true;
        }
        private static float Sphere(Vector3 origin, Vector3 direction, Vector3 center, float radius)
        {
            var oc = origin - center; var b = Vector3.Dot(oc, direction);
            var h = b * b - oc.sqrMagnitude + radius * radius;
            if (h < 0) return float.PositiveInfinity;
            var t = -b - Mathf.Sqrt(h);
            return t >= 0 ? t : float.PositiveInfinity;
        }
    }
}
