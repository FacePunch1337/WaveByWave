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
        public ulong ShooterClientId, PlayerShipNetworkId;
        public int EnemyShipId, ShotId;
        public uint ShotRevision;
        public byte EnemyTeam;
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
            _water ??= new EquipmentWaterQuery(Resources.Load<EnemyShipDefinition>("EnemyShipDefinition")?.WaterProfile);
            var deltaTime = math.min(0.05f, math.max(0f, SystemAPI.Time.DeltaTime));
            if (deltaTime <= 0f) return;
            Dependency = new MoveCannonProjectiles { DeltaTime = deltaTime }.ScheduleParallel(Dependency);
            Dependency.Complete();
            var manager = EntityManager;
            using var entities = _query.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                var ball = manager.GetComponentData<DotsCannonProjectile>(entity);
                if (!TryImpact(in ball, out var point, out var normal, out var water,
                        out var kind, out var targetId, out var collider) && ball.Age < ball.Lifetime)
                    continue;
                if (kind == 1 && ball.EnemyTeam == 0)
                    DotsEnemyShipRuntime.Instance?.DamageFromPlayerCannon(targetId, ball.Damage, ball.Previous);
                else if (kind == 2)
                    DotsEnemyRuntime.Instance?.Damage(targetId, ball.Damage, ball.Previous);
                else if (kind == 3 && collider != null)
                {
                    collider.GetComponentInParent<ShipCannonBattery>()?.ApplyDamageServer(ball.Damage);
                    if (EquipmentDamageReceiverUtility.TryGet(collider, out var receiver, out _))
                        receiver.ReceiveEquipmentHitServer(ball.Damage, ball.Previous, false);
                }
                ReportImpact(in ball, point, normal, water, kind != 0);
                manager.DestroyEntity(entity);
            }
        }

        // kind: 0 expired, 1 DOTS ship, 2 DOTS skeleton, 3 PhysX target, 4 water.
        private bool TryImpact(in DotsCannonProjectile ball, out Vector3 point,
            out Vector3 normal, out bool water, out byte kind, out int targetId,
            out Collider collider)
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
                    var battery = hit.collider.GetComponentInParent<ShipCannonBattery>();
                    if (battery != null && battery.NetworkObjectId == ball.PlayerShipNetworkId) continue;
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
                if (_water.Crossing(from, to, out var waterPoint, out var waterFraction) &&
                    waterFraction < best)
                {
                    kind = 4; targetId = 0; collider = null;
                    point = waterPoint; normal = Vector3.up; water = true;
                }
            }
            return kind != 0;
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
                    ball.PlayerShipNetworkId, out var source) &&
                source.TryGetComponent<ShipCannonBattery>(out var battery))
                battery.ReportProjectileImpact(ball.ShotId, point, normal, water, show,
                    ball.Started + ball.Age);
        }
    }
}
