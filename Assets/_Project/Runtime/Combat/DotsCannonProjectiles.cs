using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Netcode;
using UnityEngine;
using StylizedWater3;
using WaveByWave.Enemies;
using WaveByWave.Player;
using WaveByWave.Ships;

namespace WaveByWave.Combat
{
    // Server-only DOTS cannon balls. Visuals are still lightweight client events;
    // no NetworkObject, Rigidbody or GameObject is created for each projectile.
    public struct DotsCannonProjectile : IComponentData
    {
        public float3 Position, Previous, Velocity, Gravity, Origin;
        public float Age, Lifetime, Radius, Damage, Started;
        public ulong ShooterClientId, SourceNetworkObjectId;
        public int EnemyShipId, ShotId;
        public uint ShotRevision;
        public byte EnemyTeam, EnteredWater;
    }

    [BurstCompile]
    internal partial struct MoveCannonProjectiles : IJobEntity
    {
        public float DeltaTime;

        private void Execute(ref DotsCannonProjectile ball)
        {
            ball.Previous = ball.Position;
            ball.Position += ball.Velocity * DeltaTime + ball.Gravity * (0.5f * DeltaTime * DeltaTime);
            ball.Velocity += ball.Gravity * DeltaTime;
            ball.Age += DeltaTime;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(EnemyShipServerSystem))]
    public partial class DotsCannonProjectileSystem : SystemBase
    {
        private EntityQuery _query;
        private readonly RaycastHit[] _hits = new RaycastHit[96];
        private EquipmentWaterQuery _water;

        public static bool Spawn(DotsCannonProjectile projectile)
        {
            var world = ClientServerBootstrap.ServerWorld;
            if (world == null || !world.IsCreated) return false;
            var entity = world.EntityManager.CreateEntity(typeof(DotsCannonProjectile));
            world.EntityManager.SetComponentData(entity, projectile);
            return true;
        }

        protected override void OnCreate() =>
            _query = GetEntityQuery(typeof(DotsCannonProjectile));

        protected override void OnDestroy()
        {
            _water?.Dispose();
            _water = null;
        }

        protected override void OnUpdate()
        {
            if (_query.IsEmptyIgnoreFilter) return;
            _water ??= new EquipmentWaterQuery(EnemyShipDefinition.Load()?.WaterProfile);
            var deltaTime = math.min(0.05f, math.max(0f, SystemAPI.Time.DeltaTime));
            if (deltaTime <= 0f) return;
            Dependency = new MoveCannonProjectiles { DeltaTime = deltaTime }.ScheduleParallel(Dependency);
            Dependency.Complete();
            var manager = EntityManager;
            using var entities = _query.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                var ball = manager.GetComponentData<DotsCannonProjectile>(entity);
                var wasUnderwater = ball.EnteredWater != 0;
                if (!TryImpact(ref ball, out var point, out var normal, out var water,
                        out var kind, out var targetId, out var collider, out var waterEntryPoint) &&
                    ball.Age < ball.Lifetime)
                {
                    if (!wasUnderwater && ball.EnteredWater != 0)
                    {
                        ReportWaterEntry(in ball, waterEntryPoint);
                        manager.SetComponentData(entity, ball);
                    }
                    continue;
                }
                if (!wasUnderwater && ball.EnteredWater != 0)
                    ReportWaterEntry(in ball, waterEntryPoint);
                if (kind == 1 && ball.EnemyTeam == 0)
                    DotsEnemyShipRuntime.Instance?.DamageFromPlayerCannon(targetId, ball.Damage, ball.Previous, point);
                else if (kind == 2)
                    DotsEnemyRuntime.Instance?.Damage(targetId, ball.Damage, ball.Previous, point);
                else if (kind == 3 && collider != null)
                {
                    collider.GetComponentInParent<ShipHullHealth>()?.ApplyDamageServer(ball.Damage, point);
                    if (EquipmentDamageReceiverUtility.TryGet(collider, out var receiver, out _))
                        receiver.ReceiveEquipmentHitServer(ball.Damage, ball.Previous, false, point);
                }
                ReportImpact(in ball, point, normal, water, kind != 0);
                manager.DestroyEntity(entity);
            }
        }

