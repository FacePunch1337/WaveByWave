using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace WaveByWave.Enemies
{
    [GhostComponent]
    public struct DotsEnemyShipState : IComponentData
    {
        [GhostField] public int Id;
        [GhostField] public int Scene;
        [GhostField] public uint Seed;
        [GhostField(Quantization = 1000)] public float3 Position;
        [GhostField(Quantization = 1000)] public quaternion Rotation;
        [GhostField(Quantization = 100)] public float Health;
        [GhostField] public uint HitRevision;
        [GhostField] public uint ShotRevision;
        [GhostField(Quantization = 1000)] public float3 ShotOrigin;
        [GhostField(Quantization = 1000)] public float3 ShotVelocity;
        [GhostField(Quantization = 1000)] public float ShotStarted;
        [GhostField] public uint ImpactRevision;
        [GhostField] public uint ImpactShotRevision;
        [GhostField(Quantization = 1000)] public float3 ImpactPoint;
        [GhostField(Quantization = 1000)] public float3 ImpactNormal;
        [GhostField(Quantization = 1000)] public float ImpactAt;
        [GhostField] public byte ImpactFlags;
        [GhostField(Quantization = 1000)] public float DeathAt;
    }

    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    public struct DotsEnemyShipBrain : IComponentData
    {
        public int Target;
        public float3 DesiredDirection;
        public float TargetDistance;
        public float Heading;
        public float Speed;
        public float LastTick;
        public float NextFire;
        public float3 Separation;
        public byte OrbitSide;
        public byte CrewSpawned;
    }

    public struct DotsEnemyShipPrefab : IComponentData { public Entity Value; }
    public struct EnemyShipTarget { public float3 Position; public int Index; }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial class EnemyShipGhostRegistrationSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var prefab = EntityManager.CreateEntity(typeof(DotsEnemyShipState), typeof(DotsEnemyShipBrain));
            GhostPrefabCreation.ConvertToGhostPrefab(EntityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = "WaveByWave.EnemyShip.v1",
                Importance = 40,
                MaxSendRate = 20,
                SupportedGhostModes = GhostModeMask.Interpolated,
                DefaultGhostMode = GhostMode.Interpolated,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = true
            });
            var singleton = EntityManager.CreateEntity(typeof(DotsEnemyShipPrefab));
            EntityManager.SetComponentData(singleton, new DotsEnemyShipPrefab { Value = prefab });
            Enabled = false;
        }
    }

    [BurstCompile]
    internal partial struct EnemyShipSteeringJob : IJobEntity
    {
        [ReadOnly] public NativeArray<EnemyShipTarget> Targets;
        [ReadOnly] public NativeArray<DotsEnemyShipState> Bodies;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> Grid;
        public float TargetRadiusSquared;
        public float PreferredRange;
        public float RangeBand;
        public float OrbitWeight;
        public float RangeWeight;
        public float AvoidanceRadius;
        public float AvoidanceStrength;

        private void Execute(in DotsEnemyShipState state, ref DotsEnemyShipBrain brain)
        {
            brain.Target = -1;
            brain.TargetDistance = float.MaxValue;
            brain.DesiredDirection = float3.zero;
            brain.Separation = float3.zero;
            if (state.Health <= 0) return;

            var best = TargetRadiusSquared;
            var radial = float3.zero;
            for (var i = 0; i < Targets.Length; i++)
            {
                var delta = Targets[i].Position - state.Position;
                delta.y = 0;
                var distance = math.lengthsq(delta);
                if (distance >= best) continue;
                best = distance;
                radial = math.normalizesafe(delta);
                brain.Target = Targets[i].Index;
                brain.TargetDistance = math.sqrt(distance);
            }

            if (brain.Target >= 0)
            {
                var side = brain.OrbitSide == 0 ? -1f : 1f;
                var tangent = new float3(-radial.z, 0, radial.x) * side;
                var correction = math.clamp((brain.TargetDistance - PreferredRange) /
                    math.max(0.1f, RangeBand), -1f, 1f);
                brain.DesiredDirection = math.normalizesafe(tangent * OrbitWeight +
                    radial * (correction * RangeWeight));
            }

            if (AvoidanceRadius <= 0) return;
            var cellSize = math.max(0.1f, AvoidanceRadius);
            var cell = (int2)math.floor(state.Position.xz / cellSize);
            var radiusSq = AvoidanceRadius * AvoidanceRadius;
            var separation = float3.zero;
            for (var z = -1; z <= 1; z++)
            for (var x = -1; x <= 1; x++)
            {
                if (!Grid.TryGetFirstValue(CellKey(cell + new int2(x, z)), out var index, out var iterator)) continue;
                do
                {
                    var other = Bodies[index];
                    if (other.Id == state.Id || other.Health <= 0 || other.Scene != state.Scene) continue;
                    var away = state.Position - other.Position;
                    away.y = 0;
                    var distanceSq = math.lengthsq(away);
                    if (distanceSq >= radiusSq) continue;
                    if (distanceSq < 0.0001f)
                    {
                        var angle = (math.hash(new int2(state.Id, other.Id)) & 65535u) *
                            (math.PI * 2f / 65535f);
                        away = new float3(math.cos(angle), 0, math.sin(angle));
                    }
                    else away /= math.sqrt(distanceSq);
                    separation += away * (1f - math.sqrt(math.max(0f, distanceSq)) / AvoidanceRadius);
                }
                while (Grid.TryGetNextValue(out index, ref iterator));
            }
            brain.Separation = math.normalizesafe(separation) * math.saturate(math.length(separation));
            brain.DesiredDirection = math.normalizesafe(brain.DesiredDirection +
                brain.Separation * AvoidanceStrength, brain.DesiredDirection);
        }

        public static int CellKey(int2 cell) => unchecked(cell.x * 73856093 ^ cell.y * 19349663);
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(EnemyShipGhostRegistrationSystem))]
    public partial class EnemyShipServerSystem : SystemBase
    {
        private EntityQuery _query;

        protected override void OnCreate() =>
            _query = GetEntityQuery(typeof(DotsEnemyShipState), typeof(DotsEnemyShipBrain));

        protected override void OnUpdate()
        {
            var runtime = DotsEnemyShipRuntime.Instance;
            if (runtime != null && runtime.CanSimulate) runtime.TickServer(this);
        }

        public void Steer(NativeArray<EnemyShipTarget> targets, EnemyShipDefinition definition)
        {
            using var bodies = _query.ToComponentDataArray<DotsEnemyShipState>(Allocator.TempJob);
            var cellSize = math.max(0.1f, definition.AvoidanceRadius);
            using var grid = new NativeParallelMultiHashMap<int, int>(math.max(1, bodies.Length), Allocator.TempJob);
            for (var i = 0; i < bodies.Length; i++)
            {
                if (bodies[i].Health <= 0) continue;
                var cell = (int2)math.floor(bodies[i].Position.xz / cellSize);
                grid.Add(EnemyShipSteeringJob.CellKey(cell), i);
            }
            Dependency = new EnemyShipSteeringJob
            {
                Targets = targets,
                Bodies = bodies,
                Grid = grid,
                TargetRadiusSquared = definition.TargetSearchRadius * definition.TargetSearchRadius,
                PreferredRange = definition.PreferredBroadsideRange,
                RangeBand = definition.RangeCorrectionBand,
                OrbitWeight = definition.OrbitWeight,
                RangeWeight = definition.RangeCorrectionWeight,
                AvoidanceRadius = definition.AvoidanceRadius,
                AvoidanceStrength = definition.AvoidanceStrength
            }.ScheduleParallel(Dependency);
            Dependency.Complete();
        }
    }
}
