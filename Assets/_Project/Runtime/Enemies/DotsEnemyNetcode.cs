using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace WaveByWave.Enemies
{
    // Enemy authority lives in ECS; the boundary consults NGO players/platforms.
    [GhostComponent]
    public struct DotsEnemyState : IComponentData
    {
        [GhostField] public int Id;
        [GhostField] public int Scene;
        [GhostField] public uint Seed;
        [GhostField] public EnemyCombatType CombatType;
        [GhostField] public EnemyAnimationState Animation;
        [GhostField(Quantization = 1000)] public float3 Position;
        [GhostField(Quantization = 1000)] public quaternion Rotation;
        [GhostField(Quantization = 100)] public float Health;
        [GhostField(Quantization = 1000)] public float AnimationStarted;
        [GhostField(Quantization = 1000)] public float AnimationDuration;
        [GhostField] public ulong SupportId;
        [GhostField(Quantization = 1000)] public float3 LocalPosition;
        [GhostField(Quantization = 1000)] public quaternion LocalRotation;
        [GhostField] public uint HitRevision;
        [GhostField(Quantization = 1000)] public float StunUntil;
        [GhostField(Quantization = 1000)] public float DeathAt;
    }

    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    public struct DotsEnemyBrain : IComponentData
    {
        public int Target;
        public int SpawnGroup;
        public float3 Direction;
        public float TargetDistance;
        public float NextAttack;
        public float StrikeAt;
        public float LastSurfaceTime;
        public float LocomotionIdleTime;
        public float3 Knockback;
        public float3 Separation;
        public byte Attacking;
    }

    public struct DotsEnemyPrefab : IComponentData { public Entity Value; }
    public struct EnemyTarget { public float3 Position; public int Index; }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial class EnemyGhostRegistrationSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var prefab = EntityManager.CreateEntity(typeof(DotsEnemyState), typeof(DotsEnemyBrain));
            GhostPrefabCreation.ConvertToGhostPrefab(EntityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = "WaveByWave.SkeletonEnemy.v1",
                Importance = 10,
                MaxSendRate = 10,
                SupportedGhostModes = GhostModeMask.Interpolated,
                DefaultGhostMode = GhostMode.Interpolated,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = true
            });
            var singleton = EntityManager.CreateEntity(typeof(DotsEnemyPrefab));
            EntityManager.SetComponentData(singleton, new DotsEnemyPrefab { Value = prefab });
            Enabled = false;
        }
    }

    [BurstCompile]
    public partial struct EnemySeekJob : IJobEntity
    {
        [ReadOnly] public NativeArray<EnemyTarget> Targets;
        [ReadOnly] public NativeArray<DotsEnemyState> Bodies;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> CrowdGrid;
        public float Time;
        public float CrowdCellSize;
        public float CrowdRadius;
        private void Execute(in DotsEnemyState state, ref DotsEnemyBrain brain)
        {
            brain.Target = -1;
            brain.Direction = float3.zero;
            brain.Separation = float3.zero;
            brain.TargetDistance = float.MaxValue;
            if (state.Health <= 0 || state.StunUntil > Time) return;
            // Target intent is global. Distance, water and the current supporting surface must
            // never make a living skeleton forget the nearest living player. Traversability is
            // handled later by surface following, including lateral movement along an edge.
            var best = float.MaxValue;
            for (var i = 0; i < Targets.Length; i++)
            {
                var delta = Targets[i].Position - state.Position;
                var distance = math.lengthsq(delta);
                if (distance >= best) continue;
                best = distance;
                brain.Target = Targets[i].Index;
                brain.TargetDistance = math.sqrt(distance);
                brain.Direction = math.normalizesafe(new float3(delta.x, 0, delta.z));
            }

            if (CrowdRadius <= 0 || CrowdCellSize <= 0) return;
            var cell = (int2)math.floor(state.Position.xz / CrowdCellSize);
            var separation = float3.zero;
            var crowdRadiusSq = CrowdRadius * CrowdRadius;
            for (var z = -1; z <= 1; z++)
            for (var x = -1; x <= 1; x++)
            {
                var key = CellKey(cell + new int2(x, z));
                if (!CrowdGrid.TryGetFirstValue(key, out var index, out var iterator)) continue;
                do
                {
                    var other = Bodies[index];
                    if (other.Id == state.Id || other.Health <= 0 || other.Scene != state.Scene) continue;
                    var delta = new float3(state.Position.x - other.Position.x, 0,
                        state.Position.z - other.Position.z);
                    var distanceSq = math.lengthsq(delta);
                    if (distanceSq >= crowdRadiusSq) continue;
                    float3 away;
                    float distance;
                    if (distanceSq > 0.000001f)
                    {
                        distance = math.sqrt(distanceSq);
                        away = delta / distance;
                    }
                    else
                    {
                        // Stable opposite directions for a pair that spawned at exactly one point.
                        var low = math.min(state.Id, other.Id);
                        var high = math.max(state.Id, other.Id);
                        var hash = math.hash(new int2(low, high));
                        var angle = (hash & 65535u) * (math.PI * 2f / 65535f);
                        away = new float3(math.cos(angle), 0, math.sin(angle));
                        if (state.Id > other.Id) away = -away;
                        distance = 0;
                    }
                    separation += away * (1f - distance / CrowdRadius);
                }
                while (CrowdGrid.TryGetNextValue(out index, ref iterator));
            }
            var magnitude = math.length(separation);
            brain.Separation = magnitude > 0.0001f
                ? separation / magnitude * math.saturate(magnitude) : float3.zero;
        }

        public static int CellKey(int2 cell) => unchecked(cell.x * 73856093 ^ cell.y * 19349663);
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(EnemyGhostRegistrationSystem))]
    public partial class EnemyServerSystem : SystemBase
    {
        private EntityQuery _enemyQuery;

        protected override void OnCreate()
        {
            _enemyQuery = GetEntityQuery(typeof(DotsEnemyState), typeof(DotsEnemyBrain));
        }

        protected override void OnUpdate()
        {
            var runtime = DotsEnemyRuntime.Instance;
            if (runtime != null && runtime.CanSimulate) runtime.TickServer(this);
        }

        public void Seek(NativeArray<EnemyTarget> targets, float now, float crowdRadius)
        {
            using var bodies = _enemyQuery.ToComponentDataArray<DotsEnemyState>(Allocator.TempJob);
            var cellSize = math.max(0.01f, crowdRadius);
            using var crowdGrid = new NativeParallelMultiHashMap<int, int>(math.max(1, bodies.Length), Allocator.TempJob);
            for (var i = 0; i < bodies.Length; i++)
            {
                if (bodies[i].Health <= 0) continue;
                var cell = (int2)math.floor(bodies[i].Position.xz / cellSize);
                crowdGrid.Add(EnemySeekJob.CellKey(cell), i);
            }
            Dependency = new EnemySeekJob
                {
                    Targets = targets, Bodies = bodies, CrowdGrid = crowdGrid,
                    Time = now,
                    CrowdCellSize = cellSize, CrowdRadius = crowdRadius
                }
                .ScheduleParallel(Dependency);
            Dependency.Complete();
        }
    }
}
