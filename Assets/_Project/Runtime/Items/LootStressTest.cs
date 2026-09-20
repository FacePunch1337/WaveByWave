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
using WaveByWave.Player;

namespace WaveByWave.Items
{
    public struct LootStressCommand : IRpcCommand
    {
        public int Count;
        public float3 Center;
        public float Radius;
        public uint Seed;
    }

    public struct LootStressDeltaCommand : IRpcCommand
    {
        public int Id;
        public byte Kind;
        public float3 Position;
        public quaternion Rotation;
        public ulong HookOwner;
        public float3 HookOffset;
    }

    public struct LootStressEntity : IComponentData { }
    internal struct LootStressConnectionInitialized : IComponentData { }

    public static class LootStressTest
    {
        public const int MaximumCount = 3000;
        internal const byte Remove = 0, Place = 1, Tether = 2;

        private sealed class ServerItem
        {
            public int CatalogIndex;
            public Vector3 Position;
            public Quaternion Rotation;
            public PlayerEquipment Hook;
            public Vector3 HookOffset;
            public bool Moved;
        }

        private static readonly Dictionary<int, ServerItem> ServerItems = new(MaximumCount);
        private static ItemCatalog _catalog;
        private static GameObject _rarityEffectPrefab;
        private static LootStressCommand _latest;
        private static bool _hasState;

        public static void RegisterCatalog(ItemCatalog catalog, GameObject rarityEffectPrefab = null)
        {
            if (catalog == null) return;
            _catalog = catalog;
            if (rarityEffectPrefab != null) _rarityEffectPrefab = rarityEffectPrefab;
            LootStressPresentation.SetAssets(catalog, _rarityEffectPrefab);
        }

        public static bool SetTarget(int count, Vector3 center, float radius)
        {
            var world = ClientServerBootstrap.ServerWorld;
            if (world == null || !world.IsCreated || _catalog == null)
            {
                Debug.LogWarning("[Loot stress] NFE server world or item catalog is unavailable.");
                return false;
            }
            _latest = new LootStressCommand
            {
                Count = Mathf.Clamp(count, 0, MaximumCount), Center = center,
                Radius = Mathf.Clamp(radius, 10f, 20f),
                Seed = unchecked((uint)Environment.TickCount * 747796405u + (uint)count * 2891336453u)
            };
            _hasState = true;
            RebuildServerState();
            Send(world.EntityManager, _latest, Entity.Null);
            using var connections = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<NetworkId>(), ComponentType.ReadOnly<NetworkStreamInGame>())
                .ToEntityArray(Allocator.Temp);
            foreach (var connection in connections)
                if (!world.EntityManager.HasComponent<LootStressConnectionInitialized>(connection))
                    world.EntityManager.AddComponent<LootStressConnectionInitialized>(connection);
            return true;
        }

        public static bool TryGetServerItem(int id, out ItemDefinition definition, out Vector3 position)
        {
            definition = null;
            position = default;
            if (_catalog == null || !ServerItems.TryGetValue(id, out var item) || item.Hook != null ||
                item.CatalogIndex < 0 || item.CatalogIndex >= _catalog.Items.Count) return false;
            definition = _catalog.Items[item.CatalogIndex];
            position = item.Position;
            return definition != null;
        }

        public static bool RemoveServerItem(int id)
        {
            if (!ServerItems.Remove(id) || ClientServerBootstrap.ServerWorld is not { IsCreated: true } world)
                return false;
            Send(world.EntityManager, new LootStressDeltaCommand { Id = id, Kind = Remove }, Entity.Null);
            return true;
        }

