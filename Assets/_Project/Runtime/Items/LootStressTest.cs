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
using WaveByWave.UI;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using StylizedWater3;
using WaveByWave.Player;
using WaveByWave.Enemies;

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
        public int CatalogIndex;
        public float3 StartPosition;
        public float3 Position;
        public quaternion Rotation;
        public quaternion BaseRotation;
        public float3 ArcUp;
        public float Duration;
        public float ArcHeight;
        public ulong HookOwner;
        public ulong SupportId;
        public float3 LocalPosition;
        public quaternion LocalRotation;
        public float3 LocalStart;
        public float3 LocalArcUp;
        public float3 HookOffset;
        public bool OnWater;
        public int IslandId;
        public float FadeDuration;
        public double OpeningAt;
    }

    // Client-side readiness and the server-side item lifetime are independent. A newly connected
    // client explicitly asks for the current snapshot so pre-existing loose items cannot depend on
    // the timing of connection-system discovery.
    public struct LootStressSnapshotRequest : IRpcCommand { public byte ProtocolVersion; }

    public struct LootStressEntity : IComponentData { }
    internal struct LootStressConnectionState : IComponentData
    {
        public uint DeliveredRevision;
    }
    internal struct LootSnapshotRequested : IComponentData
    {
        public FixedString64Bytes SceneName;
    }

    public static partial class LootStressTest
    {
        public const int MaximumCount = 3000;
        internal const byte Remove = 0, Place = 1, Tether = 2, Add = 3;

        private sealed class ServerItem
        {
            public float OpenerLuck;
            public int CatalogIndex;
            public Vector3 Position;
            public Vector3 RestPosition;
            public Vector3 FlightStart;
            public Vector3 ArcUp;
            public double FlightStarted;
            public float FlightDuration;
            public float ArcHeight;
            public bool Dynamic;
            public Quaternion Rotation;
            public PlayerEquipment Hook;
            public Vector3 HookOffset;
            public bool Moved;
            public bool OnWater;
            public float WaterOffset;
            public Vector2 WaterSize;
            public Quaternion BaseRotation;
            public NetworkObject Support;
            public ulong SupportId;
            public Vector3 LocalStart, LocalArcUp;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
            public int IslandId;
            public float FadeDuration;
            public double OpeningAt;
        }

        private static readonly Dictionary<int, ServerItem> ServerItems = new(MaximumCount);
        private static ItemCatalog _catalog;
        private static GameObject _rarityEffectPrefab;
        private static WaveProfile _waterProfile;
        private static EquipmentWaterQuery _water;
        private static LootStressCommand _latest;
        private static uint _stateRevision;
        private static bool _hasState;
        private static string _sceneName;
        private static bool _sceneEventsBound;
        private static int _nextDynamicId = -1;
        private static readonly RaycastHit[] SurfaceHits = new RaycastHit[256];

        internal static WaveProfile WaterProfile => _waterProfile;
        internal static uint StateRevision => _stateRevision;

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
            _stateRevision++;
            if (_stateRevision == 0) _stateRevision = 1;
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
            // A host owns both worlds in this process. Present locally immediately rather than
            // depending on the local Steam/NFE handshake winning a race with the admin slider.
            // The delivery system below sends this revision explicitly to every remote client.
            if (ClientServerBootstrap.ClientWorld is { IsCreated: true } clientWorld)
            {
                LootStressPresentation.Apply(clientWorld, _latest);
                ApplyDynamicItemsLocally();
            }
            return true;
        }

        /// <summary>
        /// Creates the authoritative representation used by every loose item. Inventory and held
        /// items remain compact gameplay data; as soon as an item enters the world it uses this
        /// DOTS/NFE path, regardless of whether it came from a drop, the admin panel or the stress test.
        /// </summary>
        public static bool SpawnWorldItemServer(ItemDefinition definition, Vector3 feet, Vector3 direction,
            NetworkObject preferredSupport)
        {
            if (definition == null || _catalog == null ||
                ClientServerBootstrap.ServerWorld is not { IsCreated: true }) return false;
            var catalogIndex = -1;
            for (var i = 0; i < _catalog.Items.Count; i++)
                if (_catalog.Items[i] == definition) { catalogIndex = i; break; }
            if (catalogIndex < 0 || !TryGetVisual(definition, out _, out _)) return false;

            EnsureStateForCurrentScene(feet);
            var frame = WorldItem.GetPhysicsFrame(preferredSupport);
            var up = preferredSupport != null ? frame.MultiplyVector(Vector3.up).normalized : Vector3.up;
            var aim = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
            var planarAim = Vector3.ProjectOnPlane(aim, up);
            var planarAmount = Mathf.Clamp01(planarAim.magnitude);
            var forward = planarAim.normalized;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
            const float speed = 4.5f;
            const float dropDistance = 0.9f;
            var verticalVelocity = Vector3.Dot(aim, up) * speed;
            var horizontalSpeed = speed * planarAmount;
            var start = feet + up * 0.9f + aim * 0.25f;
            var target = feet + forward * (dropDistance * Mathf.Max(0.15f, planarAmount));
            if (!TryResolveSurface(target, out var point, out var normal, out var onWater, out var support, out var surfaceId))
                return false;

            var firstFall = BallisticFlightTime(Vector3.Dot(start - point, up), verticalVelocity);
            target = feet + forward * (dropDistance * Mathf.Max(0.15f, planarAmount) + horizontalSpeed * firstFall);
            if (!TryResolveSurface(target, out point, out normal, out onWater, out support, out surfaceId)) return false;

            var rotationUp = onWater ? Vector3.up : normal;
            var facing = Vector3.ProjectOnPlane(forward, rotationUp).normalized;
            if (facing.sqrMagnitude < 0.01f) facing = Vector3.Cross(rotationUp, Vector3.right).normalized;
            var baseRotation = Quaternion.LookRotation(facing, rotationUp) * definition.RestingRotation;
            var rotation = onWater ? Quaternion.FromToRotation(Vector3.up, normal) * baseRotation : baseRotation;
            var end = point + (onWater ? Vector3.up : normal) * GetSurfaceClearance(definition, rotation, normal);
            var flightDuration = Mathf.Clamp(BallisticFlightTime(Vector3.Dot(start - end, up), verticalVelocity),
                0.12f, 3f);
            var arcHeight = 0.25f * Mathf.Clamp01(1f - Mathf.Abs(Vector3.Dot(aim, up)) * 2f) +
                            Mathf.Max(0f, verticalVelocity * flightDuration + Vector3.Dot(start - end, up)) * 0.25f;
            var id = _nextDynamicId--;
            var item = new ServerItem
            {
                CatalogIndex = catalogIndex, Position = start, RestPosition = end, FlightStart = start,
                ArcUp = up, FlightStarted = NetworkManager.Singleton.ServerTime.Time,
                FlightDuration = flightDuration, ArcHeight = arcHeight, Dynamic = true,
                Rotation = rotation, BaseRotation = baseRotation, OnWater = onWater,
                WaterOffset = GetSurfaceClearance(definition, rotation, normal), WaterSize = GetSurfaceSize(definition)
            };
            SetSupport(item, support, surfaceId);
            ServerItems.Add(id, item);
            var delta = BuildAddDelta(id, item, flightDuration);
            Broadcast(delta);
            LootStressPresentation.ApplyDelta(delta);
            return true;
        }

        public static bool TryGetServerItem(int id, out ItemDefinition definition, out Vector3 position)
        {
            definition = null;
            position = default;
            if (_catalog == null || !ServerItems.TryGetValue(id, out var item) || item.Hook != null ||
                item.OpeningAt > 0d || !IsExposed(item.IslandId, item.Position) ||
                item.CatalogIndex < 0 || item.CatalogIndex >= _catalog.Items.Count) return false;
            definition = _catalog.Items[item.CatalogIndex];
            UpdateItemPose(item);
            position = item.Position;
            return definition != null;
        }

        // Streaming only needs location/ownership, not the exact current wave height.
        // Avoid hundreds of synchronous wave and prefab-bounds queries on every prune tick.
        internal static bool TryGetStreamingPosition(int id, out Vector3 position)
        {
            position = default;
            if (!ServerItems.TryGetValue(id, out var item) || item.Hook != null || item.OpeningAt > 0d)
                return false;
            if (item.SupportId != 0) UpdateSupportedPose(item);
            position = item.SupportId != 0 ? item.RestPosition : item.Position;
            return true;
        }

        public static bool RemoveServerItem(int id)
        {
            if (!ServerItems.Remove(id) || ClientServerBootstrap.ServerWorld is not { IsCreated: true } world)
                return false;
            Send(world.EntityManager, new LootStressDeltaCommand { Id = id, Kind = Remove }, Entity.Null);
            ServerItemRemoved?.Invoke(id);
            return true;
        }

        public static void CaptureWithHookServer(PlayerEquipment hook, Vector3 from, Vector3 to, float radius,
            int offsetStart, List<int> captured)
        {
            if (hook == null || captured == null) return;
            var segment = to - from;
            var radiusSquared = radius * radius;
            foreach (var pair in ServerItems)
            {
                var item = pair.Value;
                if (item.Hook != null || item.OpeningAt > 0d) continue;

                // Thousands of floating items must not each run a four-sample Gerstner query for
                // every 1/60 s hook substep. X/Z cannot be changed by buoyancy, so reject almost
                // the entire collection before refreshing the exact water height. Supported items
                // use their current platform position; flying items still need their live arc pose.
                if (item.SupportId != 0)
                    UpdateSupportedPose(item);
                if (item.FlightDuration > 0f)
                    UpdateItemPose(item);
                var broadphasePosition = item.SupportId != 0 ? item.RestPosition : item.Position;
                if (HorizontalSegmentDistanceSquared(broadphasePosition, from, to) > radiusSquared)
                    continue;
                if (!IsExposed(item.IslandId, item.Position)) continue;

                UpdateItemPose(item);
                var t = segment.sqrMagnitude > 0.00001f
                    ? Mathf.Clamp01(Vector3.Dot(item.Position - from, segment) / segment.sqrMagnitude) : 0f;
                if ((item.Position - (from + segment * t)).sqrMagnitude > radiusSquared) continue;
                item.Hook = hook;
                item.IslandId = 0;
                // Claimed procedural loot leaves the generation budget immediately,
                // even if the hook is released before the next pruning tick.
                ServerItemRemoved?.Invoke(pair.Key);
                item.FlightDuration = 0f;
                item.RestPosition = item.Position;
                item.Support = null;
                item.SupportId = 0;
                item.OnWater = false;
                item.HookOffset = PlayerEquipment.HookCargoOffset(offsetStart + captured.Count);
                captured.Add(pair.Key);
                Broadcast(new LootStressDeltaCommand { Id = pair.Key, Kind = Tether,
                    HookOwner = hook.OwnerClientId, HookOffset = item.HookOffset });
            }
        }

        private static float HorizontalSegmentDistanceSquared(Vector3 point, Vector3 from, Vector3 to)
        {
            var segment = new Vector2(to.x - from.x, to.z - from.z);
            var relative = new Vector2(point.x - from.x, point.z - from.z);
            var lengthSquared = segment.sqrMagnitude;
            var t = lengthSquared > 0.00001f
                ? Mathf.Clamp01(Vector2.Dot(relative, segment) / lengthSquared)
                : 0f;
            var nearest = new Vector2(from.x, from.z) + segment * t;
            return (new Vector2(point.x, point.z) - nearest).sqrMagnitude;
        }

        public static void ReleaseFromHookServer(int id, PlayerEquipment hook, Vector3 position, Quaternion rotation)
        {
            if (!ServerItems.TryGetValue(id, out var item) || item.Hook != hook) return;
            item.Hook = null;
            if (TryResolveSurface(position + Vector3.up * 0.5f, out var point, out var normal,
                out var onWater, out var support, out var surfaceId))
            {
                item.OnWater = onWater;
                item.BaseRotation = rotation;
                item.Rotation = Quaternion.FromToRotation(Vector3.up, normal) * rotation;
                var definition = item.CatalogIndex >= 0 && item.CatalogIndex < _catalog.Items.Count
                    ? _catalog.Items[item.CatalogIndex] : null;
                item.WaterSize = GetSurfaceSize(definition);
                item.WaterOffset = GetSurfaceClearance(definition, item.Rotation, normal);
                item.Position = point + (onWater ? Vector3.up : normal) * item.WaterOffset;
                item.RestPosition = item.Position;
                SetSupport(item, support, surfaceId);
            }
            else { item.OnWater = false; item.Support = null; item.SupportId = 0; item.Position = position;
                item.RestPosition = position; item.Rotation = rotation; }
            item.FlightDuration = 0f;
            item.Moved = true;
            Broadcast(new LootStressDeltaCommand
                { Id = id, Kind = Place, Position = item.Position, Rotation = item.Rotation,
                    BaseRotation = item.BaseRotation, OnWater = item.OnWater,
                    SupportId = item.SupportId, LocalPosition = item.LocalPosition, LocalRotation = item.LocalRotation,
                    LocalStart = item.LocalStart, LocalArcUp = item.LocalArcUp });
        }

        public static bool TryFindClientItem(Ray ray, Vector3 playerPosition, float reach,
            out IPlayerInteractable target, out float rayDistance, out Vector3 point) =>
            LootStressPresentation.TryFind(ray, playerPosition, reach, out target, out rayDistance, out point);

        public static void SetFocusedClientItem(IPlayerInteractable target) =>
            LootStressPresentation.SetFocused(target);

        public static void ClearLocal()
        {
            LootStressPresentation.Clear();
            ServerItems.Clear();
            _latest = default;
            _hasState = false;
            _sceneName = null;
            _nextDynamicId = -1;
            OpeningChests.Clear();
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
                else if (item.Moved || item.SupportId != 0)
                {
                    UpdateItemPose(item);
                    Rpc(ecb, new LootStressDeltaCommand { Id = id, Kind = Place,
                        Position = item.Position, Rotation = item.Rotation, BaseRotation = item.BaseRotation,
                        OnWater = item.OnWater,
                        SupportId = item.SupportId, LocalPosition = item.LocalPosition, LocalRotation = item.LocalRotation,
                        LocalStart = item.LocalStart, LocalArcUp = item.LocalArcUp }, connection);
                }
            }
            foreach (var pair in ServerItems)
            {
                if (!pair.Value.Dynamic) continue;
                UpdateItemPose(pair.Value);
                Rpc(ecb, BuildAddDelta(pair.Key, pair.Value, 0f), connection);
                if (pair.Value.Hook != null)
                    Rpc(ecb, new LootStressDeltaCommand { Id = pair.Key, Kind = Tether,
                        HookOwner = pair.Value.Hook.OwnerClientId, HookOffset = pair.Value.HookOffset }, connection);
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

        private static void EnsureStateForCurrentScene(Vector3 center)
        {
            var scene = SceneManager.GetActiveScene().name;
            if (_hasState && _sceneName == scene) return;
            _latest = new LootStressCommand
            {
                Count = 0, Center = center, Radius = 15f,
                Seed = unchecked((uint)Environment.TickCount * 747796405u) | 1u,
                SceneName = new FixedString64Bytes(scene)
            };
            _sceneName = scene;
            _hasState = true;
            _stateRevision++;
            if (_stateRevision == 0) _stateRevision = 1;
            if (ClientServerBootstrap.ClientWorld is { IsCreated: true } clientWorld)
                LootStressPresentation.Apply(clientWorld, _latest);
        }

        private static LootStressDeltaCommand BuildAddDelta(int id, ServerItem item, float duration)
        {
            var start = duration > 0f ? item.FlightStart : item.Position;
            return new LootStressDeltaCommand
            {
                Id = id, Kind = Add, CatalogIndex = item.CatalogIndex,
                StartPosition = start, Position = item.RestPosition, Rotation = item.Rotation,
                BaseRotation = item.BaseRotation, ArcUp = item.ArcUp, Duration = duration,
                ArcHeight = duration > 0f ? item.ArcHeight : 0f, OnWater = item.OnWater,
                SupportId = item.SupportId, LocalPosition = item.LocalPosition, LocalRotation = item.LocalRotation,
                LocalStart = duration > 0f ? item.LocalStart : item.LocalPosition, LocalArcUp = item.LocalArcUp,
                IslandId = item.IslandId, FadeDuration = item.FadeDuration, OpeningAt = item.OpeningAt
            };
        }

        private static void ApplyDynamicItemsLocally()
        {
            foreach (var pair in ServerItems)
            {
                if (!pair.Value.Dynamic)
                {
                    if (pair.Value.SupportId != 0)
                    {
                        var placement = BuildAddDelta(pair.Key, pair.Value, 0f);
                        placement.Kind = Place;
                        LootStressPresentation.ApplyDelta(placement);
                    }
                    continue;
                }
                UpdateItemPose(pair.Value);
                LootStressPresentation.ApplyDelta(BuildAddDelta(pair.Key, pair.Value, 0f));
                if (pair.Value.Hook != null)
                    LootStressPresentation.ApplyDelta(new LootStressDeltaCommand
                    {
                        Id = pair.Key, Kind = Tether, HookOwner = pair.Value.Hook.OwnerClientId,
                        HookOffset = pair.Value.HookOffset
                    });
            }
        }

        private static void RebuildServerState()
        {
            var obsolete = new List<int>();
            foreach (var pair in ServerItems) if (!pair.Value.Dynamic) obsolete.Add(pair.Key);
            foreach (var id in obsolete) ServerItems.Remove(id);
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
                var surfaceId = 0UL;
                if (TryResolveSurface(spawn, out var point, out var normal, out onWater, out support, out surfaceId))
                {
                    rotation = Quaternion.FromToRotation(Vector3.up, normal) * baseRotation;
                    position = point + (onWater ? Vector3.up : normal) *
                        GetSurfaceClearance(definition, rotation, normal);
                }
                var waterOffset = GetSurfaceClearance(definition, rotation, Vector3.up);
                if (onWater) waterOffset = GetSurfaceClearance(definition, rotation, normal);
                var item = new ServerItem
                    { CatalogIndex = catalogIndex, Position = position, RestPosition = position, Rotation = rotation,
                        BaseRotation = baseRotation, OnWater = onWater,
                        WaterOffset = waterOffset, WaterSize = GetSurfaceSize(definition) };
                SetSupport(item, support, surfaceId);
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
            => TryResolveSurface(origin, out point, out normal, out onWater, out support, out _);

        internal static bool TryResolveSurface(Vector3 origin, out Vector3 point, out Vector3 normal,
            out bool onWater, out NetworkObject support, out ulong surfaceId)
        {
            point = origin;
            normal = Vector3.up;
            onWater = false;
            support = null;
            surfaceId = 0;
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
                var ship = hit.collider.GetComponentInParent<EnemyShipView>();
                surfaceId = ship != null ? DotsEnemyRuntime.ShipSurfaceKey(ship.ShipId) : 0;
            }
            if (_water != null && _water.TrySurface(origin, Vector2.one * 0.45f, out var height, out var waterNormal) &&
                height <= origin.y + 0.5f && height > bestY + 0.01f)
            {
                point = new Vector3(origin.x, height, origin.z);
                normal = waterNormal;
                onWater = true;
                support = null;
                surfaceId = 0;
                bestY = height;
            }
            return bestY > float.NegativeInfinity;
        }

        private static void SetSupport(ServerItem item, NetworkObject support, ulong surfaceId = 0)
        {
            item.Support = support;
            item.SupportId = surfaceId != 0 ? surfaceId : SupportWireId(support);
            if (!TrySupportFrame(item.SupportId, true, out var frame)) return;
            var inverse = frame.inverse;
            item.LocalPosition = inverse.MultiplyPoint3x4(item.RestPosition);
            item.LocalRotation = Quaternion.Inverse(frame.rotation) * item.Rotation;
            item.LocalStart = inverse.MultiplyPoint3x4(item.FlightStart);
            item.LocalArcUp = inverse.MultiplyVector(item.ArcUp);
            item.OnWater = false;
        }

        internal static bool TrySupportFrame(ulong id, bool physics, out Matrix4x4 frame)
        {
            frame = Matrix4x4.identity;
            return id != 0 && DotsEnemyRuntime.Instance != null &&
                DotsEnemyRuntime.Instance.TryGetSurfaceFrame(id, physics, out frame);
        }

        private static ulong SupportWireId(NetworkObject support) =>
            support != null ? support.NetworkObjectId + 1UL : 0UL;

        private static void UpdateSupportedPose(ServerItem item)
        {
            if (!TrySupportFrame(item.SupportId, true, out var frame)) return;
            item.RestPosition = frame.MultiplyPoint3x4(item.LocalPosition);
            item.Rotation = frame.rotation * item.LocalRotation;
            item.FlightStart = frame.MultiplyPoint3x4(item.LocalStart);
            item.ArcUp = frame.MultiplyVector(item.LocalArcUp);
        }

        private static void UpdateWaterPose(ServerItem item)
        {
            if (!item.OnWater || _water == null ||
                !_water.TrySurface(item.RestPosition, item.WaterSize, out var height, out var normal)) return;
            item.Rotation = Quaternion.FromToRotation(Vector3.up, normal) * item.BaseRotation;
            var definition = item.CatalogIndex >= 0 && item.CatalogIndex < _catalog.Items.Count
                ? _catalog.Items[item.CatalogIndex] : null;
            item.WaterOffset = GetSurfaceClearance(definition, item.Rotation, normal);
            item.RestPosition = new Vector3(item.RestPosition.x, height + item.WaterOffset, item.RestPosition.z);
        }

        private static void UpdateItemPose(ServerItem item)
        {
            UpdateSupportedPose(item);
            UpdateWaterPose(item);
            if (item.FlightDuration > 0f)
            {
                var elapsed = (float)(NetworkManager.Singleton.ServerTime.Time - item.FlightStarted);
                var t = Mathf.Clamp01(elapsed / item.FlightDuration);
                item.Position = Vector3.Lerp(item.FlightStart, item.RestPosition, t) +
                                item.ArcUp * (4f * t * (1f - t) * item.ArcHeight);
                if (t >= 1f) item.FlightDuration = 0f;
            }
            if (item.FlightDuration <= 0f) item.Position = item.RestPosition;
        }

        private static float BallisticFlightTime(float fallHeight, float verticalVelocity)
        {
            const float gravity = 9.81f;
            var discriminant = verticalVelocity * verticalVelocity + 2f * gravity * fallHeight;
            return discriminant > 0f ? Mathf.Max(0.12f, (verticalVelocity + Mathf.Sqrt(discriminant)) / gravity) : 0.45f;
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
    [UpdateBefore(typeof(LootStressLateJoinSystem))]
    public partial struct LootStressSnapshotRequestSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (request, entity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>()
                         .WithAll<LootStressSnapshotRequest>().WithEntityAccess())
            {
                if (LootStressTest.TryGetLatest(out _))
                    LootStressTest.WriteLateJoinState(ecb, request.ValueRO.SourceConnection);
                ecb.DestroyEntity(entity);
            }
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct LootStressLateJoinSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!LootStressTest.TryGetLatest(out _) || LootStressTest.StateRevision == 0) return;
            var revision = LootStressTest.StateRevision;
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (_, connection) in SystemAPI.Query<RefRO<NetworkId>>().WithAll<NetworkStreamInGame>()
                         .WithNone<NetworkStreamRequestDisconnect>().WithEntityAccess())
            {
                var hasState = state.EntityManager.HasComponent<LootStressConnectionState>(connection);
                if (hasState && state.EntityManager.GetComponentData<LootStressConnectionState>(connection)
                        .DeliveredRevision == revision)
                    continue;
                LootStressTest.WriteLateJoinState(ecb, connection);
                var delivered = new LootStressConnectionState { DeliveredRevision = revision };
                if (hasState) ecb.SetComponent(connection, delivered);
                else ecb.AddComponent(connection, delivered);
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
            // The world survives leaving/rejoining sessions. Request readiness belongs to the
            // connection (and loaded scene), never to a once-per-world bool.
            if (LootStressPresentation.AssetsReady)
            {
                var scene = new FixedString64Bytes(SceneManager.GetActiveScene().name);
                foreach (var (_, connection) in SystemAPI.Query<RefRO<NetworkId>>()
                             .WithAll<NetworkStreamInGame>().WithNone<NetworkStreamRequestDisconnect>().WithEntityAccess())
                {
                    var requested = state.EntityManager.HasComponent<LootSnapshotRequested>(connection);
                    if (requested && state.EntityManager.GetComponentData<LootSnapshotRequested>(connection)
                            .SceneName == scene) continue;
                    var request = ecb.CreateEntity();
                    ecb.AddComponent(request, new LootStressSnapshotRequest { ProtocolVersion = 1 });
                    ecb.AddComponent(request, new SendRpcCommandRequest { TargetConnection = connection });
                    var marker = new LootSnapshotRequested { SceneName = scene };
                    if (requested) ecb.SetComponent(connection, marker);
                    else ecb.AddComponent(connection, marker);
                }
            }
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
            LootStressPresentation.ApplyPending(state.World);
        }
    }

    [DefaultExecutionOrder(9800)]
    internal sealed class LootStressPresentationDriver : MonoBehaviour
    {
        private void LateUpdate()
        {
            LootStressPresentation.UpdateDynamicItems();
        }
        private void OnDestroy() => LootStressPresentation.DriverDestroyed(this);
    }

    internal static partial class LootStressPresentation
    {
        private static readonly Unity.Profiling.ProfilerMarker UpdateMarker = new("WaveByWave.Loot.Presentation");
        private static readonly Unity.Profiling.ProfilerMarker WaterMarker = new("WaveByWave.Loot.WaterSamples");
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
            public DotsLootRarityEffect RarityEffect;
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
            public Vector3 LocalStart, LocalArcUp;
            public Vector3 WaterTargetPosition;
            public Quaternion WaterTargetRotation;
            public Vector3 ArcUp;
            public float FlightDuration;
            public float ArcHeight;
            public ulong HookOwner = ulong.MaxValue;
            public Vector3 HookOffset;
            public DotsWorldItemPresentation DynamicPresentation;
            public int IslandId;
            public float FadeDuration;
            public double OpeningAt;
        }

        private sealed class StressInteractable : IPlayerInteractable
        {
            private readonly int _id;
            private readonly string _name;
            public StressInteractable(int id, string name) { _id = id; _name = name; }
            public int Id => _id;
            public string GetInteractionPrompt(NetworkPlayerController player) => $"Подобрать {_name} ×1";
            public void Interact(NetworkPlayerController player) => player?.Inventory?.PickupStressItem(_id);
        }

        private static readonly Dictionary<int, ClientItem> Items = new(LootStressTest.MaximumCount);
        private static readonly List<ClientItem> WaterItems = new(LootStressTest.MaximumCount);
        private static readonly List<LootStressDeltaCommand> PendingDeltas = new();
        private static ItemCatalog _catalog;
        private static GameObject _effectPrefab;
        private static EquipmentWaterQuery _water;
        private static World _world;
        private static LootStressCommand? _pending;
        private static int _waterCursor;
        private static string _activeScene;
        private static uint _appliedSeed;
        private static int _appliedCount;
        private static string _appliedScene;
        private static World _appliedWorld;
        private static LootStressPresentationDriver _driver;
        private static bool _hasInitialState;
        private static bool _flushingDeltas;
        internal static bool AssetsReady => _catalog != null;
        private static GameObject _prompt;
        private static Text _promptName;
        private static int _focusedId = int.MaxValue;
        private static int _focusedFrame = -1;
        private static WorldItem _focusedWorldItem;

        internal static void SetAssets(ItemCatalog catalog, GameObject effectPrefab, WaveProfile waterProfile)
        {
            _catalog = catalog;
            if (effectPrefab != null) _effectPrefab = effectPrefab;
            if (waterProfile != null && _water == null) _water = new EquipmentWaterQuery(waterProfile);
            EnsureDriver();
            if (ClientServerBootstrap.ClientWorld is { IsCreated: true } world) ApplyPending(world);
        }

        internal static void ApplyPending(World world)
        {
            if (_pending.HasValue && _catalog != null && world != null && world.IsCreated &&
                _pending.Value.SceneName.ToString() == SceneManager.GetActiveScene().name)
            {
                var command = _pending.Value;
                _pending = null;
                Apply(world, command);
            }
            if (!_pending.HasValue) FlushPendingDeltas();
        }

        internal static void Apply(World world, LootStressCommand command)
        {
            var sceneName = command.SceneName.ToString();
            if (_catalog == null || world == null || !world.IsCreated ||
                sceneName != SceneManager.GetActiveScene().name)
            {
                _pending = command;
                return;
            }
            _pending = null;
            if (command.Seed != 0 && command.Seed == _appliedSeed && command.Count == _appliedCount &&
                sceneName == _appliedScene && ReferenceEquals(world, _appliedWorld))
            {
                _hasInitialState = true;
                FlushPendingDeltas();
                return;
            }
            ClearContent(false);
            _appliedSeed = command.Seed;
            _appliedCount = command.Count;
            _appliedScene = sceneName;
            _appliedWorld = world;
            _activeScene = sceneName;
            _world = world;
            _hasInitialState = true;
            if (command.Count <= 0) { FlushPendingDeltas(); return; }
            var catalogIndices = LootStressTest.RenderableCatalogIndices();
            var variants = BuildVariants(catalogIndices);
            if (variants.Count == 0) return;
            var materials = new List<Material>();
            var meshes = new Mesh[variants.Count];
            var materialStart = new int[variants.Count];
            for (var i = 0; i < variants.Count; i++)
            {
                meshes[i] = variants[i].Mesh;
                materialStart[i] = materials.Count;
                materials.AddRange(variants[i].Materials);
            }
            var renderArray = new RenderMeshArray(materials.ToArray(), meshes);
            var description = new RenderMeshDescription(ShadowCastingMode.On, true,
                MotionVectorGenerationMode.Camera, LootSilhouetteRenderFeature.ItemLayer,
                uint.MaxValue, LightProbeUsage.BlendProbes);
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
                var surfaceId = 0UL;
                if (LootStressTest.TryResolveSurface(spawnPosition, out var point, out var normal,
                    out onWater, out support, out surfaceId))
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
                item.SupportId = surfaceId;
                SetSupport(item, support);
                item.WaterTargetPosition = restPosition;
                item.WaterTargetRotation = restRotation;
                var subMeshes = Mathf.Min(variant.Mesh.subMeshCount, variant.Materials.Length);
                for (ushort sub = 0; sub < subMeshes; sub++)
                {
                    var entity = world.EntityManager.CreateEntity(typeof(LocalToWorld), typeof(LootStressEntity));
                    RenderMeshUtility.AddComponents(entity, world.EntityManager, description, renderArray,
                        MaterialMeshInfo.FromRenderMeshArrayIndices(materialStart[variantIndex] + sub, variantIndex, sub));
                    item.Entities.Add(entity);
                }
                item.RarityEffect = DotsLootRarityEffect.Create(world, item.Definition.Rarity,
                    _effectPrefab, id + 1);
                Items.Add(id, item);
                if (onWater) WaterItems.Add(item);
                SetPose(item);
            }
            Debug.Log($"[Loot stress] Created {Items.Count} interactive DOTS items with prefab materials.");
            FlushPendingDeltas();
        }

        internal static void ApplyDelta(LootStressDeltaCommand delta)
        {
            if (_pending.HasValue || _catalog == null || _world == null || !_world.IsCreated || !_hasInitialState)
            {
                QueueDelta(delta);
                return;
            }
            if (!Items.TryGetValue(delta.Id, out var item))
            {
                if (delta.Kind != LootStressTest.Add)
                    return;
                if (delta.CatalogIndex < 0 || delta.CatalogIndex >= _catalog.Items.Count) return;
                var definition = _catalog.Items[delta.CatalogIndex];
                var variant = BuildVariant(delta.CatalogIndex);
                var presentation = DotsWorldItemPresentation.Create(definition, _effectPrefab, delta.Id + 1);
                if (definition == null || variant == null || presentation == null) return;
                item = new ClientItem
                {
                    Id = delta.Id, Definition = definition, Variant = variant,
                    Position = delta.StartPosition, SpawnPosition = delta.StartPosition,
                    RestPosition = delta.Position, Rotation = delta.Rotation, RestRotation = delta.Rotation,
                    BaseRotation = delta.BaseRotation, ArcUp = delta.ArcUp,
                    FlightDuration = delta.Duration, ArcHeight = delta.ArcHeight,
                    FallStarted = Time.timeAsDouble, Falling = delta.Duration > 0.001f,
                    OnWater = delta.OnWater, SupportId = delta.SupportId,
                    WaterTargetPosition = delta.Position, WaterTargetRotation = delta.Rotation,
                    DynamicPresentation = presentation, IslandId = delta.IslandId,
                    FadeDuration = delta.FadeDuration, OpeningAt = delta.OpeningAt
                };
                item.Support = ResolveSupport(delta.SupportId);
                SetReplicatedSupport(item, delta);
                Items.Add(delta.Id, item);
                if (item.OnWater) WaterItems.Add(item);
                SetPose(item);
                return;
            }
            if (delta.Kind == LootStressTest.ChestOpening) { item.OpeningAt = delta.OpeningAt; return; }
            if (delta.Kind == LootStressTest.ChestBurst)
            { PlayChestBurst(item); Destroy(item); Items.Remove(delta.Id); return; }
            if (delta.Kind == LootStressTest.Remove) { Destroy(item); Items.Remove(delta.Id); return; }
            if (delta.Kind == LootStressTest.Add)
            {
                // Reliable RPC retries and host-local immediate presentation are deliberately idempotent.
                return;
            }
            if (delta.Kind == LootStressTest.Tether)
            {
                item.IslandId = 0;
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
            SetReplicatedSupport(item, delta);
            if (item.OnWater != delta.OnWater)
            {
                WaterItems.Remove(item);
                item.OnWater = delta.OnWater;
                if (item.OnWater) WaterItems.Add(item);
            }
            SetPose(item);
        }

        private static void QueueDelta(LootStressDeltaCommand delta)
        {
            // A reliable NFE Add may arrive before the initial snapshot/client presentation world.
            // Retain it until scene/catalog readiness; session shutdown clears the queue.
            PendingDeltas.Add(delta);
        }

        private static void FlushPendingDeltas()
        {
            if (_flushingDeltas || !_hasInitialState || _catalog == null || _world == null || !_world.IsCreated ||
                PendingDeltas.Count == 0) return;
            _flushingDeltas = true;
            var pending = PendingDeltas.ToArray();
            PendingDeltas.Clear();
            // Snapshot Add commands establish identity. Apply them before any queued tether/place/remove
            // commands in case separate NFE receive batches crossed client-world initialization.
            for (var i = 0; i < pending.Length; i++)
                if (pending[i].Kind == LootStressTest.Add) ApplyDelta(pending[i]);
            for (var i = 0; i < pending.Length; i++)
                if (pending[i].Kind != LootStressTest.Add) ApplyDelta(pending[i]);
            _flushingDeltas = false;
        }

        internal static void UpdateDynamicItems()
        {
            if (_world == null || !_world.IsCreated) return;
            using var presentationScope = UpdateMarker.Auto();
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
                    var supported = LootStressTest.TrySupportFrame(item.SupportId, false, out var supportFrame);
                    if (supported)
                    {
                        item.RestPosition = supportFrame.MultiplyPoint3x4(item.LocalPosition);
                        item.RestRotation = supportFrame.rotation * item.LocalRotation;
                        item.SpawnPosition = supportFrame.MultiplyPoint3x4(item.LocalStart);
                        item.ArcUp = supportFrame.MultiplyVector(item.LocalArcUp);
                    }
                    if (item.Falling)
                    {
                        var elapsed = Mathf.Max(0f, (float)(Time.timeAsDouble - item.FallStarted));
                        if (item.FlightDuration > 0.001f)
                        {
                            var t = Mathf.Clamp01(elapsed / item.FlightDuration);
                            item.Position = Vector3.Lerp(item.SpawnPosition, item.RestPosition, t) +
                                            item.ArcUp * (4f * t * (1f - t) * item.ArcHeight);
                            if (t >= 1f) item.Falling = false;
                        }
                        else
                        {
                            item.Position = item.RestPosition;
                            item.Position.y = Mathf.Max(item.RestPosition.y,
                                item.SpawnPosition.y - 0.5f * 18f * elapsed * elapsed);
                            if (item.Position.y <= item.RestPosition.y + 0.001f) item.Falling = false;
                        }
                        if (!item.Falling)
                        {
                            item.Position = item.RestPosition;
                            item.Rotation = item.RestRotation;
                        }
                        SetPose(item);
                    }
                    else if (supported)
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
                if (item.OpeningAt > 0d || item.FadeDuration > 0f) UpdateChestAndFade(item);
            }
            UpdateWaterItems();
            UpdateFocusedPrompt();
        }

        internal static void SetFocused(IPlayerInteractable target)
        {
            _focusedId = target is StressInteractable stress ? stress.Id : int.MaxValue;
            _focusedWorldItem = target as WorldItem;
            _focusedFrame = Time.frameCount;
        }

        internal static bool TryFind(Ray ray, Vector3 playerPosition, float reach,
            out IPlayerInteractable target, out float rayDistance, out Vector3 point)
        {
            target = null; rayDistance = float.PositiveInfinity; point = default;
            foreach (var item in Items.Values)
            {
                if (item.HookOwner != ulong.MaxValue || item.OpeningAt > 0d ||
                    (item.Position - playerPosition).sqrMagnitude > reach * reach ||
                    !LootStressTest.IsExposed(item.IslandId, item.Position)) continue;
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

        internal static void Clear() => ClearContent(true);

        private static void ClearContent(bool resetAppliedCommand)
        {
            foreach (var item in Items.Values) Destroy(item);
            Items.Clear();
            WaterItems.Clear();
            _waterCursor = 0;
            if (_prompt != null) _prompt.SetActive(false);
            _world = null;
            _activeScene = null;
            if (!resetAppliedCommand) return;
            PendingDeltas.Clear();
            _pending = null;
            _hasInitialState = false;
            _appliedSeed = 0;
            _appliedCount = 0;
            _appliedScene = null;
            _appliedWorld = null;
        }

        internal static void ClearScene(string sceneName)
        {
            if (string.IsNullOrEmpty(_activeScene) || _activeScene != sceneName) return;
            // A snapshot for the destination scene can arrive before NGO unloads the old one.
            // Keep that snapshot and its deltas across this unload.
            if (_pending.HasValue && _pending.Value.SceneName.ToString() != sceneName)
            {
                ClearContent(false);
                _hasInitialState = false;
                _appliedSeed = 0;
                _appliedWorld = null;
            }
            else Clear();
        }

        private static void EnsureDriver()
        {
            if (_driver != null) return;
            var host = new GameObject("DOTS Loot Presentation") { hideFlags = HideFlags.HideInHierarchy };
            UnityEngine.Object.DontDestroyOnLoad(host);
            _driver = host.AddComponent<LootStressPresentationDriver>();
        }

        private static void EnsurePrompt()
        {
            if (_prompt != null) return;
            _prompt=GameUiPrefabs.Create("World/ItemTooltip");
            if(_prompt!=null)
            {
                GameUiPrefabs.Persist(_prompt);
                _promptName=GameUiPrefabs.Find<Text>(_prompt,"Label");
                return;
            }
            _prompt = new GameObject("Focused Item Prompt", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(Image));
            if(Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(_prompt);
            var rect = (RectTransform)_prompt.transform;
            rect.sizeDelta = new Vector2(350f, 76f);
            rect.localScale = Vector3.one * 0.0032f;
            var canvas = _prompt.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 80;
            var background = _prompt.GetComponent<Image>();
            background.color = new Color(0.025f, 0.035f, 0.05f, 0.9f);
            background.raycastTarget = false;

            var keyObject = new GameObject("Interaction Key", typeof(RectTransform), typeof(Image));
            keyObject.transform.SetParent(_prompt.transform, false);
            var keyRect = (RectTransform)keyObject.transform;
            keyRect.anchorMin = keyRect.anchorMax = new Vector2(0f, 0.5f);
            keyRect.pivot = new Vector2(0f, 0.5f);
            keyRect.anchoredPosition = new Vector2(8f, 0f);
            keyRect.sizeDelta = new Vector2(40f, 40f);
            keyObject.GetComponent<Image>().color = new Color(0.9f, 0.92f, 0.95f, 0.98f);
            var keyText = CreatePromptText("E", keyObject.transform, Color.black, 22, TextAnchor.MiddleCenter);
            Stretch(keyText.rectTransform, 0f);

            _promptName = CreatePromptText("Item", _prompt.transform, Color.white, 20, TextAnchor.MiddleLeft);
            var nameRect = _promptName.rectTransform;
            nameRect.anchorMin = Vector2.zero;
            nameRect.anchorMax = Vector2.one;
            nameRect.offsetMin = new Vector2(60f, 3f);
            nameRect.offsetMax = new Vector2(-8f, -3f);
            _prompt.SetActive(false);
        }

        private static Text CreatePromptText(string value, Transform parent, Color color, int size,
            TextAnchor alignment)
        {
            var child = new GameObject("Label", typeof(RectTransform), typeof(Text));
            child.transform.SetParent(parent, false);
            var label = child.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.text = value;
            label.fontSize = size;
            label.fontStyle = FontStyle.Bold;
            label.alignment = alignment;
            label.color = color;
            label.raycastTarget = false;
            return label;
        }

        private static void Stretch(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.one * inset;
            rect.offsetMax = Vector2.one * -inset;
        }

        private static void UpdateFocusedPrompt()
        {
            EnsurePrompt();
            ClientItem item = null;
            var loose = Items.TryGetValue(_focusedId, out item) && item.HookOwner == ulong.MaxValue;
            var world = _focusedWorldItem != null && _focusedWorldItem.IsSpawned;
            var definition = loose ? item.Definition : world ? _focusedWorldItem.Definition : null;
            var visible = _focusedFrame == Time.frameCount && definition != null && Camera.main != null && !PlayerEquipment.InputCaptured;
            _prompt.SetActive(visible);
            if (!visible) return;
            var camera = Camera.main;
            var position = loose ? item.Position + Vector3.up * Mathf.Max(0.45f, item.Variant.Radius + 0.22f)
                : _focusedWorldItem.transform.position + Vector3.up * .55f;
            _prompt.transform.SetPositionAndRotation(position, camera.transform.rotation);
            var localPlayer = NetworkManager.Singleton != null && NetworkManager.Singleton.LocalClient?.PlayerObject != null
                ? NetworkManager.Singleton.LocalClient.PlayerObject.GetComponent<NetworkPlayerController>() : null;
            _promptName.text = definition.HoverDescription(localPlayer) +
                (definition.IsChest ? "\n<size=12>E — взять · удерживай E — открыть</size>" : "");
            _promptName.color = definition.RarityColor;
            UpdateChestProgress(loose ? _focusedId : int.MaxValue);
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
                var variant = BuildVariant(index);
                if (variant != null) result.Add(variant);
            }
            return result;
        }

        private static Variant BuildVariant(int index)
        {
            if (_catalog == null || index < 0 || index >= _catalog.Items.Count ||
                !LootStressTest.TryGetVisual(_catalog.Items[index], out var filter, out var renderer)) return null;
            var matrix = filter.transform.localToWorldMatrix;
            var scale = matrix.lossyScale;
            var extents = Vector3.Scale(filter.sharedMesh.bounds.extents,
                new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            var bounds = filter.sharedMesh.bounds;
            var corners = new Vector3[8];
            for (var corner = 0; corner < corners.Length; corner++)
                corners[corner] = matrix.MultiplyPoint3x4(bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f,
                        (corner & 4) == 0 ? -1f : 1f)));
            return new Variant { Mesh = filter.sharedMesh, Materials = renderer.sharedMaterials,
                Offset = matrix.GetColumn(3), Rotation = matrix.rotation, Scale = scale,
                Radius = Mathf.Clamp(extents.magnitude, 0.22f, 1.2f), Corners = corners,
                SurfaceSize = new Vector2(Mathf.Clamp(bounds.size.x * Mathf.Abs(scale.x), 0.15f, 1.5f),
                    Mathf.Clamp(bounds.size.z * Mathf.Abs(scale.z), 0.15f, 1.5f)) };
        }

        private static void SetPose(ClientItem item)
        {
            if (_world == null || !_world.IsCreated) return;
            if (item.DynamicPresentation != null)
            {
                item.DynamicPresentation.SetPose(item.Position, item.Rotation);
                return;
            }
            var position = item.Position + item.Rotation * item.Variant.Offset;
            var rotation = item.Rotation * item.Variant.Rotation;
            foreach (var entity in item.Entities)
            {
                _world.EntityManager.SetComponentData(entity,
                    new LocalToWorld { Value = float4x4.TRS(position, rotation, item.Variant.Scale) });
            }
            item.RarityEffect?.SetPose(item.Position);
        }

        private static void UpdateWaterItems()
        {
            if (_water == null || WaterItems.Count == 0) return;
            using var waterScope = WaterMarker.Auto();
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
            if (!LootStressTest.TrySupportFrame(item.SupportId, false, out var frame)) return;
            item.LocalPosition = frame.inverse.MultiplyPoint3x4(item.RestPosition);
            item.LocalRotation = Quaternion.Inverse(frame.rotation) * item.RestRotation;
            item.LocalStart = frame.inverse.MultiplyPoint3x4(item.SpawnPosition);
            item.LocalArcUp = frame.inverse.MultiplyVector(item.ArcUp);
            item.OnWater = false;
        }

        private static void SetReplicatedSupport(ClientItem item, LootStressDeltaCommand delta)
        {
            // Never derive local coordinates from a delayed world-space packet and today's
            // ship transform. These values belong to the server's original support frame.
            item.LocalPosition = delta.LocalPosition;
            item.LocalRotation = delta.LocalRotation;
            item.LocalStart = delta.LocalStart;
            item.LocalArcUp = delta.LocalArcUp;
            if (item.SupportId != 0) item.OnWater = false;
        }

        private static void Destroy(ClientItem item)
        {
            WaterItems.Remove(item);
            item.DynamicPresentation?.Dispose();
            item.DynamicPresentation = null;
            if (_world != null && _world.IsCreated)
            {
                foreach (var entity in item.Entities) if (_world.EntityManager.Exists(entity)) _world.EntityManager.DestroyEntity(entity);
            }
            item.RarityEffect?.Dispose();
            item.RarityEffect = null;
        }
    }
}
