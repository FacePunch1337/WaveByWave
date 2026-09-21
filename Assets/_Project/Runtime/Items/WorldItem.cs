using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using WaveByWave.Player;
using WaveByWave.Enemies;

namespace WaveByWave.Items
{
    [DefaultExecutionOrder(3000)]
    [RequireComponent(typeof(NetworkObject), typeof(Collider))]
    public sealed class WorldItem : NetworkBehaviour, IPlayerInteractable
    {
        [SerializeField] private ItemCatalog catalog;
        [SerializeField, Tooltip("Префаб-источник материала свечения. Он не создаётся в игре: луч и искры отрисовываются через DOTS.")]
        private GameObject rarityEffectPrefab;
        [SerializeField] private string initialItemId = "cannonball";
        [SerializeField, Min(1)] private int initialAmount = 1;
        [SerializeField, Min(0.05f)] private float throwDuration = 0.45f;
        [SerializeField, Min(0f)] private float throwArcHeight = 0.25f;
        [SerializeField, Min(0f)] private float dropDistance = 0.9f;
        [SerializeField, Min(0.1f), Tooltip("Горизонтальная скорость броска. Чем выше точка, тем дальше предмет пролетит до поверхности.")]
        private float throwForwardSpeed = 4.5f;
        [SerializeField] private LayerMask placementLayers = ~0;
        private readonly NetworkVariable<FixedString64Bytes> _itemId = new();
        private readonly NetworkVariable<ushort> _amount = new(1);
        private readonly NetworkVariable<WorldItemPlacement> _placement = new();
        private readonly NetworkVariable<WorldItemTetherState> _tether = new();
        public static readonly HashSet<WorldItem> ActiveItems = new();
        private static readonly RaycastHit[] PlacementHits = new RaycastHit[128];
        private NetworkObject _support;
        private PlatformNetworkTransform _supportMotion;
        private Bounds _modelBounds;
        private DotsWorldItemPresentation _visual;
        private EquipmentWaterQuery _water;
        private bool _waterPoseInitialized;

        public FixedString64Bytes ItemId => _itemId.Value;
        public ushort Amount => _amount.Value;
        public ItemDefinition Definition => catalog != null && catalog.TryGet(ItemId.ToString(), out var item) ? item : null;
        public GameObject RarityEffectPrefab => rarityEffectPrefab;

        private void Awake()
        {
            // Rotation/scale authored on the item prefab describe its presentation. The
            // NetworkObject root stays unit-scaled so placement, colliders and networking
            // are not distorted; ItemVisualUtility applies those values to the visual clone.
            transform.localScale = Vector3.one;
            // The collider is only a pickup target; it never pushes a character or ship.
            GetComponent<Collider>().isTrigger = true;
        }

        public override void OnNetworkSpawn()
        {
            ActiveItems.Add(this);
            if (IsServer && _itemId.Value.IsEmpty)
            {
                var maximum = catalog != null && catalog.TryGet(initialItemId, out var definition)
                    ? definition.MaximumStack : 1;
                SetState(new FixedString64Bytes(initialItemId), (ushort)Mathf.Clamp(initialAmount, 1, maximum));
            }
            RefreshModelBounds();
            if (IsServer && !_placement.Value.Initialized)
                PreparePlacement(transform.position, transform.forward, null, false);
            _itemId.OnValueChanged += OnItemChanged;
            _placement.OnValueChanged += OnPlacementChanged;
            UpdateVisual();
            ApplyPresentationPose();
        }
        public override void OnNetworkDespawn()
        {
            ActiveItems.Remove(this);
            _itemId.OnValueChanged -= OnItemChanged;
            _placement.OnValueChanged -= OnPlacementChanged;
            _support = null;
            _supportMotion = null;
            _water?.Dispose();
            _water = null;
            _visual?.Dispose();
            _visual = null;
        }
        public override void OnDestroy()
        {
            _water?.Dispose();
            _water = null;
            _visual?.Dispose();
            _visual = null;
            base.OnDestroy();
        }
        private void OnItemChanged(FixedString64Bytes previous, FixedString64Bytes current)
        {
            RefreshModelBounds();
            UpdateVisual();
        }
        private void OnPlacementChanged(WorldItemPlacement previous, WorldItemPlacement current)
        {
            _support = null;
            _supportMotion = null;
            _waterPoseInitialized = false;
            ApplyPresentationPose();
        }
        private void LateUpdate()
        {
            if (IsSpawned) ApplyPresentationPose();
        }

