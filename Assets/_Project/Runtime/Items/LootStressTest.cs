using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Netcode;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using StylizedWater3;
using WaveByWave.Player;

namespace WaveByWave.Items
{
    public struct LootStressCommand : IRpcCommand
    {
        public int Count;
        public float3 Center;
        public float Radius;
        public uint Seed;
        public FixedString64Bytes SceneName;
    }

    public struct LootStressDeltaCommand : IRpcCommand
    {
        public int Id;
        public byte Kind;
        public float3 Position;
        public quaternion Rotation;
        public quaternion BaseRotation;
        public ulong HookOwner;
        public ulong SupportId;
        public float3 HookOffset;
        public bool OnWater;
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
            public bool OnWater;
            public float WaterOffset;
            public Vector2 WaterSize;
            public Quaternion BaseRotation;
            public NetworkObject Support;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
        }

        private static readonly Dictionary<int, ServerItem> ServerItems = new(MaximumCount);
        private static ItemCatalog _catalog;
        private static GameObject _rarityEffectPrefab;
        private static WaveProfile _waterProfile;
        private static EquipmentWaterQuery _water;
        private static LootStressCommand _latest;
        private static bool _hasState;
        private static string _sceneName;
        private static bool _sceneEventsBound;
        private static readonly RaycastHit[] SurfaceHits = new RaycastHit[256];

        internal static WaveProfile WaterProfile => _waterProfile;