        public static void CaptureWithHookServer(PlayerEquipment hook, Vector3 from, Vector3 to, float radius,
            int maximumTotal, List<int> captured)
        {
            if (hook == null || captured == null || captured.Count >= maximumTotal) return;
            var segment = to - from;
            var candidates = new List<(int id, float distance)>();
            foreach (var pair in ServerItems)
            {
                var item = pair.Value;
                if (item.Hook != null) continue;
                var t = segment.sqrMagnitude > 0.00001f
                    ? Mathf.Clamp01(Vector3.Dot(item.Position - from, segment) / segment.sqrMagnitude) : 0f;
                var distance = Vector3.Distance(item.Position, from + segment * t);
                if (distance <= radius) candidates.Add((pair.Key, distance));
            }
            candidates.Sort((a, b) => a.distance.CompareTo(b.distance));
            for (var i = 0; i < candidates.Count && captured.Count < maximumTotal; i++)
            {
                if (!ServerItems.TryGetValue(candidates[i].id, out var item) || item.Hook != null) continue;
                item.Hook = hook;
                item.HookOffset = Vector3.up * 0.1f + Vector3.right * ((captured.Count % 3 - 1) * 0.15f);
                captured.Add(candidates[i].id);
                Broadcast(new LootStressDeltaCommand { Id = candidates[i].id, Kind = Tether,
                    HookOwner = hook.OwnerClientId, HookOffset = item.HookOffset });
            }
        }

        public static void ReleaseFromHookServer(int id, PlayerEquipment hook, Vector3 position, Quaternion rotation)
        {
            if (!ServerItems.TryGetValue(id, out var item) || item.Hook != hook) return;
            item.Hook = null;
            item.Position = position;
            item.Rotation = rotation;
            item.Moved = true;
            Broadcast(new LootStressDeltaCommand
                { Id = id, Kind = Place, Position = position, Rotation = rotation });
        }

        public static bool TryFindClientItem(Ray ray, Vector3 playerPosition, float reach,
            out IPlayerInteractable target, out float rayDistance, out Vector3 point) =>
            LootStressPresentation.TryFind(ray, playerPosition, reach, out target, out rayDistance, out point);

        public static void ClearLocal()
        {
            LootStressPresentation.Clear();
            ServerItems.Clear();
            _latest = default;
            _hasState = false;
        }

        internal static bool TryGetLatest(out LootStressCommand command)
        {
            command = _latest;
            return _hasState;
        }

        internal static void WriteLateJoinState(EntityCommandBuffer ecb, Entity connection)
        {
            Rpc(ecb, _latest, connection);
            for (var id = 0; id < _latest.Count; id++)
            {
                if (!ServerItems.TryGetValue(id, out var item))
                    Rpc(ecb, new LootStressDeltaCommand { Id = id, Kind = Remove }, connection);
                else if (item.Hook != null)
                    Rpc(ecb, new LootStressDeltaCommand { Id = id, Kind = Tether,
                        HookOwner = item.Hook.OwnerClientId, HookOffset = item.HookOffset }, connection);
                else if (item.Moved)
                    Rpc(ecb, new LootStressDeltaCommand { Id = id, Kind = Place,
                        Position = item.Position, Rotation = item.Rotation }, connection);
            }
        }

        internal static List<int> RenderableCatalogIndices()
        {
            var result = new List<int>();
            if (_catalog == null) return result;
            for (var i = 0; i < _catalog.Items.Count; i++)
                if (TryGetVisual(_catalog.Items[i], out _, out _)) result.Add(i);
            return result;
        }

        internal static bool TryGetVisual(ItemDefinition definition, out MeshFilter filter, out MeshRenderer renderer)
        {
            filter = null;
            renderer = null;
            var prefab = definition != null ? definition.WorldVisualPrefab : null;
            if (prefab == null) return false;
            var filters = prefab.GetComponentsInChildren<MeshFilter>(true);
            if (filters.Length != 1 || filters[0].sharedMesh == null) return false;
            filter = filters[0];
            renderer = filter.GetComponent<MeshRenderer>();
            return renderer != null && renderer.sharedMaterials.Length > 0 && renderer.sharedMaterials[0] != null;
        }