        private NetworkObject ResolveSupport()
        {
            if (!_placement.Value.HasSupport || _placement.Value.SurfaceId != 0) return null;
            if (_support != null && _support.IsSpawned) return _support;
            if (!_placement.Value.Support.TryGet(out _support, NetworkManager)) return null;
            _supportMotion = _support.GetComponent<PlatformNetworkTransform>();
            return _support;
        }

        private bool TryGetSupportFrame(bool physics, out Matrix4x4 frame)
        {
            frame = Matrix4x4.identity;
            if (_placement.Value.SurfaceId != 0)
                return DotsEnemyRuntime.Instance != null && DotsEnemyRuntime.Instance.TryGetSurfaceFrame(
                    _placement.Value.SurfaceId, physics, out frame);
            var support = ResolveSupport();
            if (support == null) return false;
            frame = physics ? GetPhysicsFrame(support) : support.transform.localToWorldMatrix;
            return true;
        }

        private void ApplyPresentationPose()
        {
            if (TryResolveTether(out var hook))
            {
                if (hook.Hook.Phase != HookPhase.Stowed)
                    SetPose(hook.RenderedHookPosition + _tether.Value.Offset, transform.rotation);
                return;
            }
            // Updates for two NetworkObjects may arrive in different frames. Keep the
            // last pose until the server's release placement arrives, never snap to zero.
            if (_tether.Value.Active && !IsServer) return;
            var state = _placement.Value;
            if (!state.Initialized) return;
            var hasSupport = TryGetSupportFrame(false, out var supportFrame);
            var now = !IsServer && _supportMotion != null
                ? _supportMotion.PresentationServerTime : NetworkManager.ServerTime.Time;
            if (hasSupport)
                SetPose(supportFrame.MultiplyPoint3x4(state.Evaluate(now)),
                    supportFrame.rotation * state.Rotation);
            else if (state.HasSupport)
                SetPose(state.FallbackPosition, state.FallbackRotation);
            else if (state.OnWater && now >= state.Started + state.Duration &&
                TryWaterSurface(state.End, out var waterHeight, out var waterNormal))
            {
                var rotation = Quaternion.FromToRotation(Vector3.up, waterNormal) * state.Rotation;
                var position = state.End;
                position.y = waterHeight + GetSurfaceClearance(rotation, waterNormal) + 0.005f;
                var blend = 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime);
                if (_waterPoseInitialized)
                {
                    position = Vector3.Lerp(transform.position, position, blend);
                    rotation = Quaternion.Slerp(transform.rotation, rotation, blend);
                }
                _waterPoseInitialized = true;
                SetPose(position, rotation);
            }
            else
            {
                _waterPoseInitialized = false;
                SetPose(state.Evaluate(now), state.Rotation);
            }
        }

        private void SetPose(Vector3 position, Quaternion rotation)
        {
            // A settled object on static ground must not dirty its trigger every frame.
            if (transform.position.Equals(position) && transform.rotation.Equals(rotation)) return;
            transform.SetPositionAndRotation(position, rotation);
            _visual?.SetPose(position, rotation);
        }

        public NetworkObject SupportingObject => ResolveSupport();

