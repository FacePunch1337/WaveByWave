using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace WaveByWave.Items
{
    public struct LootStressCommand : IRpcCommand
    {
        public int Count;
        public float3 Center;
        public float Radius;
        public uint Seed;
    }

    public struct LootStressEntity : IComponentData { }
    internal struct LootStressConnectionInitialized : IComponentData { }

    /// <summary>
    /// Host-side entry point for the admin panel. Only a compact deterministic command crosses
    /// the network; every client renders the same item field as lightweight Entities Graphics
    /// instances. This isolates NFE/Steam and rendering throughput from GameObject overhead.
    /// </summary>
    public static class LootStressTest
    {
        public const int MaximumCount = 3000;
        private static ItemCatalog _catalog;
        private static LootStressCommand _latest;
        private static bool _hasState;

        internal static bool TryGetLatest(out LootStressCommand command)
        {
            command = _latest;
            return _hasState;
        }

        public static void RegisterCatalog(ItemCatalog catalog)
        {
            if (catalog == null)
                return;
            _catalog = catalog;
            LootStressPresentation.SetCatalog(catalog);
        }

        public static bool SetTarget(int count, Vector3 center, float radius)
        {
            var world = ClientServerBootstrap.ServerWorld;
            if (world == null || !world.IsCreated)
            {
                Debug.LogWarning("[Loot stress] Netcode for Entities server world is unavailable.");
                return false;
            }

            count = Mathf.Clamp(count, 0, MaximumCount);
            radius = Mathf.Clamp(radius, 10f, 20f);
            _latest = new LootStressCommand
            {
                Count = count,
                Center = center,
                Radius = radius,
                Seed = unchecked((uint)Environment.TickCount * 747796405u + (uint)count * 2891336453u)
            };
            _hasState = true;

            var manager = world.EntityManager;
            var request = manager.CreateEntity();
            manager.AddComponentData(request, _latest);
            manager.AddComponentData(request, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            return true;
        }

        public static void ClearLocal()
        {
            LootStressPresentation.Clear();
            _hasState = false;
            _latest = default;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct LootStressLateJoinSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!LootStressTest.TryGetLatest(out var latest))
                return;

            var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (_, connection) in SystemAPI.Query<RefRO<NetworkId>>()
                         .WithAll<NetworkStreamInGame>()
                         .WithNone<LootStressConnectionInitialized>()
                         .WithEntityAccess())
            {
                commands.AddComponent<LootStressConnectionInitialized>(connection);
                var request = commands.CreateEntity();
                commands.AddComponent(request, latest);
                commands.AddComponent(request, new SendRpcCommandRequest { TargetConnection = connection });
            }
            commands.Playback(state.EntityManager);
            commands.Dispose();
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct LootStressClientSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var hasCommand = false;
            var latest = default(LootStressCommand);
            var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (command, entity) in SystemAPI.Query<RefRO<LootStressCommand>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                latest = command.ValueRO;
                hasCommand = true;
                commands.DestroyEntity(entity);
            }
            commands.Playback(state.EntityManager);
            commands.Dispose();

            if (hasCommand)
                LootStressPresentation.Apply(state.World, latest);
        }
    }

    internal static class LootStressPresentation
    {
        private readonly struct Variant
        {
            public readonly Mesh Mesh;
            public readonly Material Material;
            public readonly quaternion Rotation;
            public readonly float3 Scale;

            public Variant(Mesh mesh, Material material, quaternion rotation, float3 scale)
            {
                Mesh = mesh;
                Material = material;
                Rotation = rotation;
                Scale = scale;
            }
        }

        private static readonly List<Entity> Entities = new(LootStressTest.MaximumCount);
        private static readonly List<Material> RuntimeMaterials = new();
        private static ItemCatalog _catalog;
        private static World _world;
        private static LootStressCommand? _pending;

        internal static void SetCatalog(ItemCatalog catalog)
        {
            _catalog = catalog;
            if (_pending.HasValue && ClientServerBootstrap.ClientWorld is { IsCreated: true } world)
            {
                var command = _pending.Value;
                _pending = null;
                Apply(world, command);
            }
        }

        internal static void Apply(World world, LootStressCommand command)
        {
            if (_catalog == null)
            {
                _pending = command;
                return;
            }

            Clear();
            if (command.Count <= 0 || world == null || !world.IsCreated)
                return;

            var variants = BuildVariants();
            if (variants.Count == 0)
            {
                Debug.LogError("[Loot stress] Item catalog contains no MeshRenderer-based prefab visuals.");
                return;
            }

            _world = world;
            var manager = world.EntityManager;
            var meshes = new Mesh[variants.Count];
            var materials = new Material[variants.Count];
            for (var i = 0; i < variants.Count; i++)
            {
                meshes[i] = variants[i].Mesh;
                materials[i] = variants[i].Material;
            }
            var renderMeshes = new RenderMeshArray(materials, meshes);
            var renderDescription = new RenderMeshDescription(
                ShadowCastingMode.Off, false, MotionVectorGenerationMode.ForceNoMotion,
                0, uint.MaxValue, LightProbeUsage.Off);

            var random = new Unity.Mathematics.Random(command.Seed == 0 ? 1u : command.Seed);
            var count = math.clamp(command.Count, 0, LootStressTest.MaximumCount);
            var radius = math.clamp(command.Radius, 10f, 20f);
            for (var index = 0; index < count; index++)
            {
                var variantIndex = random.NextInt(variants.Count);
                var variant = variants[variantIndex];
                var angle = random.NextFloat(0f, math.PI * 2f);
                var distance = math.sqrt(random.NextFloat()) * radius;
                var position = command.Center + new float3(math.cos(angle) * distance,
                    0.12f + random.NextFloat(0f, 0.2f), math.sin(angle) * distance);
                var yaw = quaternion.RotateY(random.NextFloat(0f, math.PI * 2f));

                var entity = manager.CreateEntity(typeof(LocalTransform), typeof(LootStressEntity));
                manager.SetComponentData(entity, LocalTransform.FromPositionRotationScale(
                    position, math.mul(yaw, variant.Rotation), 1f));
                manager.AddComponentData(entity, new PostTransformMatrix
                {
                    Value = float4x4.Scale(variant.Scale)
                });
                RenderMeshUtility.AddComponents(entity, manager, renderDescription, renderMeshes,
                    MaterialMeshInfo.FromRenderMeshArrayIndices(variantIndex, variantIndex));
                Entities.Add(entity);
            }

            Debug.Log($"[Loot stress] Rendered {count} synchronized DOTS items in radius {radius:0.#} m.");
        }

        internal static void Clear()
        {
            if (_world != null && _world.IsCreated)
            {
                var manager = _world.EntityManager;
                foreach (var entity in Entities)
                    if (manager.Exists(entity)) manager.DestroyEntity(entity);
            }
            Entities.Clear();
            _world = null;
            foreach (var material in RuntimeMaterials)
                if (material != null) UnityEngine.Object.Destroy(material);
            RuntimeMaterials.Clear();
        }

        private static List<Variant> BuildVariants()
        {
            var result = new List<Variant>();
            foreach (var definition in _catalog.Items)
            {
                if (definition == null || definition.WorldVisualPrefab == null)
                    continue;
                var root = ItemVisualUtility.GetBoundsRoot(definition.WorldVisualPrefab);
                if (root == null)
                    continue;
                var filters = root.GetComponentsInChildren<MeshFilter>(true);
                foreach (var filter in filters)
                {
                    if (filter.sharedMesh == null || !filter.TryGetComponent<MeshRenderer>(out var renderer) ||
                        renderer.sharedMaterial == null)
                        continue;

                    var material = new Material(renderer.sharedMaterial)
                    {
                        name = $"Stress {definition.Id} {definition.Rarity}",
                        enableInstancing = true
                    };
                    if (material.HasProperty("_EmissionColor"))
                    {
                        material.EnableKeyword("_EMISSION");
                        material.SetColor("_EmissionColor", definition.RarityColor * 1.8f);
                    }
                    RuntimeMaterials.Add(material);

                    var matrix = filter.transform.localToWorldMatrix;
                    var scale = matrix.lossyScale;
                    result.Add(new Variant(filter.sharedMesh, material, matrix.rotation,
                        new float3(Mathf.Max(0.01f, scale.x), Mathf.Max(0.01f, scale.y), Mathf.Max(0.01f, scale.z))));
                    break;
                }
            }
            return result;
        }
    }
}