        private static void RebuildServerState()
        {
            ServerItems.Clear();
            var definitions = RenderableCatalogIndices();
            if (definitions.Count == 0) return;
            var random = new Unity.Mathematics.Random(_latest.Seed == 0 ? 1u : _latest.Seed);
            for (var id = 0; id < _latest.Count; id++)
            {
                var catalogIndex = definitions[random.NextInt(definitions.Count)];
                NextPose(ref random, _latest, out var position, out var rotation);
                ServerItems[id] = new ServerItem
                    { CatalogIndex = catalogIndex, Position = position, Rotation = rotation };
            }
        }

        internal static void NextPose(ref Unity.Mathematics.Random random, LootStressCommand command,
            out Vector3 position, out Quaternion rotation)
        {
            var angle = random.NextFloat(0f, math.PI * 2f);
            var distance = math.sqrt(random.NextFloat()) * command.Radius;
            position = (Vector3)command.Center + new Vector3(math.cos(angle) * distance,
                0.12f + random.NextFloat(0f, 0.2f), math.sin(angle) * distance);
            rotation = Quaternion.Euler(0f, random.NextFloat(0f, 360f), 0f);
        }

        private static void Broadcast(LootStressDeltaCommand delta)
        {
            if (ClientServerBootstrap.ServerWorld is { IsCreated: true } world)
                Send(world.EntityManager, delta, Entity.Null);
        }