        // Interaction checks use the same authoritative frame as the player,
        // while rendering uses the ship's final interpolated pose each frame.
        public Vector3 GetServerPosition()
        {
            if (TryResolveTether(out var hook)) return hook.ServerHookPosition + _tether.Value.Offset;
            var state = _placement.Value;
            if (!state.Initialized) return transform.position;
            var hasSupport = TryGetSupportFrame(true, out var supportFrame);
            var position = state.Evaluate(NetworkManager.ServerTime.Time);
            if (state.OnWater && NetworkManager.ServerTime.Time >= state.Started + state.Duration &&
                TryWaterSurface(state.End, out var waterHeight, out var waterNormal))
            {
                var rotation = Quaternion.FromToRotation(Vector3.up, waterNormal) * state.Rotation;
                position = state.End;
                position.y = waterHeight + GetSurfaceClearance(rotation, waterNormal) + 0.005f;
            }
            return hasSupport ? supportFrame.MultiplyPoint3x4(position)
                : state.HasSupport ? state.FallbackPosition : position;
        }

        private bool TryResolveTether(out PlayerEquipment equipment)
        {
            equipment = null;
            return _tether.Value.Active && _tether.Value.Player.TryGet(out var player, NetworkManager) &&
                player.TryGetComponent(out equipment) && equipment.IsSpawned;
        }

        public bool IsTetheredTo(PlayerEquipment hook) => _tether.Value.Active && hook != null &&
            _tether.Value.Player.Equals(hook.TetherReference);

        public void CaptureWithHookServer(PlayerEquipment hook, Vector3 offset)
        {
            if (!IsServer || hook == null || !hook.IsSpawned) return;
            // Replacing this single owner transfers the item immediately, including between players.
            _tether.Value = new WorldItemTetherState
            { Active = true, Player = hook.TetherReference, Offset = offset };
        }

        public void ReleaseFromHookServer(PlayerEquipment hook, Vector3 position, NetworkObject support, bool onWater = false)
        {
            if (!IsServer || !IsTetheredTo(hook)) return;
            _tether.Value = default;
            RefreshModelBounds();
            if (onWater)
            {
                var rotation = Definition != null ? Definition.RestingRotation : Quaternion.identity;
                var end = position + Vector3.up * (GetSurfaceClearance(rotation, Vector3.up) + 0.005f);
                _placement.Value = new WorldItemPlacement { Initialized = true, OnWater = true, Start = end, End = end,
                    Rotation = rotation, FallbackRotation = rotation, FallbackPosition = end, ArcUp = Vector3.up };
                return;
            }
            PreparePlacement(position, Vector3.forward, support, false);
        }

        public bool PrepareDrop(Vector3 feet, Vector3 direction, NetworkObject preferredSupport)
        {
            if (IsSpawned && !IsServer) return false;
            RefreshModelBounds();
            return PreparePlacement(feet, direction, preferredSupport, true);
        }