        public static void RegisterCatalog(ItemCatalog catalog, GameObject rarityEffectPrefab = null,
            WaveProfile waterProfile = null)
        {
            if (catalog == null) return;
            _catalog = catalog;
            if (rarityEffectPrefab != null) _rarityEffectPrefab = rarityEffectPrefab;
            if (waterProfile != null && waterProfile != _waterProfile)
            {
                _water?.Dispose();
                _waterProfile = waterProfile;
                _water = new EquipmentWaterQuery(waterProfile);
            }
            EnsureSceneEvents();
            LootStressPresentation.SetAssets(catalog, _rarityEffectPrefab, _waterProfile);
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
                Seed = unchecked((uint)Environment.TickCount * 747796405u + (uint)count * 2891336453u),
                SceneName = new FixedString64Bytes(SceneManager.GetActiveScene().name)
            };
            _sceneName = _latest.SceneName.ToString();
            _hasState = true;
            RebuildServerState();
            Send(world.EntityManager, _latest, Entity.Null);
            foreach (var pair in ServerItems)
            {
                var item = pair.Value;
                if (item.Support == null) continue;
                Send(world.EntityManager, new LootStressDeltaCommand
                {
                    Id = pair.Key, Kind = Place, Position = item.Position, Rotation = item.Rotation,
                    BaseRotation = item.BaseRotation, SupportId = SupportWireId(item.Support)
                }, Entity.Null);
            }
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
            UpdateSupportedPose(item);
            UpdateWaterPose(item);
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
                UpdateSupportedPose(item);
                UpdateWaterPose(item);
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
                item.Support = null;
                item.OnWater = false;
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
            if (TryResolveSurface(position + Vector3.up * 0.5f, out var point, out var normal,
                out var onWater, out var support))
            {
                item.OnWater = onWater;
                item.BaseRotation = rotation;
                item.Rotation = Quaternion.FromToRotation(Vector3.up, normal) * rotation;
                var definition = item.CatalogIndex >= 0 && item.CatalogIndex < _catalog.Items.Count
                    ? _catalog.Items[item.CatalogIndex] : null;
                item.WaterSize = GetSurfaceSize(definition);
                item.WaterOffset = GetSurfaceClearance(definition, item.Rotation, normal);
                item.Position = point + (onWater ? Vector3.up : normal) * item.WaterOffset;
                SetSupport(item, support);
            }
            else { item.OnWater = false; item.Support = null; item.Position = position; item.Rotation = rotation; }
            item.Moved = true;
            Broadcast(new LootStressDeltaCommand
                { Id = id, Kind = Place, Position = item.Position, Rotation = item.Rotation,
                    BaseRotation = item.BaseRotation, OnWater = item.OnWater,
                    SupportId = SupportWireId(item.Support) });
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
            _sceneName = null;
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
                else if (item.Moved || item.Support != null)
                {
                    UpdateSupportedPose(item);
                    Rpc(ecb, new LootStressDeltaCommand { Id = id, Kind = Place,
                        Position = item.Position, Rotation = item.Rotation, BaseRotation = item.BaseRotation,
                        OnWater = item.OnWater,
                        SupportId = SupportWireId(item.Support) }, connection);
                }
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
                NextPose(ref random, _latest, out var spawn, out var yaw);
                var definition = _catalog.Items[catalogIndex];
                var baseRotation = yaw * (definition != null ? definition.RestingRotation : Quaternion.identity);
                var position = spawn;
                var rotation = baseRotation;
                var onWater = false;
                NetworkObject support = null;
                if (TryResolveSurface(spawn, out var point, out var normal, out onWater, out support))
                {
                    rotation = Quaternion.FromToRotation(Vector3.up, normal) * baseRotation;
                    position = point + (onWater ? Vector3.up : normal) *
                        GetSurfaceClearance(definition, rotation, normal);
                }
                var waterOffset = GetSurfaceClearance(definition, rotation, Vector3.up);
                if (onWater) waterOffset = GetSurfaceClearance(definition, rotation, normal);
                var item = new ServerItem
                    { CatalogIndex = catalogIndex, Position = position, Rotation = rotation,
                        BaseRotation = baseRotation, OnWater = onWater,
                        WaterOffset = waterOffset, WaterSize = GetSurfaceSize(definition) };
                SetSupport(item, support);
                ServerItems[id] = item;
            }
        }

        internal static void NextPose(ref Unity.Mathematics.Random random, LootStressCommand command,
            out Vector3 position, out Quaternion rotation)
        {
            var angle = random.NextFloat(0f, math.PI * 2f);
            var distance = math.sqrt(random.NextFloat()) * command.Radius;
            position = (Vector3)command.Center + new Vector3(math.cos(angle) * distance,
                random.NextFloat(1.5f, 4f), math.sin(angle) * distance);
            rotation = Quaternion.Euler(0f, random.NextFloat(0f, 360f), 0f);
        }

        internal static bool TryResolveSurface(Vector3 origin, out Vector3 point, out Vector3 normal,
            out bool onWater, out NetworkObject support)
        {
            point = origin;
            normal = Vector3.up;
            onWater = false;
            support = null;
            var count = UnityEngine.Physics.RaycastNonAlloc(origin + Vector3.up * 0.5f, Vector3.down,
                SurfaceHits, 512f, ~0, QueryTriggerInteraction.Ignore);
            var bestY = float.NegativeInfinity;
            for (var i = 0; i < count; i++)
            {
                var hit = SurfaceHits[i];
                if (hit.point.y <= bestY || Vector3.Dot(hit.normal, Vector3.up) < 0.3f ||
                    hit.collider.GetComponentInParent<NetworkPlayerController>() != null ||
                    hit.collider.GetComponentInParent<WorldItem>() != null) continue;
                bestY = hit.point.y;
                point = hit.point;
                normal = hit.normal;
                support = hit.collider.GetComponentInParent<NetworkObject>();
                if (support != null && !support.IsSpawned) support = null;
            }
            if (_water != null && _water.TrySurface(origin, Vector2.one * 0.45f, out var height, out var waterNormal) &&
                height <= origin.y + 0.5f && height > bestY + 0.01f)
            {
                point = new Vector3(origin.x, height, origin.z);
                normal = waterNormal;
                onWater = true;
                support = null;
                bestY = height;
            }
            return bestY > float.NegativeInfinity;
        }

        private static void SetSupport(ServerItem item, NetworkObject support)
        {
            item.Support = support;
            if (support == null) return;
            var inverse = WorldItem.GetPhysicsFrame(support).inverse;
            item.LocalPosition = inverse.MultiplyPoint3x4(item.Position);
            item.LocalRotation = Quaternion.Inverse(support.TryGetComponent<Rigidbody>(out var body)
                ? body.rotation : support.transform.rotation) * item.Rotation;
            item.OnWater = false;
        }

        private static ulong SupportWireId(NetworkObject support) =>
            support != null ? support.NetworkObjectId + 1UL : 0UL;

        private static void UpdateSupportedPose(ServerItem item)
        {
            if (item.Support == null || !item.Support.IsSpawned) return;
            var frame = WorldItem.GetPhysicsFrame(item.Support);
            item.Position = frame.MultiplyPoint3x4(item.LocalPosition);
            var rotation = item.Support.TryGetComponent<Rigidbody>(out var body)
                ? body.rotation : item.Support.transform.rotation;
            item.Rotation = rotation * item.LocalRotation;
        }

        private static void UpdateWaterPose(ServerItem item)
        {
            if (!item.OnWater || _water == null ||
                !_water.TrySurface(item.Position, item.WaterSize, out var height, out var normal)) return;
            item.Rotation = Quaternion.FromToRotation(Vector3.up, normal) * item.BaseRotation;
            var definition = item.CatalogIndex >= 0 && item.CatalogIndex < _catalog.Items.Count
                ? _catalog.Items[item.CatalogIndex] : null;
            item.WaterOffset = GetSurfaceClearance(definition, item.Rotation, normal);
            item.Position = new Vector3(item.Position.x, height + item.WaterOffset, item.Position.z);
        }

        internal static float GetSurfaceClearance(ItemDefinition definition, Quaternion rotation, Vector3 normal)
        {
            if (!TryGetVisual(definition, out var filter, out _)) return 0.15f;
            var bounds = filter.sharedMesh.bounds;
            var matrix = filter.transform.localToWorldMatrix;
            var minimum = float.PositiveInfinity;
            for (var corner = 0; corner < 8; corner++)
            {
                var point = bounds.center + Vector3.Scale(bounds.extents, new Vector3(
                    (corner & 1) == 0 ? -1f : 1f,
                    (corner & 2) == 0 ? -1f : 1f,
                    (corner & 4) == 0 ? -1f : 1f));
                minimum = Mathf.Min(minimum, Vector3.Dot(rotation * matrix.MultiplyPoint3x4(point), normal));
            }
            return Mathf.Max(0.005f, -minimum + 0.005f);
        }

        private static Vector2 GetSurfaceSize(ItemDefinition definition)
        {
            if (!TryGetVisual(definition, out var filter, out _)) return Vector2.one * 0.45f;
            var bounds = filter.sharedMesh.bounds;
            var scale = filter.transform.localToWorldMatrix.lossyScale;
            return new Vector2(Mathf.Clamp(bounds.size.x * Mathf.Abs(scale.x), 0.15f, 1.5f),
                Mathf.Clamp(bounds.size.z * Mathf.Abs(scale.z), 0.15f, 1.5f));
        }

        private static void EnsureSceneEvents()
        {
            if (_sceneEventsBound) return;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            _sceneEventsBound = true;
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            LootStressPresentation.ClearScene(scene.name);
            if (!_hasState || string.IsNullOrEmpty(_sceneName) || scene.name != _sceneName) return;
            if (ClientServerBootstrap.ServerWorld is { IsCreated: true } world)
                Send(world.EntityManager, new LootStressCommand { Count = 0,
                    SceneName = new FixedString64Bytes(scene.name) }, Entity.Null);
            ClearLocal();
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
        }
    }

    [DefaultExecutionOrder(9800)]
    internal sealed class LootStressPresentationDriver : MonoBehaviour
    {
        private void LateUpdate() => LootStressPresentation.UpdateDynamicItems();
        private void OnDestroy() => LootStressPresentation.DriverDestroyed(this);
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
            public Vector3[] Corners;
            public Vector2 SurfaceSize;
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
            public Vector3 SpawnPosition;
            public Vector3 RestPosition;
            public Quaternion RestRotation;
            public Quaternion BaseRotation;
            public double FallStarted;
            public bool Falling;
            public bool OnWater;
            public NetworkObject Support;
            public ulong SupportId;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
            public Vector3 WaterTargetPosition;
            public Quaternion WaterTargetRotation;
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
        private static readonly List<ClientItem> WaterItems = new(LootStressTest.MaximumCount);
        private static ItemCatalog _catalog;
        private static GameObject _effectPrefab;
        private static EquipmentWaterQuery _water;
        private static World _world;
        private static LootStressCommand? _pending;
        private static float _nextEffectRefresh;
        private static int _waterCursor;
        private static string _activeScene;
        private static LootStressPresentationDriver _driver;

        internal static void SetAssets(ItemCatalog catalog, GameObject effectPrefab, WaveProfile waterProfile)
        {
            _catalog = catalog;
            if (effectPrefab != null) _effectPrefab = effectPrefab;
            if (waterProfile != null && _water == null) _water = new EquipmentWaterQuery(waterProfile);
            EnsureDriver();
            if (_pending.HasValue && ClientServerBootstrap.ClientWorld is { IsCreated: true } world)
            { var command = _pending.Value; _pending = null; Apply(world, command); }
        }

        internal static void Apply(World world, LootStressCommand command)
        {
            if (_catalog == null) { _pending = command; return; }
            Clear();
            if (command.Count <= 0 || world == null || !world.IsCreated ||
                command.SceneName.ToString() != SceneManager.GetActiveScene().name) return;
            _activeScene = command.SceneName.ToString();
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
                LootStressTest.NextPose(ref random, command, out var spawnPosition, out var yaw);
                var variant = variants[variantIndex];
                var definition = _catalog.Items[catalogIndices[variantIndex]];
                var baseRotation = yaw * (definition != null ? definition.RestingRotation : Quaternion.identity);
                var restPosition = spawnPosition;
                var restRotation = baseRotation;
                var onWater = false;
                NetworkObject support = null;
                if (LootStressTest.TryResolveSurface(spawnPosition, out var point, out var normal,
                    out onWater, out support))
                {
                    restRotation = Quaternion.FromToRotation(Vector3.up, normal) * baseRotation;
                    restPosition = point + (onWater ? Vector3.up : normal) *
                        SurfaceClearance(variant, restRotation, normal);
                }
                var item = new ClientItem { Id = id, Definition = _catalog.Items[catalogIndices[variantIndex]],
                    Variant = variant, Position = spawnPosition, Rotation = baseRotation,
                    SpawnPosition = spawnPosition, RestPosition = restPosition, RestRotation = restRotation,
                    BaseRotation = baseRotation, OnWater = onWater, FallStarted = Time.timeAsDouble,
                    Falling = spawnPosition.y > restPosition.y + 0.01f };
                SetSupport(item, support);
                item.WaterTargetPosition = restPosition;
                item.WaterTargetRotation = restRotation;
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
                if (onWater) WaterItems.Add(item);
                SetPose(item);
            }
            Debug.Log($"[Loot stress] Created {Items.Count} interactive DOTS items with prefab materials.");
        }

        internal static void ApplyDelta(LootStressDeltaCommand delta)
        {
            if (!Items.TryGetValue(delta.Id, out var item)) return;
            if (delta.Kind == LootStressTest.Remove) { Destroy(item); Items.Remove(delta.Id); return; }
            if (delta.Kind == LootStressTest.Tether)
            {
                item.HookOwner = delta.HookOwner;
                item.HookOffset = delta.HookOffset;
                item.Support = null;
                item.SupportId = 0;
                if (item.OnWater) WaterItems.Remove(item);
                item.OnWater = false;
                return;
            }
            item.HookOwner = ulong.MaxValue;
            item.Position = delta.Position;
            item.Rotation = delta.Rotation;
            item.RestPosition = delta.Position;
            item.RestRotation = delta.Rotation;
            item.BaseRotation = delta.BaseRotation;
            item.Falling = false;
            item.WaterTargetPosition = delta.Position;
            item.WaterTargetRotation = delta.Rotation;
            item.SupportId = delta.SupportId;
            item.Support = ResolveSupport(delta.SupportId);
            if (item.Support != null) SetSupport(item, item.Support);
            if (item.OnWater != delta.OnWater)
            {
                WaterItems.Remove(item);
                item.OnWater = delta.OnWater;
                if (item.OnWater) WaterItems.Add(item);
            }
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
                else
                {
                    if (item.Support == null && item.SupportId != 0)
                    {
                        item.Support = ResolveSupport(item.SupportId);
                        if (item.Support != null) SetSupport(item, item.Support);
                    }
                    if (item.Support != null && item.Support.IsSpawned)
                    {
                        item.RestPosition = item.Support.transform.TransformPoint(item.LocalPosition);
                        item.RestRotation = item.Support.transform.rotation * item.LocalRotation;
                    }
                    if (item.Falling)
                    {
                        var elapsed = Mathf.Max(0f, (float)(Time.timeAsDouble - item.FallStarted));
                        item.Position = item.RestPosition;
                        item.Position.y = Mathf.Max(item.RestPosition.y,
                            item.SpawnPosition.y - 0.5f * 18f * elapsed * elapsed);
                        if (item.Position.y <= item.RestPosition.y + 0.001f)
                        {
                            item.Position = item.RestPosition;
                            item.Rotation = item.RestRotation;
                            item.Falling = false;
                        }
                        SetPose(item);
                    }
                    else if (item.Support != null && item.Support.IsSpawned)
                    {
                        item.Position = item.RestPosition;
                        item.Rotation = item.RestRotation;
                        SetPose(item);
                    }
                    else if (item.OnWater)
                    {
                        var blend = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
                        item.Position = Vector3.Lerp(item.Position, item.WaterTargetPosition, blend);
                        item.Rotation = Quaternion.Slerp(item.Rotation, item.WaterTargetRotation, blend);
                        SetPose(item);
                    }
                }
                if (item.Effect != null) item.Effect.transform.position = item.Position;
            }
            UpdateWaterItems();
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
            WaterItems.Clear();
            _waterCursor = 0;
            foreach (var material in RuntimeMaterials)
                if (material != null) UnityEngine.Object.Destroy(material);
            RuntimeMaterials.Clear();
            _world = null;
            _activeScene = null;
        }

        internal static void ClearScene(string sceneName)
        {
            if (!string.IsNullOrEmpty(_activeScene) && _activeScene == sceneName) Clear();
        }

        private static void EnsureDriver()
        {
            if (_driver != null) return;
            var host = new GameObject("DOTS Loot Presentation") { hideFlags = HideFlags.HideInHierarchy };
            UnityEngine.Object.DontDestroyOnLoad(host);
            _driver = host.AddComponent<LootStressPresentationDriver>();
        }

        internal static void DriverDestroyed(LootStressPresentationDriver driver)
        {
            if (_driver == driver) _driver = null;
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
                var bounds = filter.sharedMesh.bounds;
                var corners = new Vector3[8];
                for (var corner = 0; corner < corners.Length; corner++)
                    corners[corner] = matrix.MultiplyPoint3x4(bounds.center + Vector3.Scale(bounds.extents,
                        new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f,
                            (corner & 4) == 0 ? -1f : 1f)));
                result.Add(new Variant { Mesh = filter.sharedMesh, Materials = materials,
                    Offset = matrix.GetColumn(3), Rotation = matrix.rotation, Scale = scale,
                    Radius = Mathf.Clamp(extents.magnitude, 0.22f, 1.2f), Corners = corners,
                    SurfaceSize = new Vector2(Mathf.Clamp(filter.sharedMesh.bounds.size.x * Mathf.Abs(scale.x), 0.15f, 1.5f),
                        Mathf.Clamp(filter.sharedMesh.bounds.size.z * Mathf.Abs(scale.z), 0.15f, 1.5f)) });
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
                if (_world.EntityManager.HasComponent<LocalToWorld>(entity))
                    _world.EntityManager.SetComponentData(entity,
                        new LocalToWorld { Value = float4x4.TRS(position, rotation, item.Variant.Scale) });
                _world.EntityManager.SetComponentData(entity,
                    new PostTransformMatrix { Value = float4x4.Scale(item.Variant.Scale) });
            }
            if (item.BeamEntity != Entity.Null && _world.EntityManager.Exists(item.BeamEntity))
            {
                _world.EntityManager.SetComponentData(item.BeamEntity,
                    LocalTransform.FromPositionRotationScale(item.Position + Vector3.up * 0.72f,
                        Quaternion.identity, 1f));
                if (_world.EntityManager.HasComponent<LocalToWorld>(item.BeamEntity))
                    _world.EntityManager.SetComponentData(item.BeamEntity, new LocalToWorld
                    {
                        Value = float4x4.TRS(item.Position + Vector3.up * 0.72f,
                            Quaternion.identity, new float3(0.08f, 1.4f, 0.08f))
                    });
                _world.EntityManager.SetComponentData(item.BeamEntity,
                    new PostTransformMatrix { Value = float4x4.Scale(0.08f, 1.4f, 0.08f) });
            }
        }

        private static void UpdateWaterItems()
        {
            if (_water == null || WaterItems.Count == 0) return;
            var budget = Mathf.Max(1, Mathf.CeilToInt(WaterItems.Count / 10f));
            for (var n = 0; n < budget && WaterItems.Count > 0; n++)
            {
                if (_waterCursor >= WaterItems.Count) _waterCursor = 0;
                var item = WaterItems[_waterCursor++];
                if (item.Falling || item.HookOwner != ulong.MaxValue) continue;
                if (!_water.TrySurface(item.Position, item.Variant.SurfaceSize, out var height, out var normal)) continue;
                item.WaterTargetRotation = Quaternion.FromToRotation(Vector3.up, normal) * item.BaseRotation;
                var clearance = SurfaceClearance(item.Variant, item.WaterTargetRotation, normal);
                item.WaterTargetPosition = new Vector3(item.Position.x, height + clearance, item.Position.z);
            }
        }

        private static float SurfaceClearance(Variant variant, Quaternion rotation, Vector3 normal)
        {
            if (variant?.Corners == null || variant.Corners.Length == 0) return 0.15f;
            var minimum = float.PositiveInfinity;
            for (var i = 0; i < variant.Corners.Length; i++)
                minimum = Mathf.Min(minimum, Vector3.Dot(rotation * variant.Corners[i], normal));
            return Mathf.Max(0.005f, -minimum + 0.005f);
        }

        private static NetworkObject ResolveSupport(ulong id)
        {
            if (id == 0 || NetworkManager.Singleton == null) return null;
            return NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(id - 1UL, out var support)
                ? support : null;
        }

        private static void SetSupport(ClientItem item, NetworkObject support)
        {
            item.Support = support;
            item.SupportId = support != null ? support.NetworkObjectId + 1UL : item.SupportId;
            if (support == null) return;
            item.LocalPosition = support.transform.InverseTransformPoint(item.RestPosition);
            item.LocalRotation = Quaternion.Inverse(support.transform.rotation) * item.RestRotation;
            item.OnWater = false;
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
            WaterItems.Remove(item);
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
