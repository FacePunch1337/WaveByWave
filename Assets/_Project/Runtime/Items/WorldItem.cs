using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using WaveByWave.Player;

namespace WaveByWave.Items
{
    [DefaultExecutionOrder(3000)]
    [RequireComponent(typeof(NetworkObject), typeof(Collider))]
    public sealed class WorldItem : NetworkBehaviour, IPlayerInteractable
    {
        [SerializeField] private ItemCatalog catalog;
        [SerializeField] private Material glowMaterial;
        [SerializeField, Tooltip("Модель для предпросмотра готового префаба в редакторе. При сетевом спавне заменяется моделью из каталога.")]
        private GameObject authoredVisual;
        [SerializeField] private string initialItemId = "cannonball";
        [SerializeField, Min(1)] private int initialAmount = 1;
        [SerializeField, Min(0.05f)] private float throwDuration = 0.45f;
        [SerializeField, Min(0f)] private float throwArcHeight = 0.25f;
        [SerializeField, Min(0f)] private float dropDistance = 0.9f;
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
        private GameObject _visual;

        public FixedString64Bytes ItemId => _itemId.Value;
        public ushort Amount => _amount.Value;
        public ItemDefinition Definition => catalog != null && catalog.TryGet(ItemId.ToString(), out var item) ? item : null;

        private void Awake()
        {
            // The collider is only a pickup target; it never pushes a character or ship.
            GetComponent<Collider>().isTrigger = true;
        }

        public override void OnNetworkSpawn()
        {
            ActiveItems.Add(this);
            if (authoredVisual != null) authoredVisual.SetActive(false);
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
            ApplyPresentationPose();
        }
        private void LateUpdate()
        {
            if (IsSpawned) ApplyPresentationPose();
        }

        private NetworkObject ResolveSupport()
        {
            if (!_placement.Value.HasSupport) return null;
            if (_support != null && _support.IsSpawned) return _support;
            if (!_placement.Value.Support.TryGet(out _support, NetworkManager)) return null;
            _supportMotion = _support.GetComponent<PlatformNetworkTransform>();
            return _support;
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
            var support = ResolveSupport();
            var now = !IsServer && _supportMotion != null
                ? _supportMotion.PresentationServerTime : NetworkManager.ServerTime.Time;
            if (support != null)
                SetPose(support.transform.TransformPoint(state.Evaluate(now)),
                    support.transform.rotation * state.Rotation);
            else if (state.HasSupport)
                SetPose(state.FallbackPosition, state.FallbackRotation);
            else
                SetPose(state.Evaluate(now), state.Rotation);
        }

        private void SetPose(Vector3 position, Quaternion rotation)
        {
            // A settled object on static ground must not dirty its trigger every frame.
            if (transform.position.Equals(position) && transform.rotation.Equals(rotation)) return;
            transform.SetPositionAndRotation(position, rotation);
        }

        public NetworkObject SupportingObject => ResolveSupport();