        // PhysX is queried only to locate the surface once, never to simulate loot.
        private bool PreparePlacement(Vector3 feet, Vector3 direction, NetworkObject preferredSupport, bool thrown)
        {
            var frame = GetPhysicsFrame(preferredSupport);
            var up = preferredSupport != null ? frame.MultiplyVector(Vector3.up).normalized : Vector3.up;
            var aim = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
            var planarAim = Vector3.ProjectOnPlane(aim, up);
            var planarAmount = Mathf.Clamp01(planarAim.magnitude);
            var forward = planarAim.normalized;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
            var verticalVelocity = thrown ? Vector3.Dot(aim, up) * throwForwardSpeed : 0f;
            var horizontalSpeed = thrown ? throwForwardSpeed * planarAmount : 0f;
            var start = thrown ? feet + up * 0.9f + aim * 0.25f : feet;
            var target = feet + forward * (thrown ? dropDistance * Mathf.Max(0.15f, planarAmount) : 0f);
            var found = TryFindSurface(target, up, out var hit);
            var onWater = TryWaterSurface(target, out var waterHeight, out var waterNormal) &&
                waterHeight <= target.y + 1.5f && (!found || waterHeight > hit.point.y + 0.01f);
            if (thrown && (found || onWater))
            {
                var firstHeight = onWater ? waterHeight : hit.point.y;
                var fallTime = BallisticFlightTime(Vector3.Dot(start - new Vector3(target.x, firstHeight, target.z), up),
                    verticalVelocity);
                var distance = dropDistance * Mathf.Max(0.15f, planarAmount) + horizontalSpeed * fallTime;
                target = feet + forward * distance;
                found = TryFindSurface(target, up, out hit);
                onWater = TryWaterSurface(target, out waterHeight, out waterNormal) &&
                    waterHeight <= target.y + 1.5f && (!found || waterHeight > hit.point.y + 0.01f);
                if (found || onWater)
                {
                    var resolvedHeight = onWater ? waterHeight : hit.point.y;
                    var resolvedFallTime = BallisticFlightTime(
                        Vector3.Dot(start - new Vector3(target.x, resolvedHeight, target.z), up), verticalVelocity);
                    var resolvedDistance = dropDistance * Mathf.Max(0.15f, planarAmount) +
                        horizontalSpeed * resolvedFallTime;
                    if (resolvedDistance > distance + 0.1f)
                    {
                        target = feet + forward * resolvedDistance;
                        found = TryFindSurface(target, up, out hit);
                        onWater = TryWaterSurface(target, out waterHeight, out waterNormal) &&
                            waterHeight <= target.y + 1.5f && (!found || waterHeight > hit.point.y + 0.01f);
                    }
                }
            }
            // Without a surface keep the inventory intact instead of stranding loot in midair.
            if (thrown && !found && !onWater) return false;
            var point = onWater ? new Vector3(target.x, waterHeight, target.z) : found ? hit.point : target;
            var normal = onWater ? waterNormal : found ? hit.normal : up;
            var support = !onWater && found ? hit.collider.GetComponentInParent<NetworkObject>() : preferredSupport;
            if (onWater) support = null;
            if (support != null && !support.IsSpawned) support = null;
            var surfaceId = 0UL;
            if (support == null && !onWater && found)
            {
                var enemyShip = hit.collider.GetComponentInParent<EnemyShipView>();
                if (enemyShip != null) surfaceId = DotsEnemyRuntime.ShipSurfaceKey(enemyShip.ShipId);
            }
            var rotationUp = onWater ? Vector3.up : normal;
            var facing = Vector3.ProjectOnPlane(forward, rotationUp).normalized;
            if (facing.sqrMagnitude < 0.01f) facing = Vector3.Cross(rotationUp, Vector3.right).normalized;
            var definition = Definition;
            var modelRotation = definition != null ? definition.RestingRotation : Quaternion.identity;
            if (definition != null && definition.WorldVisualPrefab == null &&
                (definition.Category == ItemCategory.Weapon || definition.Category == ItemCategory.Tool))
                modelRotation *= Quaternion.Euler(90f, 0f, 0f);
            var rotation = Quaternion.LookRotation(facing, rotationUp) * modelRotation;
            var surfaceRotation = onWater ? Quaternion.FromToRotation(Vector3.up, normal) * rotation : rotation;
            var clearance = GetSurfaceClearance(surfaceRotation, normal);
            var end = point + (onWater ? Vector3.up : normal) * (clearance + 0.005f);
            if (!thrown) start = end;
            var fallHeight = Vector3.Dot(start - end, up);
            var fallDuration = BallisticFlightTime(fallHeight, verticalVelocity);
            var flightDuration = thrown ? Mathf.Clamp(fallDuration > 0.01f ? fallDuration : throwDuration, 0.12f, 3f) : 0f;
            var gravityArc = Mathf.Max(0f, verticalVelocity * flightDuration + fallHeight) * 0.25f;
            var decorativeArc = throwArcHeight * Mathf.Clamp01(1f - Mathf.Abs(Vector3.Dot(aim, up)) * 2f);
            var state = new WorldItemPlacement
            {
                Initialized = true, HasSupport = support != null || surfaceId != 0, OnWater = onWater,
                Support = support != null ? new NetworkObjectReference(support) : default,
                SurfaceId = surfaceId,
                Start = start, End = end, ArcUp = up, Rotation = rotation,
                FallbackPosition = end, FallbackRotation = rotation,
                Started = Unity.Netcode.NetworkManager.Singleton.ServerTime.Time,
                Duration = flightDuration,
                ArcHeight = thrown ? decorativeArc + gravityArc : 0f
            };
            var placementFrame = GetPhysicsFrame(support);
            if (surfaceId != 0 && DotsEnemyRuntime.Instance != null)
                DotsEnemyRuntime.Instance.TryGetSurfaceFrame(surfaceId, true, out placementFrame);
            if (state.HasSupport)
            {
                var inverse = placementFrame.inverse;
                var supportRotation = placementFrame.rotation;
                state.Start = inverse.MultiplyPoint3x4(start);
                state.End = inverse.MultiplyPoint3x4(end);
                state.ArcUp = inverse.MultiplyVector(up);
                state.Rotation = Quaternion.Inverse(supportRotation) * rotation;
            }
            _placement.Value = state;
            transform.SetPositionAndRotation(start, rotation);
            return true;
        }