        // kind: 0 expired, 1 DOTS ship, 2 DOTS enemy, 3 PhysX target.
        private bool TryImpact(ref DotsCannonProjectile ball, out Vector3 point,
            out Vector3 normal, out bool water, out byte kind, out int targetId,
            out Collider collider, out Vector3 waterEntryPoint)
        {
            var from = (Vector3)ball.Previous;
            var to = (Vector3)ball.Position;
            var delta = to - from;
            var distance = delta.magnitude;
            point = to;
            normal = Vector3.up;
            water = false;
            kind = 0;
            targetId = 0;
            collider = null;
            waterEntryPoint = default;
            var best = 1f;
            if (distance > 0.000001f)
            {
                var count = Physics.SphereCastNonAlloc(from, ball.Radius, delta / distance,
                    _hits, distance, ~0, QueryTriggerInteraction.Ignore);
                var hits = count == _hits.Length
                    ? Physics.SphereCastAll(from, ball.Radius, delta / distance,
                        distance, ~0, QueryTriggerInteraction.Ignore) : _hits;
                if (hits != _hits) count = hits.Length;
                for (var i = 0; i < count; i++)
                {
                    var hit = hits[i];
                    if (hit.collider == null || hit.distance / distance >= best ||
                        hit.collider.GetComponentInParent<WaterObject>() != null) continue;
                    var enemyView = hit.collider.GetComponentInParent<EnemyShipView>();
                    if (enemyView != null)
                    {
                        if (ball.EnemyTeam != 0 || enemyView.ShipId == ball.EnemyShipId) continue;
                    }
                    var hull = hit.collider.GetComponentInParent<ShipHullHealth>();
                    if (hull != null && hull.NetworkObjectId == ball.SourceNetworkObjectId) continue;
                    var player = hit.collider.GetComponentInParent<NetworkPlayerController>();
                    if (ball.EnemyTeam != 0 && player == null &&
                        hit.collider.GetComponentInParent<NetworkHealth>() != null) continue;
                    if (player != null && ball.EnemyTeam == 0 &&
                        player.OwnerClientId == ball.ShooterClientId) continue;
                    best = hit.distance / distance;
                    point = hit.point;
                    normal = hit.normal;
                    collider = hit.collider;
                    kind = enemyView != null ? (byte)1 : (byte)3;
                    targetId = enemyView != null ? enemyView.ShipId : 0;
                }
                if (ball.EnemyTeam == 0)
                {
                    var fleet = DotsEnemyShipRuntime.Instance;
                    if (fleet != null && fleet.ProjectileHit(from, to, ball.Radius,
                            out var shipId, out var fraction, out var shipNormal) && fraction < best)
                    {
                        best = fraction; kind = 1; targetId = shipId; collider = null;
                        point = Vector3.Lerp(from, to, best); normal = shipNormal;
                    }
                    var skeletons = DotsEnemyRuntime.Instance;
                    if (skeletons != null && skeletons.RayHit(from, to, ball.Radius,
                            out var skeletonId, out var skeletonFraction) && skeletonFraction < best)
                    {
                        best = skeletonFraction; kind = 2; targetId = skeletonId; collider = null;
                        point = Vector3.Lerp(from, to, best); normal = -delta.normalized;
                    }
                }
                if (ball.EnteredWater == 0 &&
                    _water.Crossing(from, to, out var waterPoint, out var waterFraction) &&
                    waterFraction < best)
                {
                    ball.EnteredWater = 1;
                    waterEntryPoint = waterPoint;
                }
            }
            return kind != 0;
        }

        private static void ReportWaterEntry(in DotsCannonProjectile ball, Vector3 point)
        {
            if (ball.EnemyTeam != 0) return;
            var manager = NetworkManager.Singleton;
            if (manager != null && manager.SpawnManager.SpawnedObjects.TryGetValue(
                    ball.SourceNetworkObjectId, out var source))
                source.GetComponent<CannonNetworkController>()?.ReportProjectileWaterEntry(
                    ball.ShotId, point, ball.Started + ball.Age);
        }

        private static void ReportImpact(in DotsCannonProjectile ball, Vector3 point,
            Vector3 normal, bool water, bool show)
        {
            if (ball.EnemyTeam != 0)
            {
                DotsEnemyShipRuntime.Instance?.ReportProjectileImpact(ball.EnemyShipId,
                    ball.ShotRevision, point, normal, water, show);
                return;
            }
            var manager = NetworkManager.Singleton;
            if (manager != null && manager.SpawnManager.SpawnedObjects.TryGetValue(
                    ball.SourceNetworkObjectId, out var source))
                source.GetComponent<CannonNetworkController>()?.ReportProjectileImpact(
                    ball.ShotId, point, normal, water, show, ball.Started + ball.Age);
        }
    }
}
