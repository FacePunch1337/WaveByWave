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
        public float3 PushVelocity;
        public float LastTick;
        public float NextFire;
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
        public float PreferredRange;
        public float RangeBand;
        public float OrbitWeight;
        public float RangeWeight;

        private void Execute(in DotsEnemyShipState state, ref DotsEnemyShipBrain brain)
        {
            brain.Target = -1;
            brain.TargetDistance = float.MaxValue;
            brain.DesiredDirection = float3.zero;
            if (state.Health <= 0) return;

            // Player ships are registered targets, not discovered by a proximity query.
            var best = float.PositiveInfinity;
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
                var broadsideBlend = math.saturate((PreferredRange - brain.TargetDistance) /
                    math.max(0.1f, RangeBand));
                // Closing pressure never changes sign. Broadside alignment only starts
                // close to the player; neither range correction nor neighbours repel us.
                brain.DesiredDirection = math.normalizesafe(tangent * (OrbitWeight * broadsideBlend) +
                    radial * math.max(0.5f, RangeWeight));
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(EnemyShipGhostRegistrationSystem))]
    [UpdateBefore(typeof(EnemyServerSystem))]
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
            Dependency = new EnemyShipSteeringJob
            {
                Targets = targets,
                PreferredRange = definition.PreferredBroadsideRange,
                RangeBand = definition.RangeCorrectionBand,
                OrbitWeight = definition.OrbitWeight,
                RangeWeight = definition.RangeCorrectionWeight
            }.ScheduleParallel(Dependency);
            Dependency.Complete();
        }
    }
}