        private static float BallisticFlightTime(float fallHeight, float verticalVelocity)
        {
            const float gravity = 9.81f;
            var discriminant = verticalVelocity * verticalVelocity + 2f * gravity * fallHeight;
            if (discriminant <= 0f) return 0f;
            return Mathf.Max(0f, (verticalVelocity + Mathf.Sqrt(discriminant)) / gravity);
        }

        public static Matrix4x4 GetPhysicsFrame(NetworkObject support)
        {
            if (support == null) return Matrix4x4.identity;
            if (support.TryGetComponent<MovingPlatform>(out var platform) && platform.UsesKccMover)
                return Matrix4x4.TRS(platform.KccMover.TransientPosition,
                    platform.KccMover.TransientRotation, support.transform.lossyScale);
            return support.TryGetComponent<Rigidbody>(out var body)
                ? Matrix4x4.TRS(body.position, body.rotation, support.transform.lossyScale)
                : support.transform.localToWorldMatrix;
        }

        private static Quaternion GetPhysicsRotation(NetworkObject support) =>
            GetPhysicsFrame(support).rotation;

        private bool TryFindSurface(Vector3 target, Vector3 up, out RaycastHit result)
        {
            result = default;
            var count = UnityEngine.Physics.RaycastNonAlloc(target + up * 0.7f, -up, PlacementHits,
                256f, placementLayers, QueryTriggerInteraction.Ignore);
            var hits = PlacementHits;
            if (count == PlacementHits.Length)
            {
                hits = UnityEngine.Physics.RaycastAll(target + up * 0.7f, -up, 256f,
                    placementLayers, QueryTriggerInteraction.Ignore);
                count = hits.Length;
            }
            var distance = float.PositiveInfinity;
            for (var i = 0; i < count; i++)
            {
                var hit = hits[i];
                if (hit.distance >= distance || Vector3.Dot(hit.normal, up) < 0.3f ||
                    hit.collider.GetComponentInParent<NetworkPlayerController>() != null ||
                    hit.collider.GetComponentInParent<WorldItem>() != null) continue;
                if (hit.rigidbody != null && !hit.rigidbody.isKinematic) continue;
                distance = hit.distance;
                result = hit;
            }
            return distance < float.PositiveInfinity;
        }

        private bool TryWaterSurface(Vector3 point, out float height, out Vector3 normal)
        {
            if (_water == null && LootStressTest.WaterProfile != null)
                _water = new EquipmentWaterQuery(LootStressTest.WaterProfile);
            var size = new Vector2(Mathf.Clamp(_modelBounds.size.x, 0.15f, 1.5f),
                Mathf.Clamp(_modelBounds.size.z, 0.15f, 1.5f));
            if (_water != null) return _water.TrySurface(point, size, out height, out normal);
            height = 0f;
            normal = Vector3.up;
            return false;
        }

