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
using Object = UnityEngine.Object;
using Random = Unity.Mathematics.Random;

namespace WaveByWave.Enemies
{
    [DefaultExecutionOrder(9850)]
    public sealed partial class DotsEnemyRuntime : MonoBehaviour
    {
        public static DotsEnemyRuntime Instance { get; private set; }
        public DotsEnemyCatalog Catalog { get; private set; }
        public bool CanSimulate => Catalog != null && NetworkManager.Singleton != null &&
                                   NetworkManager.Singleton.IsServer;
        public const int MaximumStressCount = 3000;
        private sealed class SpawnRequest
        {
            public EnemySpawnPoint Point;
            public NetworkPlayerController FollowPlayer;
            public Vector3 Center;
            public float Radius;
            public int Remaining, Group, Attempts;
            public EnemyCombatType Type;
            public Random Random;
        }
        private struct PlayerTarget
        {
            public NetworkPlayerController Player;
            public PlayerEquipment Equipment;
            public NetworkHealth Health;
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
        private readonly Dictionary<int, Entity> _byId = new();
        private readonly Dictionary<int, int> _crowdIndices = new();
        private readonly Dictionary<ulong, Transform> _surfaces = new();
        private readonly HashSet<int> _crewGroups = new();
        private readonly List<Entity> _remove = new();
        private readonly List<Probe> _probes = new();
        private readonly RaycastHit[] _hits = new RaycastHit[48];
        private World _serverWorld;
        private EntityQuery _enemies;
        private EquipmentWaterQuery _water;
        private int _scene, _nextId = 1, _nextGroup = 1, _surfaceCursor, _lastFrame = -1;
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
            Catalog = Resources.Load<DotsEnemyCatalog>("SkeletonEnemyCatalog");
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
                    _surfaceTransfers.Remove(id);
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
            if (Catalog == null) Catalog = Resources.Load<DotsEnemyCatalog>("SkeletonEnemyCatalog");
        }
        private void LateUpdate() => DotsEnemyPresentation.Update(this);