        // Interaction checks use the same authoritative frame as the player,
        // while rendering uses the ship's final interpolated pose each frame.
        public Vector3 GetServerPosition()
        {
            if (TryResolveTether(out var hook)) return hook.ServerHookPosition + _tether.Value.Offset;
            var state = _placement.Value;
            if (!state.Initialized) return transform.position;
            var support = ResolveSupport();
            var position = state.Evaluate(NetworkManager.ServerTime.Time);
            return support != null ? support.transform.TransformPoint(position)
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
                _placement.Value = new WorldItemPlacement { Initialized = true, Start = end, End = end,
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
            var forward = Vector3.ProjectOnPlane(direction, up).normalized;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
            var target = feet + forward * (thrown ? dropDistance : 0f);
            var found = TryFindSurface(target, up, out var hit);
            // At a railing, prefer placing the drop back onto the supporting deck.
            if (preferredSupport != null && (!found || hit.distance > 3f))
            {
                if (TryFindSurface(feet, up, out var deckHit)) { hit = deckHit; found = true; }
            }
            // Without a surface keep the inventory intact instead of stranding loot in midair.
            if (thrown && !found) return false;
            var point = found ? hit.point : target;
            var normal = found ? hit.normal : up;
            var support = found ? hit.collider.GetComponentInParent<NetworkObject>() : preferredSupport;
            if (support != null && !support.IsSpawned) support = null;
            var facing = Vector3.ProjectOnPlane(forward, normal).normalized;
            if (facing.sqrMagnitude < 0.01f) facing = Vector3.Cross(normal, Vector3.right).normalized;
            var definition = Definition;
            var modelRotation = definition != null ? definition.RestingRotation : Quaternion.identity;
            if (definition != null && definition.WorldVisualPrefab == null &&
                (definition.Category == ItemCategory.Weapon || definition.Category == ItemCategory.Tool))
                modelRotation *= Quaternion.Euler(90f, 0f, 0f);
            var rotation = Quaternion.LookRotation(facing, normal) * modelRotation;
            var clearance = GetSurfaceClearance(rotation, normal);
            var end = point + normal * (clearance + 0.005f);
            var start = thrown ? feet + up * 0.9f + forward * 0.25f : end;
            var state = new WorldItemPlacement
            {
                Initialized = true, HasSupport = support != null,
                Support = support != null ? new NetworkObjectReference(support) : default,
                Start = start, End = end, ArcUp = up, Rotation = rotation,
                FallbackPosition = end, FallbackRotation = rotation,
                Started = Unity.Netcode.NetworkManager.Singleton.ServerTime.Time,
                Duration = thrown ? throwDuration : 0f, ArcHeight = thrown ? throwArcHeight : 0f
            };
            if (support != null)
            {
                var inverse = GetPhysicsFrame(support).inverse;
                var supportRotation = GetPhysicsRotation(support);
                state.Start = inverse.MultiplyPoint3x4(start);
                state.End = inverse.MultiplyPoint3x4(end);
                state.ArcUp = inverse.MultiplyVector(up);
                state.Rotation = Quaternion.Inverse(supportRotation) * rotation;
            }
            _placement.Value = state;
            transform.SetPositionAndRotation(start, rotation);
            return true;
        }

        public static Matrix4x4 GetPhysicsFrame(NetworkObject support)
        {
            if (support == null) return Matrix4x4.identity;
            return support.TryGetComponent<Rigidbody>(out var body)
                ? Matrix4x4.TRS(body.position, body.rotation, support.transform.lossyScale)
                : support.transform.localToWorldMatrix;
        }

        private static Quaternion GetPhysicsRotation(NetworkObject support) =>
            support.TryGetComponent<Rigidbody>(out var body) ? body.rotation : support.transform.rotation;

        private bool TryFindSurface(Vector3 target, Vector3 up, out RaycastHit result)
        {
            result = default;
            var count = UnityEngine.Physics.RaycastNonAlloc(target + up * 0.7f, -up, PlacementHits,
                64f, placementLayers, QueryTriggerInteraction.Ignore);
            var hits = PlacementHits;
            if (count == PlacementHits.Length)
            {
                hits = UnityEngine.Physics.RaycastAll(target + up * 0.7f, -up, 64f,
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

        private void RefreshModelBounds()
        {
            var definition = Definition;
            var size = GetFallbackModelSize(definition);
            _modelBounds = new Bounds(Vector3.zero, size);
            if (definition != null && definition.WorldVisualPrefab != null)
            {
                var root = definition.WorldVisualPrefab.transform;
                var rootMatrix = Matrix4x4.TRS(Vector3.zero, root.localRotation, root.localScale) * root.worldToLocalMatrix;
                var initialized = false;
                foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null) continue;
                    var bounds = filter.sharedMesh.bounds;
                    var matrix = rootMatrix * filter.transform.localToWorldMatrix;
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
            if (_visual != null) Destroy(_visual);
            var original = GetComponent<Renderer>();
            if (original != null) original.enabled = false;
            if (definition.WorldVisualPrefab != null)
            {
                _visual = Instantiate(definition.WorldVisualPrefab, transform);
                _visual.transform.localPosition = Vector3.zero;
                foreach (var collider in _visual.GetComponentsInChildren<Collider>()) collider.enabled = false;
                foreach (var body in _visual.GetComponentsInChildren<Rigidbody>())
                {
                    body.isKinematic = true;
                    body.useGravity = false;
                    Destroy(body);
                }
            }
            else
            {
                var round = definition.SupplyKind == SupplyKind.Cannonball || definition.SupplyKind == SupplyKind.Food;
                _visual = GameObject.CreatePrimitive(round ? PrimitiveType.Sphere : PrimitiveType.Cube);
                _visual.name = definition.DisplayName;
                _visual.GetComponent<Collider>().enabled = false;
                Destroy(_visual.GetComponent<Collider>());
                _visual.transform.SetParent(transform, false);
                _visual.transform.localScale = GetFallbackModelSize(definition);
                var renderer = _visual.GetComponent<Renderer>();
                renderer.sharedMaterial = original != null ? original.sharedMaterial : null;
                var color = definition.SupplyKind switch
                {
                    SupplyKind.Cannonball => new Color(0.12f, 0.13f, 0.15f),
                    SupplyKind.Plank => new Color(0.45f, 0.25f, 0.1f),
                    SupplyKind.Food => new Color(0.85f, 0.15f, 0.08f),
                    _ => definition.Category == ItemCategory.Treasure ? new Color(1f, 0.7f, 0.2f) : new Color(0.55f, 0.6f, 0.65f)
                };
                var block = new MaterialPropertyBlock();
                block.SetColor("_BaseColor", color);
                block.SetColor("_EmissionColor", definition.RarityColor * 0.15f);
                renderer.SetPropertyBlock(block);
            }
            var glow = GetComponent<LootRarityGlow>();
            if (glow == null) glow = gameObject.AddComponent<LootRarityGlow>();
            glow.Initialize(definition.RarityColor, glowMaterial);
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