        private void RefreshModelBounds()
        {
            var definition = Definition;
            var size = GetFallbackModelSize(definition);
            _modelBounds = new Bounds(Vector3.zero, size);
            if (definition != null && definition.WorldVisualPrefab != null)
            {
                var boundsRoot = ItemVisualUtility.GetBoundsRoot(definition.WorldVisualPrefab);
                if (boundsRoot == null) return;
                var initialized = false;
                foreach (var filter in boundsRoot.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null) continue;
                    var bounds = filter.sharedMesh.bounds;
                    // Prefab asset transforms already contain both the item root pose and
                    // every authored child override. Use them exactly as the presentation does.
                    var matrix = filter.transform.localToWorldMatrix;
                    for (var corner = 0; corner < 8; corner++)
                    {
                        var point = matrix.MultiplyPoint3x4(BoundsCorner(bounds, corner));
                        if (!initialized) { _modelBounds = new Bounds(point, Vector3.zero); initialized = true; }
                        else _modelBounds.Encapsulate(point);
                    }
                }
            }
            if (TryGetComponent<BoxCollider>(out var pickup))
            {
                pickup.center = _modelBounds.center;
                pickup.size = Vector3.Max(_modelBounds.size, Vector3.one * 0.2f);
            }
        }

        private static Vector3 GetFallbackModelSize(ItemDefinition definition) =>
            definition != null && definition.SupplyKind == SupplyKind.Plank ? new Vector3(1.8f, 0.18f, 0.45f)
            : definition != null && definition.Category == ItemCategory.Weapon ? new Vector3(0.2f, 1.8f, 0.25f)
            : Vector3.one * 0.75f;

        private static Vector3 BoundsCorner(Bounds bounds, int corner) => bounds.center + Vector3.Scale(bounds.extents,
            new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));

        private float GetSurfaceClearance(Quaternion rotation, Vector3 normal)
        {
            var minimum = float.PositiveInfinity;
            for (var corner = 0; corner < 8; corner++)
                minimum = Mathf.Min(minimum, Vector3.Dot(rotation * Vector3.Scale(BoundsCorner(_modelBounds, corner),
                    transform.lossyScale), normal));
            return -minimum;
        }
        private void UpdateVisual()
        {
            if (!IsClient) return;
            var definition = Definition;
            if (definition == null) return;
            _visual?.Dispose();
            _visual = null;
            var original = GetComponent<Renderer>();
            if (original != null) original.enabled = false;
            if (definition.WorldVisualPrefab != null)
            {
                _visual = DotsWorldItemPresentation.Create(definition, rarityEffectPrefab,
                    unchecked((int)NetworkObjectId) + 1);
                _visual?.SetPose(transform.position, transform.rotation);
            }
            else
            {
                Debug.LogError($"Item '{definition.Id}' has no prefab assigned. Runtime geometry is intentionally not generated.", definition);
                return;
            }
        }

        public void SetState(FixedString64Bytes itemId, ushort amount)
        {
            if (!IsServer && IsSpawned)
                return;

            _itemId.Value = itemId;
            _amount.Value = amount;
        }

        public void PlaceSettledServer(Vector3 worldPosition, Quaternion worldRotation, NetworkObject support)
        {
            if (IsSpawned && !IsServer)
                return;

            var state = new WorldItemPlacement
            {
                Initialized = true,
                HasSupport = support != null,
                Support = support != null ? new NetworkObjectReference(support) : default,
                Start = worldPosition,
                End = worldPosition,
                ArcUp = Vector3.up,
                Rotation = worldRotation,
                FallbackPosition = worldPosition,
                FallbackRotation = worldRotation,
                Duration = 0f,
                ArcHeight = 0f
            };
            if (support != null)
            {
                var inverse = GetPhysicsFrame(support).inverse;
                state.Start = inverse.MultiplyPoint3x4(worldPosition);
                state.End = state.Start;
                state.ArcUp = inverse.MultiplyVector(Vector3.up);
                state.Rotation = Quaternion.Inverse(GetPhysicsRotation(support)) * worldRotation;
            }
            _placement.Value = state;
            transform.SetPositionAndRotation(worldPosition, worldRotation);
        }

        public string GetInteractionPrompt(NetworkPlayerController player) => $"Подобрать {Definition?.DisplayName ?? ItemId.ToString()} ×{Amount}";

        public void Interact(NetworkPlayerController player)
        {
            if (player != null && player.IsOwner)
                player.Inventory.PickupServerRpc(new NetworkObjectReference(NetworkObject));
        }
    }
}