        private static void Send<T>(EntityManager manager, T command, Entity target) where T : unmanaged, IRpcCommand
        {
            var entity = manager.CreateEntity();
            manager.AddComponentData(entity, command);
            manager.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = target });
        }

        private static void Rpc<T>(EntityCommandBuffer ecb, T command, Entity target) where T : unmanaged, IRpcCommand
        {
            var entity = ecb.CreateEntity();
            ecb.AddComponent(entity, command);
            ecb.AddComponent(entity, new SendRpcCommandRequest { TargetConnection = target });
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct LootStressLateJoinSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!LootStressTest.TryGetLatest(out _)) return;
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (_, connection) in SystemAPI.Query<RefRO<NetworkId>>().WithAll<NetworkStreamInGame>()
                         .WithNone<LootStressConnectionInitialized>().WithEntityAccess())
            {
                ecb.AddComponent<LootStressConnectionInitialized>(connection);
                LootStressTest.WriteLateJoinState(ecb, connection);
            }
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct LootStressClientSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            var hasInitial = false;
            var initial = default(LootStressCommand);
            var deltas = new NativeList<LootStressDeltaCommand>(Allocator.Temp);
            foreach (var (command, entity) in SystemAPI.Query<RefRO<LootStressCommand>>()
                         .WithAll<ReceiveRpcCommandRequest>().WithEntityAccess())
            {
                initial = command.ValueRO;
                hasInitial = true;
                ecb.DestroyEntity(entity);
            }
            foreach (var (delta, entity) in SystemAPI.Query<RefRO<LootStressDeltaCommand>>()
                         .WithAll<ReceiveRpcCommandRequest>().WithEntityAccess())
            {
                deltas.Add(delta.ValueRO);
                ecb.DestroyEntity(entity);
            }
            ecb.Playback(state.EntityManager);
            ecb.Dispose();

            // RenderMeshUtility performs structural changes. Apply presentation only after
            // every SystemAPI query has been fully disposed and its receive entities removed.
            if (hasInitial) LootStressPresentation.Apply(state.World, initial);
            for (var i = 0; i < deltas.Length; i++) LootStressPresentation.ApplyDelta(deltas[i]);
            deltas.Dispose();
            LootStressPresentation.UpdateDynamicItems();
        }
    }

    internal static class LootStressPresentation
    {
        private sealed class Variant
        {
            public Mesh Mesh;
            public Material[] Materials;
            public Vector3 Offset, Scale;
            public Quaternion Rotation;
            public float Radius;
        }

        private sealed class ClientItem
        {
            public int Id;
            public ItemDefinition Definition;
            public Variant Variant;
            public readonly List<Entity> Entities = new();
            public Entity BeamEntity;
            public Vector3 Position;
            public Quaternion Rotation;
            public ulong HookOwner = ulong.MaxValue;
            public Vector3 HookOffset;
            public GameObject Effect;
        }

        private sealed class StressInteractable : IPlayerInteractable
        {
            private readonly int _id;
            private readonly string _name;
            public StressInteractable(int id, string name) { _id = id; _name = name; }
            public string GetInteractionPrompt(NetworkPlayerController player) => $"Подобрать {_name} ×1";
            public void Interact(NetworkPlayerController player) => player?.Inventory?.PickupStressItem(_id);
        }

        private static readonly Dictionary<int, ClientItem> Items = new(LootStressTest.MaximumCount);
        private static readonly List<Material> RuntimeMaterials = new(5);
        private static ItemCatalog _catalog;
        private static GameObject _effectPrefab;
        private static World _world;
        private static LootStressCommand? _pending;
        private static float _nextEffectRefresh;

        internal static void SetAssets(ItemCatalog catalog, GameObject effectPrefab)
        {
            _catalog = catalog;
            if (effectPrefab != null) _effectPrefab = effectPrefab;
            if (_pending.HasValue && ClientServerBootstrap.ClientWorld is { IsCreated: true } world)
            { var command = _pending.Value; _pending = null; Apply(world, command); }
        }

        internal static void Apply(World world, LootStressCommand command)
        {
            if (_catalog == null) { _pending = command; return; }
            Clear();
            if (command.Count <= 0 || world == null || !world.IsCreated) return;
            var catalogIndices = LootStressTest.RenderableCatalogIndices();
            var variants = BuildVariants(catalogIndices);
            if (variants.Count == 0) return;
            _world = world;
            var materials = new List<Material>();
            var beamMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var glowMaterials = BuildGlowMaterials();
            var meshes = new Mesh[variants.Count + (beamMesh != null && glowMaterials.Length > 0 ? 1 : 0)];
            var materialStart = new int[variants.Count];
            for (var i = 0; i < variants.Count; i++)
            {
                meshes[i] = variants[i].Mesh;
                materialStart[i] = materials.Count;
                materials.AddRange(variants[i].Materials);
            }
            var beamMeshIndex = -1;
            var beamMaterialStart = materials.Count;
            if (beamMesh != null && glowMaterials.Length > 0)
            {
                beamMeshIndex = variants.Count;
                meshes[beamMeshIndex] = beamMesh;
                materials.AddRange(glowMaterials);
            }
            var renderArray = new RenderMeshArray(materials.ToArray(), meshes);
            var description = new RenderMeshDescription(ShadowCastingMode.On, true,
                MotionVectorGenerationMode.Camera, 0, uint.MaxValue, LightProbeUsage.BlendProbes);
            var random = new Unity.Mathematics.Random(command.Seed == 0 ? 1u : command.Seed);
            for (var id = 0; id < command.Count; id++)
            {
                var variantIndex = random.NextInt(variants.Count);
                LootStressTest.NextPose(ref random, command, out var position, out var rotation);
                var variant = variants[variantIndex];
                var item = new ClientItem { Id = id, Definition = _catalog.Items[catalogIndices[variantIndex]],
                    Variant = variant, Position = position, Rotation = rotation };
                var subMeshes = Mathf.Min(variant.Mesh.subMeshCount, variant.Materials.Length);
                for (ushort sub = 0; sub < subMeshes; sub++)
                {
                    var entity = world.EntityManager.CreateEntity(typeof(LocalTransform), typeof(LootStressEntity));
                    RenderMeshUtility.AddComponents(entity, world.EntityManager, description, renderArray,
                        MaterialMeshInfo.FromRenderMeshArrayIndices(materialStart[variantIndex] + sub, variantIndex, sub));
                    world.EntityManager.AddComponentData(entity, new PostTransformMatrix());
                    item.Entities.Add(entity);
                }
                if (beamMeshIndex >= 0)
                {
                    item.BeamEntity = world.EntityManager.CreateEntity(typeof(LocalTransform), typeof(LootStressEntity));
                    RenderMeshUtility.AddComponents(item.BeamEntity, world.EntityManager,
                        new RenderMeshDescription(ShadowCastingMode.Off, false,
                            MotionVectorGenerationMode.ForceNoMotion, 0, uint.MaxValue, LightProbeUsage.Off),
                        renderArray, MaterialMeshInfo.FromRenderMeshArrayIndices(
                            beamMaterialStart + (int)item.Definition.Rarity, beamMeshIndex));
                    world.EntityManager.AddComponentData(item.BeamEntity, new PostTransformMatrix());
                }
                Items.Add(id, item);
                SetPose(item);
            }
            Debug.Log($"[Loot stress] Created {Items.Count} interactive DOTS items with prefab materials.");
        }

        internal static void ApplyDelta(LootStressDeltaCommand delta)
        {
            if (!Items.TryGetValue(delta.Id, out var item)) return;
            if (delta.Kind == LootStressTest.Remove) { Destroy(item); Items.Remove(delta.Id); return; }
            if (delta.Kind == LootStressTest.Tether)
            { item.HookOwner = delta.HookOwner; item.HookOffset = delta.HookOffset; return; }
            item.HookOwner = ulong.MaxValue;
            item.Position = delta.Position;
            item.Rotation = delta.Rotation;
            SetPose(item);
        }

        internal static void UpdateDynamicItems()
        {
            if (_world == null || !_world.IsCreated) return;
            PlayerEquipment[] hooks = null;
            foreach (var item in Items.Values)
            {
                if (item.HookOwner != ulong.MaxValue)
                {
                    hooks ??= UnityEngine.Object.FindObjectsByType<PlayerEquipment>(FindObjectsSortMode.None);
                    foreach (var hook in hooks)
                        if (hook.IsSpawned && hook.OwnerClientId == item.HookOwner)
                        { item.Position = hook.RenderedHookPosition + item.HookOffset; SetPose(item); break; }
                }
                if (item.Effect != null) item.Effect.transform.position = item.Position;
            }
            if (Time.unscaledTime >= _nextEffectRefresh) { _nextEffectRefresh = Time.unscaledTime + 0.5f; RefreshEffects(); }
        }

        internal static bool TryFind(Ray ray, Vector3 playerPosition, float reach,
            out IPlayerInteractable target, out float rayDistance, out Vector3 point)
        {
            target = null; rayDistance = float.PositiveInfinity; point = default;
            foreach (var item in Items.Values)
            {
                if (item.HookOwner != ulong.MaxValue || (item.Position - playerPosition).sqrMagnitude > reach * reach) continue;
                var center = item.Position + item.Rotation * item.Variant.Offset;
                var projection = Vector3.Dot(center - ray.origin, ray.direction);
                if (projection < 0f || projection >= rayDistance) continue;
                var closest = ray.origin + ray.direction * projection;
                var squared = (center - closest).sqrMagnitude;
                if (squared > item.Variant.Radius * item.Variant.Radius) continue;
                rayDistance = Mathf.Max(0f, projection - Mathf.Sqrt(item.Variant.Radius * item.Variant.Radius - squared));
                point = ray.origin + ray.direction * rayDistance;
                target = new StressInteractable(item.Id, item.Definition.DisplayName);
            }
            return target != null;
        }

        internal static void Clear()
        {
            foreach (var item in Items.Values) Destroy(item);
            Items.Clear();
            foreach (var material in RuntimeMaterials)
                if (material != null) UnityEngine.Object.Destroy(material);
            RuntimeMaterials.Clear();
            _world = null;
        }

        private static List<Variant> BuildVariants(IReadOnlyList<int> indices)
        {
            var result = new List<Variant>(indices.Count);
            foreach (var index in indices)
            {
                if (!LootStressTest.TryGetVisual(_catalog.Items[index], out var filter, out var renderer)) continue;
                var matrix = filter.transform.localToWorldMatrix;
                var scale = matrix.lossyScale;
                var extents = Vector3.Scale(filter.sharedMesh.bounds.extents,
                    new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                var materials = renderer.sharedMaterials;
                result.Add(new Variant { Mesh = filter.sharedMesh, Materials = materials,
                    Offset = matrix.GetColumn(3), Rotation = matrix.rotation, Scale = scale,
                    Radius = Mathf.Clamp(extents.magnitude, 0.22f, 1.2f) });
            }
            return result;
        }

        private static void SetPose(ClientItem item)
        {
            if (_world == null || !_world.IsCreated) return;
            var position = item.Position + item.Rotation * item.Variant.Offset;
            var rotation = item.Rotation * item.Variant.Rotation;
            foreach (var entity in item.Entities)
            {
                if (!_world.EntityManager.Exists(entity)) continue;
                _world.EntityManager.SetComponentData(entity,
                    LocalTransform.FromPositionRotationScale(position, rotation, 1f));
                _world.EntityManager.SetComponentData(entity,
                    new PostTransformMatrix { Value = float4x4.Scale(item.Variant.Scale) });
            }
            if (item.BeamEntity != Entity.Null && _world.EntityManager.Exists(item.BeamEntity))
            {
                _world.EntityManager.SetComponentData(item.BeamEntity,
                    LocalTransform.FromPositionRotationScale(item.Position + Vector3.up * 0.72f,
                        Quaternion.identity, 1f));
                _world.EntityManager.SetComponentData(item.BeamEntity,
                    new PostTransformMatrix { Value = float4x4.Scale(0.08f, 1.4f, 0.08f) });
            }
        }

        private static Material[] BuildGlowMaterials()
        {
            var source = _effectPrefab != null
                ? _effectPrefab.GetComponentInChildren<LineRenderer>(true)?.sharedMaterial : null;
            if (source == null) return Array.Empty<Material>();
            var result = new Material[5];
            for (var i = 0; i < result.Length; i++)
            {
                var color = i switch
                {
                    1 => new Color(0.3f, 1f, 0.4f, 0.22f),
                    2 => new Color(0.15f, 0.55f, 1f, 0.22f),
                    3 => new Color(0.8f, 0.25f, 1f, 0.22f),
                    4 => new Color(1f, 0.65f, 0.12f, 0.22f),
                    _ => new Color(0.8f, 0.9f, 1f, 0.16f)
                };
                var material = new Material(source) { name = $"DOTS rarity {i}", enableInstancing = true };
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                if (material.HasProperty("_Color")) material.SetColor("_Color", color);
                if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", color * 2f);
                result[i] = material;
                RuntimeMaterials.Add(material);
            }
            return result;
        }

        private static void RefreshEffects()
        {
            if (_effectPrefab == null || Camera.main == null) return;
            var cameraPosition = Camera.main.transform.position;
            var ordered = new List<ClientItem>(Items.Values);
            ordered.Sort((a, b) => (a.Position - cameraPosition).sqrMagnitude.CompareTo((b.Position - cameraPosition).sqrMagnitude));
            for (var i = 0; i < ordered.Count; i++)
            {
                var item = ordered[i];
                if (i < 96 && item.Effect == null)
                {
                    item.Effect = UnityEngine.Object.Instantiate(_effectPrefab, item.Position, Quaternion.identity);
                    if (item.Effect.TryGetComponent<LootRarityGlow>(out var glow)) glow.Initialize(item.Definition.RarityColor);
                }
                else if (i >= 96 && item.Effect != null) { UnityEngine.Object.Destroy(item.Effect); item.Effect = null; }
            }
        }

        private static void Destroy(ClientItem item)
        {
            if (_world != null && _world.IsCreated)
            {
                foreach (var entity in item.Entities) if (_world.EntityManager.Exists(entity)) _world.EntityManager.DestroyEntity(entity);
                if (item.BeamEntity != Entity.Null && _world.EntityManager.Exists(item.BeamEntity))
                    _world.EntityManager.DestroyEntity(item.BeamEntity);
            }
            if (item.Effect != null) UnityEngine.Object.Destroy(item.Effect);
        }
    }
}
