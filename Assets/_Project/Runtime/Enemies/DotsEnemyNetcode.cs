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
        [GhostField(Quantization = 1000)] public float MovementUpdatedAt;
        [GhostField] public uint HitRevision;
        [GhostField(Quantization = 1000)] public float StunUntil;
        [GhostField(Quantization = 1000)] public float DeathAt;
    }

    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    public struct DotsEnemyBrain : IComponentData
    {
        public int Target;
        public int SpawnGroup;
        // Original crew membership survives boarding; server-only, never replicated.
        public int CrewShipId;
        public float3 Direction;
        public float3 MoveDirection;
        public float3 MoveTarget;
        public float TargetDistance;
        public float NextAttack;
        public float StrikeAt;
        public float LastSurfaceTime;
        public float LocomotionIdleTime;
        public float3 Knockback;
        public float3 Separation;
        public byte Attacking;
        public ulong DeckSupport;
        public int DeckNode;
        // Server-only steering memory; Direction remains the actual target direction for combat.
        public float3 CrowdDirection;
        public float3 CrowdTargetDirection;
        public float NextCrowdSteerAt, CrowdClearSince, CrowdBlockedTime, CrowdSideLockedUntil;
        public int CrowdSide, CrowdTarget;
        public ulong CrowdSupport;
        public int TargetSlotTarget;
        public int TargetSlotConfiguration;
        public float2 TargetSlotDirection;
        public float TargetSlotRadiusFactor;
        public byte TargetSlotSettled;
        public int ContactSide;
        public ulong ContactSupport;
        public float2 ContactAvoidance;
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

    public struct EnemyCrowdBody
    {
        public int Id, Scene;
        public ulong SupportId;
        public float3 Position;
    }

    public struct EnemySupportPose
    {
        public float4x4 Matrix;
        public quaternion Rotation;
    }

    public static class EnemyFacing
    {
        public static void Apply(ref DotsEnemyState state, in DotsEnemyBrain brain, quaternion surfaceRotation,
            bool hasSurface, float deltaTime, float now)
        {
            if (brain.Target < 0 || state.StunUntil > now || brain.Attacking != 0 || math.lengthsq(brain.Direction) < 0.0001f) return;
            var up = math.mul(surfaceRotation, math.up());
            var forward = brain.Direction - up * math.dot(brain.Direction, up);
            state.Rotation = math.slerp(state.Rotation, quaternion.LookRotationSafe(forward, up), 1f - math.exp(-12f * deltaTime));
            state.LocalRotation = math.mul(math.inverse(surfaceRotation), state.Rotation);
            if (!hasSurface) state.LocalPosition = state.Position;
        }
    }

    [BurstCompile]
    public partial struct EnemyCarryJob : IJobEntity
    {
        [ReadOnly] public NativeParallelHashMap<ulong, EnemySupportPose> Surfaces;
        public int Scene;
        private void Execute(ref DotsEnemyState state)
        {
            if (state.Scene != Scene || state.Health <= 0 || state.SupportId == 0 || !Surfaces.TryGetValue(state.SupportId, out var frame)) return;
            state.Position = math.transform(frame.Matrix, state.LocalPosition);
            state.Rotation = math.mul(frame.Rotation, state.LocalRotation);
        }
    }

    [BurstCompile]
    public partial struct EnemyFaceJob : IJobEntity
    {
        [ReadOnly] public NativeParallelHashMap<ulong, EnemySupportPose> Surfaces;
        public float DeltaTime, Now;
        public int Scene;
        private void Execute(ref DotsEnemyState state, in DotsEnemyBrain brain)
        {
            if (state.Health <= 0 || state.Scene != Scene) return;
            var hasSurface = state.SupportId != 0 && Surfaces.TryGetValue(state.SupportId, out _);
            var rotation = hasSurface ? Surfaces[state.SupportId].Rotation : quaternion.identity;
            EnemyFacing.Apply(ref state, brain, rotation, hasSurface, DeltaTime, Now);
        }
    }

    [BurstCompile]
    public partial struct EnemyBuildCrowdGridJob : IJobEntity
    {
        public NativeParallelMultiHashMap<int3, EnemyCrowdBody>.ParallelWriter CrowdGrid;
        public float CellSize;

        private void Execute(in DotsEnemyState state)
        {
            if (state.Health <= 0) return;
            var key = new int3((int2)math.floor(state.Position.xz / CellSize),
                EnemyCrowdSteering.Group(state.Scene, state.SupportId));
            CrowdGrid.Add(key, new EnemyCrowdBody
            {
                Id = state.Id, Scene = state.Scene, SupportId = state.SupportId, Position = state.Position
            });
        }
    }

    // Called inside EnemySeekJob: local steering is Burst math, never additional PhysX queries.
    internal static class EnemyCrowdSteering
    {
        // Different ships cannot consume one another's advisory neighbour budget.
        public static int Group(int scene, ulong support) =>
            (int)math.hash(new uint3((uint)scene, (uint)support, (uint)(support >> 32)));

        private struct Choice
        {
            public float2 Direction;
            public float Score, Travel;
        }

        public static void Update(in DotsEnemyState state, ref DotsEnemyBrain brain,
            NativeParallelMultiHashMap<int3, EnemyCrowdBody> grid, float cellSize,
            float lookAhead, float spacing, float heightRange, float speed, float now, float stoppingDistance)
        {
            var toward = brain.MoveDirection.xz;
            if (brain.Target < 0 || brain.Attacking != 0 || brain.TargetDistance <= stoppingDistance ||
                math.lengthsq(toward) < 0.0001f)
            {
                Reset(ref brain);
                return;
            }
            var changed = brain.CrowdTarget != brain.Target || brain.CrowdSupport != state.SupportId ||
                math.dot(brain.CrowdTargetDirection, brain.MoveDirection) < 0.7f;
            if (changed)
            {
                Reset(ref brain);
                brain.CrowdTarget = brain.Target;
                brain.CrowdSupport = state.SupportId;
            }
            // Throttle only the avoidance decision, not pursuit, movement or target selection.
            if (!changed && now < brain.NextCrowdSteerAt)
            {
                if (brain.CrowdSide == 0) brain.CrowdDirection = brain.MoveDirection;
                return;
            }
            brain.NextCrowdSteerAt = now + 0.1f + (math.hash(new int2(state.Id, 173)) & 255u) * (0.04f / 255f);
            brain.CrowdTargetDirection = brain.MoveDirection;
            var reach = math.max(lookAhead, speed * 0.2f + spacing);
            var range = reach + spacing;
            var rangeSq = range * range;
            var cell = (int2)math.floor(state.Position.xz / cellSize);
            var group = Group(state.Scene, state.SupportId);
            var neighbours = new FixedList4096Bytes<float2>();
            // Centre first; cap each cell separately so one packed spawn cannot turn this
            // advisory search into all-pairs work or starve every other surrounding cell.
            for (var sample = 0; sample < 9; sample++)
            {
                var surrounding = sample - 1;
                if (surrounding >= 4) surrounding++; // Skip the already visited centre.
                var offset = sample == 0 ? int2.zero : new int2(surrounding % 3 - 1, surrounding / 3 - 1);
                if (!grid.TryGetFirstValue(new int3(cell + offset, group), out var other, out var iterator)) continue;
                var visited = 0;
                do
                {
                    if (++visited > 32) break;
                    if (other.Id == state.Id || other.Scene != state.Scene || other.SupportId != state.SupportId ||
                        math.abs(other.Position.y - state.Position.y) > heightRange) continue;
                    var delta = other.Position.xz - state.Position.xz;
                    if (math.lengthsq(delta) > rangeSq) continue;
                    if (math.lengthsq(delta) < 0.000001f)
                    {
                        var hash = math.hash(new int2(math.min(state.Id, other.Id), math.max(state.Id, other.Id)));
                        math.sincos((hash & 65535u) * (math.PI * 2f / 65535f), out var sine, out var cosine);
                        delta = new float2(cosine, sine) * (state.Id < other.Id ? 0.001f : -0.001f);
                    }
                    neighbours.Add(delta);
                }
                while (grid.TryGetNextValue(out other, ref iterator));
            }
            var clearance = Clearance(toward, reach, spacing, in neighbours);
            if (clearance >= reach * 0.95f)
            {
                if (brain.CrowdClearSince <= 0) brain.CrowdClearSince = now;
                if (now - brain.CrowdClearSince >= 0.3f) brain.CrowdSide = 0;
                brain.CrowdDirection = brain.MoveDirection;
                return;
            }
            brain.CrowdClearSince = 0;
            var right = new float2(toward.y, -toward.x);
            var previous = math.normalizesafe(brain.CrowdDirection.xz, toward);
            var leftChoice = new Choice { Score = float.MinValue };
            var rightChoice = leftChoice;
            // Evaluate a fixed angular fan, plus the previous heading and exact tangents
            // to the nearest blocker. Backward-oblique steps can escape the rear of a blob.
            var blocker = float2.zero;
            var blockerSq = float.MaxValue;
            for (var i = 0; i < neighbours.Length; i++)
            {
                var delta = neighbours[i];
                var along = math.dot(delta, toward);
                var lateral = math.dot(delta, right);
                if (along < 0 || math.abs(lateral) > spacing) continue;
                var sq = math.lengthsq(delta);
                if (sq < blockerSq) { blocker = delta; blockerSq = sq; }
            }
            var tangentSin = math.min(1f, (spacing + 0.025f) / math.max(0.001f, math.sqrt(blockerSq)));
            var tangentCos = math.sqrt(math.max(0, 1f - tangentSin * tangentSin));
            var blockerDirection = math.normalizesafe(blocker, toward);
            var blockerRight = new float2(blockerDirection.y, -blockerDirection.x);
            var travel = math.max(spacing, speed * 0.2f);
            for (var sample = 0; sample < 19; sample++)
            {
                math.sincos(sample * (math.PI / 8f), out var sine, out var cosine);
                var direction = toward * cosine + right * sine;
                if (sample == 16) direction = previous;
                if (sample == 17) direction = blockerDirection * tangentCos + blockerRight * tangentSin;
                if (sample == 18) direction = blockerDirection * tangentCos - blockerRight * tangentSin;
                var free = Clearance(direction, reach, spacing, in neighbours);
                var fraction = math.saturate(free / travel);
                var score = fraction * 4f + free / reach * 1.8f + math.dot(direction, toward) +
                    math.dot(direction, previous) * 0.2f;
                var choice = new Choice { Direction = direction, Score = score, Travel = fraction };
                var side = math.dot(direction, right);
                if (side <= 0.001f && score > leftChoice.Score) leftChoice = choice;
                if (side >= -0.001f && score > rightChoice.Score) rightChoice = choice;
            }
            var heldSide = brain.CrowdSide;
            if (heldSide == 0)
            {
                var difference = rightChoice.Score - leftChoice.Score;
                heldSide = math.abs(difference) > 0.05f ? (difference > 0 ? 1 : -1) :
                    ((math.hash(new int2(state.Id, 61)) & 1u) == 0 ? 1 : -1);
                brain.CrowdSideLockedUntil = now + 0.9f;
            }
            var selected = heldSide > 0 ? rightChoice : leftChoice;
            var alternative = heldSide > 0 ? leftChoice : rightChoice;
            // Surface rejection feeds back here too: a chosen flank must not pin a bot
            // against the sea forever. Do not switch sides every time neighbours shuffle.
            if (now >= brain.CrowdSideLockedUntil &&
                (brain.CrowdBlockedTime >= 0.6f || selected.Travel < 0.2f && alternative.Travel > 0.65f))
            {
                heldSide = -heldSide;
                selected = alternative;
                brain.CrowdSideLockedUntil = now + 1.2f;
                brain.CrowdBlockedTime = 0;
            }
            brain.CrowdSide = heldSide;
            brain.CrowdDirection = new float3(selected.Direction.x, 0, selected.Direction.y);
        }

        public static void Reset(ref DotsEnemyBrain brain)
        {
            brain.CrowdDirection = brain.MoveDirection;
            brain.CrowdTargetDirection = brain.MoveDirection;
            brain.NextCrowdSteerAt = brain.CrowdClearSince = brain.CrowdBlockedTime = 0;
            brain.CrowdSide = 0;
        }

        private static float Clearance(float2 direction, float reach, float spacing, in FixedList4096Bytes<float2> neighbours)
        {
            var result = reach;
            var radiusSq = spacing * spacing;
            for (var i = 0; i < neighbours.Length; i++)
            {
                var delta = neighbours[i];
                var distanceSq = math.lengthsq(delta);
                var along = math.dot(delta, direction);
                if (distanceSq < radiusSq - 0.0001f)
                {
                    // Only an outward motion is useful when spawn positions already overlap.
                    if (along > -0.0001f) return 0;
                    continue;
                }
                if (along <= 0) continue;
                var acrossSq = math.max(0, distanceSq - along * along);
                if (acrossSq >= radiusSq) continue;
                result = math.min(result, math.max(0, along - math.sqrt(radiusSq - acrossSq) - 0.01f));
            }
            return result;
        }
    }

    [BurstCompile]
    public partial struct EnemySeekJob : IJobEntity
    {
        [ReadOnly] public NativeArray<EnemyTarget> Targets;
        [ReadOnly] public NativeParallelMultiHashMap<int3, EnemyCrowdBody> CrowdGrid;
        public float Time;
        public float CrowdCellSize;
        public float CrowdRadius;
        public float CrowdVerticalRange;
        public float AvoidanceLookAhead, BodySpacing, MoveSpeed, StoppingDistance;
        public float TargetSlotSpacing;
        public int TargetSlotCount;
        public bool UseTargetSlots;
        private void Execute(in DotsEnemyState state, ref DotsEnemyBrain brain)
        {
            var oldTarget = brain.Target;
            brain.Target = -1;
            brain.Direction = float3.zero;
            brain.MoveDirection = float3.zero;
            brain.MoveTarget = state.Position;
            brain.Separation = float3.zero;
            brain.TargetDistance = float.MaxValue;
            if (state.Health <= 0 || state.StunUntil > Time)
            {
                EnemyCrowdSteering.Reset(ref brain);
                return;
            }
            // Target intent is global. Distance, water and the current supporting surface must
            // never make a living skeleton forget the nearest living player. Traversability is
            // handled later by surface following, including lateral movement along an edge.
            var best = float.MaxValue;
            var bestDelta = float3.zero;
            for (var i = 0; i < Targets.Length; i++)
            {
                var delta = Targets[i].Position - state.Position;
                var distance = math.lengthsq(delta);
                if (distance >= best) continue;
                best = distance;
                brain.Target = Targets[i].Index;
                bestDelta = delta;
            }
            if (brain.Target >= 0)
            {
                brain.TargetDistance = math.sqrt(best);
                brain.Direction = math.normalizesafe(new float3(bestDelta.x, 0, bestDelta.z));
                brain.MoveDirection = brain.Direction;
                brain.MoveTarget = state.Position + bestDelta;
                if (UseTargetSlots)
                {
                    if (oldTarget != brain.Target || brain.TargetSlotTarget != brain.Target)
                        brain.TargetSlotSettled = 0;
                    brain.TargetSlotTarget = brain.Target;
                    if (brain.TargetSlotConfiguration != TargetSlotCount)
                    {
                        var slot = (state.Id - 1) % math.max(1, TargetSlotCount);
                        if (slot < 0) slot += math.max(1, TargetSlotCount);
                        const float goldenAngle = 2.39996323f;
                        math.sincos(slot * goldenAngle, out var sine, out var cosine);
                        brain.TargetSlotDirection = new float2(cosine, sine);
                        brain.TargetSlotRadiusFactor = 0.525f * math.sqrt(slot);
                        brain.TargetSlotConfiguration = TargetSlotCount;
                    }
                    // Equal-area Vogel spiral. Direction and radius factor are cached once;
                    // no neighbour lists, shared occupancy map or per-frame reassignment.
                    var assignedRadius = StoppingDistance + TargetSlotSpacing * brain.TargetSlotRadiusFactor;
                    var currentRadius = math.length(bestDelta.xz);
                    // A wide outer slot must never order a skeleton that is already closer
                    // to retreat from the player. It keeps the stable angle and advances
                    // inward instead; bots approaching from afar retain the full spiral.
                    var radius = assignedRadius <= currentRadius ? assignedRadius :
                        currentRadius <= StoppingDistance ? currentRadius :
                        math.max(StoppingDistance, currentRadius - TargetSlotSpacing);
                    var slotDelta = bestDelta + new float3(brain.TargetSlotDirection.x * radius, 0,
                        brain.TargetSlotDirection.y * radius);
                    brain.MoveTarget = state.Position + slotDelta;
                    var slotDistance = math.length(slotDelta.xz);
                    var arrive = math.max(0.08f, TargetSlotSpacing * 0.3f);
                    if ((brain.TargetSlotSettled != 0 && slotDistance <= arrive * 1.6f) || slotDistance <= arrive)
                    {
                        brain.TargetSlotSettled = 1;
                        brain.MoveDirection = float3.zero;
                    }
                    else
                    {
                        brain.TargetSlotSettled = 0;
                        brain.MoveDirection = math.normalizesafe(new float3(slotDelta.x, 0, slotDelta.z));
                    }
                }
                else brain.TargetSlotSettled = 0;
            }

            if (AvoidanceLookAhead > 0)
                EnemyCrowdSteering.Update(in state, ref brain, CrowdGrid, CrowdCellSize,
                    AvoidanceLookAhead, BodySpacing, CrowdVerticalRange, MoveSpeed, Time, StoppingDistance);
            else EnemyCrowdSteering.Reset(ref brain);

            if (CrowdRadius <= 0 || CrowdCellSize <= 0) return;
            var cell = (int2)math.floor(state.Position.xz / CrowdCellSize);
            var group = EnemyCrowdSteering.Group(state.Scene, state.SupportId);
            var separation = float3.zero;
            var crowdRadiusSq = CrowdRadius * CrowdRadius;
            for (var z = -1; z <= 1; z++)
            for (var x = -1; x <= 1; x++)
            {
                var key = new int3(cell + new int2(x, z), group);
                if (!CrowdGrid.TryGetFirstValue(key, out var other, out var iterator)) continue;
                do
                {
                    if (other.Id == state.Id || other.Scene != state.Scene ||
                        other.SupportId != state.SupportId || math.abs(other.Position.y - state.Position.y) > CrowdVerticalRange)
                        continue;
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
                while (CrowdGrid.TryGetNextValue(out other, ref iterator));
            }
            var magnitude = math.length(separation);
            brain.Separation = magnitude > 0.0001f
                ? separation / magnitude * math.saturate(magnitude) : float3.zero;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(EnemyGhostRegistrationSystem))]
    public partial class EnemyServerSystem : SystemBase
    {
        private EntityQuery _enemyQuery;
        private NativeParallelMultiHashMap<int3, EnemyCrowdBody> _crowdGrid;
        private NativeParallelHashMap<ulong, EnemySupportPose> _surfaceFrames;

        protected override void OnCreate()
        {
            _enemyQuery = GetEntityQuery(typeof(DotsEnemyState), typeof(DotsEnemyBrain));
        }

        protected override void OnUpdate()
        {
            var runtime = DotsEnemyRuntime.Instance;
            if (runtime != null && runtime.CanSimulate) runtime.TickServer(this);
        }

        public void Seek(NativeArray<EnemyTarget> targets, float now, float crowdRadius, float crowdVerticalRange,
            float avoidanceLookAhead = 0, float bodyRadius = 0.38f, float moveSpeed = 3.8f, float stoppingDistance = 0,
            bool enableSeparation = true, bool useTargetSlots = false, float targetSlotSpacing = 0.35f,
            int targetSlotCount = 6000)
        {
            var count = _enemyQuery.CalculateEntityCount();
            if (count == 0) return;
            Dependency.Complete();
            // Keep a tiny valid container for the optional job field. It is neither
            // cleared nor populated while both legacy neighbour modes are disabled.
            if (!_crowdGrid.IsCreated)
                _crowdGrid = new NativeParallelMultiHashMap<int3, EnemyCrowdBody>(16, Allocator.Persistent);
            var spacing = math.max(crowdRadius, bodyRadius * 2f + 0.04f);
            var separationRadius = enableSeparation ? crowdRadius : 0;
            var avoidanceRange = avoidanceLookAhead > 0 ? math.max(avoidanceLookAhead, moveSpeed * 0.2f + spacing) + spacing : 0;
            var cellSize = math.max(0.01f, math.max(separationRadius, avoidanceRange));
            var useCrowdGrid = separationRadius > 0 || avoidanceLookAhead > 0;
            if (useCrowdGrid)
            {
                // Only the legacy neighbour modes allocate/build this map. Target Slots
                // never clear, fill or read it while all three crowd toggles are off.
                _crowdGrid.Clear();
                if (_crowdGrid.Capacity < count) _crowdGrid.Capacity = math.ceilpow2(count);
                Dependency = new EnemyBuildCrowdGridJob
                {
                    CrowdGrid = _crowdGrid.AsParallelWriter(), CellSize = cellSize
                }.ScheduleParallel(_enemyQuery, Dependency);
            }
            Dependency = new EnemySeekJob
                {
                    Targets = targets, CrowdGrid = _crowdGrid,
                    Time = now,
                    CrowdCellSize = cellSize, CrowdRadius = separationRadius,
                    CrowdVerticalRange = crowdVerticalRange,
                    AvoidanceLookAhead = avoidanceLookAhead, BodySpacing = spacing,
                    MoveSpeed = moveSpeed, StoppingDistance = stoppingDistance,
                    UseTargetSlots = useTargetSlots, TargetSlotSpacing = math.max(0.2f, targetSlotSpacing),
                    TargetSlotCount = math.max(1, targetSlotCount)
                }
                .ScheduleParallel(_enemyQuery, Dependency);
            Dependency.Complete();
        }

        public void CarryPassengers(DotsEnemyRuntime runtime, int scene)
        {
            Dependency.Complete();
            using var states = _enemyQuery.ToComponentDataArray<DotsEnemyState>(Allocator.Temp);
            if (!_surfaceFrames.IsCreated)
                _surfaceFrames = new NativeParallelHashMap<ulong, EnemySupportPose>(16, Allocator.Persistent);
            _surfaceFrames.Clear();
            foreach (var state in states)
            {
                if (state.Scene != scene || state.Health <= 0 || state.SupportId == 0 || _surfaceFrames.ContainsKey(state.SupportId)) continue;
                if (!runtime.TryGetSurfaceFrame(state.SupportId, true, out var frame)) continue;
                if (_surfaceFrames.Count() == _surfaceFrames.Capacity) _surfaceFrames.Capacity *= 2;
                _surfaceFrames.Add(state.SupportId, new EnemySupportPose { Matrix = frame, Rotation = frame.rotation });
            }
            Dependency = new EnemyCarryJob { Surfaces = _surfaceFrames, Scene = scene }.ScheduleParallel(_enemyQuery, Dependency);
        }

        public void FaceTargets(float now, float deltaTime, int scene)
        {
            Dependency = new EnemyFaceJob { Surfaces = _surfaceFrames, Now = now, DeltaTime = deltaTime, Scene = scene }
                .ScheduleParallel(_enemyQuery, Dependency);
            Dependency.Complete();
        }

        protected override void OnDestroy()
        {
            Dependency.Complete();
            if (_crowdGrid.IsCreated) _crowdGrid.Dispose();
            if (_surfaceFrames.IsCreated) _surfaceFrames.Dispose();
        }
    }
}