        private bool AttachServer()
        {
            var world = ClientServerBootstrap.ServerWorld;
            if (world == null || !world.IsCreated) return false;
            if (_serverWorld != world)
            {
                _serverWorld = world;
                _enemies = world.EntityManager.CreateEntityQuery(typeof(DotsEnemyState), typeof(DotsEnemyBrain));
                _byId.Clear();
                _crewGroups.Clear();
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
                targets[i] = new EnemyTarget { Position = Feet(_players[i].Player), Index = i };
            ActivatePoints(targets);
            SpawnBatch();
            var manager = _serverWorld.EntityManager;
            using var entities = _enemies.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                var state = manager.GetComponentData<DotsEnemyState>(entity);
                if (state.Scene != _scene) { _remove.Add(entity); continue; }
                if (state.SupportId == 0 || state.Health <= 0) continue;
                Carry(ref state);
                manager.SetComponentData(entity, state);
            }
            system.Seek(targets, now, Catalog.CrowdSeparationRadius, Catalog.BodyHeight * 0.75f);
            StressCount = 0;
            foreach (var entity in entities)
            {
                var state = manager.GetComponentData<DotsEnemyState>(entity);
                var brain = manager.GetComponentData<DotsEnemyBrain>(entity);
                if (state.Scene != _scene) continue;
                if (state.Health <= 0)
                {
                    _surfaceTransfers.Remove(state.Id);
                    if (now > state.DeathAt + 1f) _remove.Add(entity);
                    continue;
                }
                if (brain.SpawnGroup == -1) StressCount++;
                FaceTarget(ref state, brain, Mathf.Min(Time.unscaledDeltaTime, 0.2f), now);
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
                    _surfaceTransfers.Remove(id);
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
                _players.Add(new PlayerTarget { Player = player, Health = health, Equipment = obj.GetComponent<PlayerEquipment>() });
            }
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
                _spawns.Add(new SpawnRequest { Point = point, Center = point.transform.position,
                    Radius = point.SpawnRadius, Remaining = point.Count, Type = point.CombatType,
                    Group = point.SpawnGroup, Random = new Random(seed | 1u) });
            }
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
                _surfaceTransfers.Remove(state.Id);
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
                if (!Catalog.CanSpawnType(request.Type))
                {
                    _spawns.RemoveAt(0);
                    continue;
                }
                var center = request.Point != null ? request.Point.transform.position :
                    request.FollowPlayer != null ? Feet(request.FollowPlayer) : request.Center;
                var angle = request.Random.NextFloat(0, math.PI * 2);
                var distance = math.sqrt(request.Random.NextFloat()) * request.Radius;
                var candidate = center + new Vector3(math.cos(angle), 0, math.sin(angle)) * distance;
                request.Attempts++;
                if (TryGround(candidate + Vector3.up * 0.6f, 100f, out var hit) &&
                    Catalog.TrySelectSpawnType(request.Type, ref request.Random, out var type))
                {
                    var state = new DotsEnemyState
                    {
                        Id = _nextId++, Seed = request.Random.NextUInt() | 1u, Scene = _scene, CombatType = type,
                        Health = Catalog.MaximumHealth, Position = hit.point,
                        Rotation = quaternion.RotateY(request.Random.NextFloat(0, math.PI * 2)),
                        Animation = EnemyAnimationState.Idle, AnimationStarted = Now,
                        AnimationDuration = Catalog.Duration(EnemyAnimationState.Idle)
                    };
                    AttachSurface(ref state, hit.collider);
                    var entity = manager.Instantiate(prefab);
                    manager.SetComponentData(entity, state);
                    manager.SetComponentData(entity, new DotsEnemyBrain { Target = -1, SpawnGroup = request.Group,
                        LastSurfaceTime = Now, NextAttack = Now + request.Random.NextFloat(0.5f, 1.5f) });
                    _byId.Add(state.Id, entity);
                    request.Remaining--;
                    request.Attempts = 0;
                }
                if (request.Remaining <= 0 || request.Attempts > 256)
                {
                    if (request.Remaining > 0)
                        Debug.LogWarning($"[Enemies] Spawn area has insufficient dry, walkable ground; {request.Remaining} spawns skipped.");
                    _spawns.RemoveAt(0);
                }
            }
        }
        private void TickCombat(ref DotsEnemyState state, ref DotsEnemyBrain brain, float now)
        {
            if (_surfaceTransfers.ContainsKey(state.Id)) return;
            if (state.StunUntil > now)
            {
                brain.Attacking = 0;
                SetAnimation(ref state, EnemyAnimationState.Stunned, state.StunUntil - now);
                return;
            }
            if (state.Animation == EnemyAnimationState.Stunned) SetAnimation(ref state, EnemyAnimationState.Idle);
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
            var range = state.CombatType == EnemyCombatType.Melee ? Catalog.MeleeRange : Catalog.RangedMaximumRange;
            if (brain.TargetDistance > range || !CanSee(state.Position, Feet(_players[brain.Target].Player))) return;
            var attackUp = TryGetSurfaceFrame(state.SupportId, true, out var attackFrame)
                ? (float3)(attackFrame.rotation * Vector3.up) : math.up();
            state.Rotation = quaternion.LookRotationSafe(
                brain.Direction - attackUp * math.dot(brain.Direction, attackUp), attackUp);
            UpdateLocal(ref state);
            SetAnimation(ref state, Catalog.AttackAnimation(state.CombatType), Catalog.Duration(Catalog.AttackAnimation(state.CombatType)));
            brain.Attacking = 1;
            brain.StrikeAt = now + state.AnimationDuration * Catalog.AttackHitTime(state.CombatType);
            brain.NextAttack = now + Mathf.Max(state.AnimationDuration, Catalog.AttackCooldown(state.CombatType));
        }
        private void Strike(ref DotsEnemyState state, ref DotsEnemyBrain brain)
        {
            if (brain.Target < 0 || brain.Target >= _players.Count) return;
            var target = _players[brain.Target];
            if (target.Player == null || target.Health == null || target.Health.IsDead) return;
            var feet = Feet(target.Player);
            var range = state.CombatType == EnemyCombatType.Melee ? Catalog.MeleeRange + 0.3f : Catalog.RangedMaximumRange;
            if (Vector3.Distance(state.Position, feet) > range || !CanSee(state.Position, feet)) return;
            var damage = Catalog.AttackDamage(state.CombatType);
            if (state.CombatType == EnemyCombatType.Melee && target.Equipment != null &&
                target.Equipment.TryBlockHitServer(damage, state.Position))
            {
                state.StunUntil = Now + Catalog.ParryStunDuration;
                brain.Knockback = math.normalizesafe(state.Position - (float3)feet) * Catalog.ParryKnockback;
                brain.Attacking = 0;
                brain.NextAttack = state.StunUntil + 0.3f;
                SetAnimation(ref state, EnemyAnimationState.Stunned, Catalog.ParryStunDuration);
                return;
            }
            target.Health.ApplyDamageServer(damage, state.Position);
        }
        private void SetAnimation(ref DotsEnemyState state, EnemyAnimationState animation, float duration = 0)
        {
            if (state.Animation == animation) return;
            state.Animation = animation;
            state.AnimationStarted = Now;
            state.AnimationDuration = duration > 0 ? duration : Catalog.Duration(animation);
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
            using var crowdBodies = _enemies.ToComponentDataArray<DotsEnemyState>(Allocator.Temp);
            _crowdIndices.Clear();
            for (var body = 0; body < crowdBodies.Length; body++) _crowdIndices[crowdBodies[body].Id] = body;
            BuildMovementCrowdIndex(crowdBodies, Catalog.CrowdSeparationRadius, Catalog.BodyRadius);
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
                    UpdateCrowdSnapshot(crowdBodies, state);
                    manager.SetComponentData(entity, state);
                    manager.SetComponentData(entity, brain);
                    continue;
                }
                var direction = brain.Direction;
                var stoppingDistance = Catalog.MeleeRange * 0.82f;
                if (brain.Attacking != 0 || brain.TargetDistance <= stoppingDistance)
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
                var movement = direction * Catalog.MoveSpeed;
                var movementLength = math.length(movement);
                if (movementLength > Catalog.MoveSpeed) movement *= Catalog.MoveSpeed / movementLength;
                var displacement = (movement + brain.Knockback) * dt;
                var wantsToMove = math.lengthsq(movement) > 0.0001f;
                brain.Knockback *= math.exp(-7f * dt);
                manager.SetComponentData(entity, brain);
                // Idle passengers already have an exact local pose. Re-projecting them onto
                // last frame's rendered collider introduces drift and wastes surface probes.
                if (state.SupportId != 0 && math.lengthsq(displacement) < 0.000001f)
                {
                    UpdateLocomotion(ref state, ref brain, 0, dt, true, wantsToMove, now);
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
            // enemies themselves have no physics colliders or pairwise separation forces.
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
                if (probe.Ground.collider != null)
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
                if (ground.collider == null && brain.Target >= 0 && brain.Target < _players.Count && brain.Attacking == 0 &&
                    state.StunUntil <= now)
                {
                    if (edgeSearchesRemaining > 0)
                    {
                        edgeSearchesRemaining--;
                        var goal = Feet(_players[brain.Target].Player);
                        if (TryBeginSurfaceTransfer(state, goal))
                        {
                            AdvanceSurfaceTransfer(ref state, ref brain, probe.DeltaTime, now);
                            UpdateCrowdSnapshot(crowdBodies, state);
                            manager.SetComponentData(probe.Entity, state);
                            manager.SetComponentData(probe.Entity, brain);
                            continue;
                        }
                        TryFollowEdge(state, goal, probe.DeltaTime, out ground);
                    }
                    else movementEvaluated = false;
                }
                if (ground.collider != null)
                {
                    var previous = state.Position;
                    var candidate = state;
                    candidate.Position = ground.point;
                    AttachSurface(ref candidate, ground.collider);
                    var accepted = LimitCrowdStep(state.Id, candidate.SupportId, previous,
                        ground.point, crowdBodies, Catalog.CrowdSeparationRadius,
                        Catalog.BodyRadius, Catalog.BodyHeight);
                    var wantedHorizontal = math.distance(previous.xz, ((float3)ground.point).xz);
                    var acceptedHorizontal = math.distance(previous.xz, ((float3)accepted).xz);
                    var horizontalFraction = wantedHorizontal > 0.00001f
                        ? math.saturate(acceptedHorizontal / wantedHorizontal) : 1f;
                    var targetHeight = Mathf.Lerp(previous.y, ground.point.y, horizontalFraction);
                    state.Position = new float3(accepted.x,
                        SmoothSurfaceHeight(previous.y, targetHeight,
                            Catalog.SurfaceVerticalSpeed, probe.DeltaTime), accepted.z);
                    movedDistance = math.distance(previous.xz, state.Position.xz);
                    if (math.lengthsq(brain.Direction) > 0.01f && state.StunUntil <= now && brain.Attacking == 0)
                        state.Rotation = math.slerp(state.Rotation, quaternion.LookRotationSafe(
                            (float3)Vector3.ProjectOnPlane(state.Position - previous, ground.normal), ground.normal),
                            1 - math.exp(-12 * probe.DeltaTime));
                    AttachSurface(ref state, ground.collider);
                    ReleaseBoardedCrew(ref brain, state);
                    UpdateCrowdSnapshot(crowdBodies, state);
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
                    !(ignoreMinor ? IsMinorObstacle(hit.collider) : IsIslandDecoration(hit.collider)) && Walkable(hit))
                    ground = hit;
            }
            return ground;
        }

        private static bool ValidSolid(Collider collider) => collider != null && !collider.isTrigger &&
            collider.GetComponentInParent<WaterObject>() == null &&
            collider.GetComponentInParent<NetworkPlayerController>() == null &&
            collider.GetComponentInParent<WorldItem>() == null;
        private bool IsMinorObstacle(Collider collider)
        {
            // Generated/baked island decorations are known explicitly. Never classify an
            // island terrain chunk by its bounds: digging can make a chunk arbitrarily small.
            if (IsIslandDecoration(collider)) return true;
            if (collider is TerrainCollider || collider.attachedRigidbody != null ||
                collider.GetComponentInParent<MovingPlatform>() != null ||
                collider.GetComponentInParent<EnemySurfaceAnchor>() != null) return false;
            var size = collider.bounds.size;
            return Catalog.IgnoredObstacleWidth > 0 && Mathf.Max(size.x, size.z) <= Catalog.IgnoredObstacleWidth;
        }
        private static bool IsIslandDecoration(Collider collider)
        {
            var island = collider.GetComponentInParent<ProceduralIsland>();
            return island != null && island.IsDecorationCollider(collider);
        }
        private bool Walkable(RaycastHit hit)
        {
            if (hit.collider == null || hit.normal.y < Mathf.Cos(Catalog.MaximumSlope * Mathf.Deg2Rad)) return false;
            if (_water.TryWaterLevel(hit.point, out var level) && hit.point.y < level - 0.05f) return false;
            return true;
        }
        private bool TryGround(Vector3 origin, float distance, out RaycastHit result)
        {
            result = default;
            var count = Physics.RaycastNonAlloc(origin, Vector3.down, _hits, distance,
                Catalog.SurfaceLayers, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
                if ((result.collider == null || _hits[i].distance < result.distance) &&
                    ValidSolid(_hits[i].collider) && !IsMinorObstacle(_hits[i].collider) && Walkable(_hits[i]))
                    result = _hits[i];
            return result.collider != null;
        }
        private bool CanSee(Vector3 fromFeet, Vector3 toFeet) =>
            !SolidBetween(fromFeet + Vector3.up * (Catalog.BodyHeight * 0.7f), toFeet + Vector3.up * 0.9f, true);
        // Player melee keeps its physical occlusion rules; only enemy perception ignores props.
        public bool SolidBetween(Vector3 from, Vector3 to) => SolidBetween(from, to, false);
        private bool SolidBetween(Vector3 from, Vector3 to, bool ignoreMinorObstacles)
        {
            var delta = to - from;
            if (delta.sqrMagnitude < 0.00001f) return false;
            var count = Physics.RaycastNonAlloc(from, delta.normalized, _hits, delta.magnitude,
                Catalog.SurfaceLayers, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
                if (ValidSolid(_hits[i].collider) && (!ignoreMinorObstacles || !IsMinorObstacle(_hits[i].collider)))
                    return true;
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
            EnemyCombatType combatType, uint seed)
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
                    Health = Catalog.MaximumHealth,
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
                    LastSurfaceTime = now,
                    NextAttack = now + random.NextFloat(0.5f, 1.5f)
                });
                _byId.Add(state.Id, entity);
            }
            _crewGroups.Add(group);
            return true;
        }

        public void DespawnGroup(int group)
        {
            _crewGroups.Remove(group);
            if (!AttachServer()) return;
            var manager = _serverWorld.EntityManager;
            using var entities = _enemies.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                if (!manager.Exists(entity) || manager.GetComponentData<DotsEnemyBrain>(entity).SpawnGroup != group)
                    continue;
                var id = manager.GetComponentData<DotsEnemyState>(entity).Id;
                _byId.Remove(id);
                _surfaceTransfers.Remove(id);
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
        public bool Damage(int id, float damage, Vector3 attacker)
        {
            if (!CanSimulate || !float.IsFinite(damage) || damage <= 0 || !AttachServer() ||
                !_byId.TryGetValue(id, out var entity) || !_serverWorld.EntityManager.Exists(entity)) return false;
            var manager = _serverWorld.EntityManager;
            var state = manager.GetComponentData<DotsEnemyState>(entity);
            if (state.Health <= 0) return false;
            Carry(ref state);
            var brain = manager.GetComponentData<DotsEnemyBrain>(entity);
            state.Health = Mathf.Max(0, state.Health - damage);
            state.HitRevision++;
            brain.Knockback = math.normalizesafe(state.Position - (float3)attacker) * Catalog.DamageKnockback;
            if (state.Health <= 0)
            {
                state.DeathAt = Now;
                brain.Attacking = 0;
                if (Catalog.LootDrops != null && Catalog.LootDrops.Length > 0)
                {
                    var random = new Random(state.Seed | 1u);
                    var drop = Catalog.LootDrops[random.NextInt(0, Catalog.LootDrops.Length)];
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
                Vector3 center = (Vector3)state.Position + Vector3.up * Catalog.BodyHeight * 0.5f;
                var toward = center - origin;
                if (toward.magnitude > range + Catalog.BodyRadius ||
                    Vector3.Dot(toward.normalized, direction) < 0.35f || SolidBetween(origin, center)) continue;
                Damage(state.Id, damage, origin);
            }
        }
        public bool RayHit(Vector3 from, Vector3 to, float radius, out int id, out float fraction)
        {
            id = 0; fraction = 1;
            if (!CanSimulate || !AttachServer()) return false;
            using var states = _enemies.ToComponentDataArray<DotsEnemyState>(Allocator.Temp);
            foreach (var snapshot in states)
            {
                if (snapshot.Health <= 0 || snapshot.Scene != _scene) continue;
                var state = snapshot; Carry(ref state);
                var bottom = (Vector3)state.Position + Vector3.up * Catalog.BodyRadius;
                var top = (Vector3)state.Position + Vector3.up * (Catalog.BodyHeight - Catalog.BodyRadius);
                if (EnemyHitGeometry.SegmentCapsule(from, to, bottom, top, Catalog.BodyRadius + radius, out var hit) && hit < fraction)
                { fraction = hit; id = state.Id; }
            }
            return id != 0;
        }
        public EntityQuery ServerQuery => _enemies;
        public World ServerWorld => _serverWorld;
        private void ClearServer()
        {
            if (_serverWorld != null && _serverWorld.IsCreated) _serverWorld.EntityManager.DestroyEntity(_enemies);
            _byId.Clear(); _crewGroups.Clear(); _spawns.Clear(); _surfaceTransfers.Clear(); StressCount = 0;
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
