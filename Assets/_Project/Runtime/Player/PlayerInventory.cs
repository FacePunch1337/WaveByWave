using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using WaveByWave.Combat;
using WaveByWave.Items;
using WaveByWave.Enemies;
using WaveByWave.Ships;

namespace WaveByWave.Player
{
    [RequireComponent(typeof(NetworkObject))]
    public sealed class PlayerInventory : NetworkBehaviour
    {
        [SerializeField, Min(5)] private int capacity = 8;
        [SerializeField] private ItemCatalog catalog;
        [SerializeField, Tooltip("Prefab локального эффекта редкости для свободных DOTS-предметов.")]
        private GameObject rarityEffectPrefab;
        [SerializeField] private string[] startingItemIds =
        {
            "cutlass", "musket", "hook", "bucket", "shovel", "cannonball", "plank", "food"
        };
        [SerializeField] private int[] startingItemAmounts = { 1, 1, 1, 1, 1, 20, 5, 5 };
        private readonly NetworkVariable<int> _equippedSlot = new();

        private NetworkList<InventorySlotState> _slots;
        private int _selectedIndex;
        private int _serverSelectedIndex;
        private uint _selectionRevision;
        private uint _serverSelectionRevision;
        private NetworkHealth _health;
        private int _localChest = int.MaxValue, _serverChest = int.MaxValue;
        private float _localChestStarted, _nextChestHeartbeat;
        private double _serverChestStarted, _serverChestHeartbeat;
        private readonly RaycastHit[] _chestLineHits = new RaycastHit[32];

        public event Action Changed;
        public int Capacity => capacity;
        public int SelectedIndex => _selectedIndex;
        public int EquippedIndex => IsOwner ? _selectedIndex : _equippedSlot.Value;
        public int ServerSelectedIndex => _serverSelectedIndex;
        public uint SelectionRevision => _selectionRevision;
        public ItemCatalog Catalog => catalog;
        public int Count => _slots?.Count ?? 0;
        public float Health => _health != null ? _health.CurrentHealth : 0f;
        public float MaximumHealth => _health != null ? _health.MaximumHealth : 100f;

        private void Awake()
        {
            _slots = new NetworkList<InventorySlotState>();
            _health = GetComponent<NetworkHealth>();
        }

        public override void OnNetworkSpawn()
        {
            LootStressTest.RegisterCatalog(catalog, rarityEffectPrefab,
                GetComponent<PlayerEquipment>()?.WaterWaveProfile);
            _slots.OnListChanged += OnListChanged;

            if (IsServer && _slots.Count == 0)
            {
                for (var i = 0; i < capacity; i++)
                {
                    var amount = i < startingItemAmounts.Length ? startingItemAmounts[i] : 1;
                    if (i < startingItemIds.Length && catalog != null && catalog.TryGet(startingItemIds[i], out var item))
                        amount = Mathf.Clamp(amount, 1, item.MaximumStack);
                    _slots.Add(i < startingItemIds.Length ? new InventorySlotState(startingItemIds[i],
                        (ushort)Mathf.Clamp(amount, 1, ushort.MaxValue)) : default);
                }
            }

            if (IsOwner)
                gameObject.AddComponent<InventoryHud>().Initialize(this);

            Changed?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            _slots.OnListChanged -= OnListChanged;
        }

        private void Update()
        {
            if (IsSpawned && IsServer) UpdateChestOpeningServer();
            if (IsOwner && PlayerEquipment.InputCaptured) CancelLocalChestHold();
            if (!IsSpawned || !IsOwner || Keyboard.current == null || PlayerEquipment.InputCaptured)
                return;

            var keys = new[]
            {
                Keyboard.current.digit1Key, Keyboard.current.digit2Key,
                Keyboard.current.digit3Key, Keyboard.current.digit4Key,
                Keyboard.current.digit5Key, Keyboard.current.digit6Key,
                Keyboard.current.digit7Key, Keyboard.current.digit8Key
            };

            for (var i = 0; i < Mathf.Min(keys.Length, capacity); i++)
            {
                if (!keys[i].wasPressedThisFrame)
                    continue;

                _selectedIndex = i;
                SelectSlotServerRpc(i, ++_selectionRevision);
                Changed?.Invoke();
                break;
            }
        }

        public InventorySlotState GetSlot(int index)
        {
            return _slots != null && index >= 0 && index < _slots.Count ? _slots[index] : default;
        }

        public string GetDisplayName(InventorySlotState slot)
        {
            return catalog != null && catalog.TryGet(slot.ItemId.ToString(), out var definition)
                ? definition.DisplayName
                : slot.ItemId.ToString();
        }

        public void UseSelected(bool special) => UseSelectedServerRpc(_selectedIndex, special);

        public bool TryGetDefinition(int index, out ItemDefinition definition)
        {
            definition = null;
            var slot = GetSlot(index);
            return !slot.IsEmpty && catalog != null && catalog.TryGet(slot.ItemId.ToString(), out definition);
        }

        // Only server transactions can remove items from a stack.
        public bool TryConsumeServer(int index, ushort amount, out ItemDefinition definition)
        {
            definition = null;
            if (!IsServer || amount == 0 || !TryGetDefinition(index, out definition))
                return false;
            var slot = _slots[index];
            if (slot.Amount < amount)
                return false;
            slot.Amount -= amount;
            _slots[index] = slot.Amount == 0 ? default : slot;
            return true;
        }

        [ServerRpc]
        private void SelectSlotServerRpc(int index, uint revision) => ApplySelectionServer(index, revision);

        public bool ApplySelectionServer(int index, uint revision)
        {
            if (!IsServer || index < 0 || index >= _slots.Count || revision < _serverSelectionRevision) return false;
            if (revision == _serverSelectionRevision) return index == _serverSelectedIndex;
            _serverSelectionRevision = revision;
            _serverSelectedIndex = index;
            _equippedSlot.Value = index;
            var controller = GetComponent<NetworkPlayerController>();
            var ship = controller != null ? controller.GetSupportingShipOnServer() : null;
            if (ship != null && ship.TryGetComponent<ShipCannonBattery>(out var battery))
                battery.NotifyInventorySelectionServer(OwnerClientId);
            return true;
        }

        public void ApplyDamageServer(float amount)
        {
            ApplyDamageServer(amount, transform.position - transform.forward);
        }

        public void ApplyDamageServer(float amount, Vector3 sourcePosition)
        {
            if (IsServer && _health != null)
                _health.ApplyDamageServer(amount, sourcePosition);
        }

        [ServerRpc]
        private void UseSelectedServerRpc(int selectedIndex, bool special, ServerRpcParams rpcParams = default)
        {
            if (!NetworkManager.ConnectedClients.TryGetValue(rpcParams.Receive.SenderClientId, out var client))
                return;

            var player = client.PlayerObject;
            if (player == null || player != NetworkObject || selectedIndex < 0 || selectedIndex >= _slots.Count || _slots[selectedIndex].IsEmpty)
                return;

            if (!TryGetDefinition(selectedIndex, out var definition))
                return;
            if (definition.Category == ItemCategory.Supply && definition.SupplyKind == SupplyKind.Food)
            {
                if (_health != null && _health.CurrentHealth < _health.MaximumHealth &&
                    TryConsumeServer(selectedIndex, 1, out _))
                    _health.HealServer(definition.Potency);
                return;
            }
            var controller = player.GetComponent<NetworkPlayerController>();
            var ship = controller != null ? controller.GetSupportingShipOnServer() : null;
            if (ship != null && ship.TryGetComponent<ShipCannonBattery>(out var battery))
            {
                if (definition.Category == ItemCategory.Supply && definition.SupplyKind == SupplyKind.Plank)
                {
                    if (battery.Health < battery.MaximumHealth && TryConsumeServer(selectedIndex, 1, out _))
                        battery.RepairServer(definition.Potency);
                }
                else if (definition.Category == ItemCategory.ShipUpgrade && TryConsumeServer(selectedIndex, 1, out _))
                    battery.ApplyUpgradeServer(definition);
            }
        }

        public void DropSelected()
        {
            if (!IsOwner || !TryGetComponent<NetworkPlayerController>(out var controller)) return;
            controller.GetItemDropPose(out var position, out var direction, out var support);
            if (support != null)
            {
                position = support.transform.InverseTransformPoint(position);
                direction = support.transform.InverseTransformDirection(direction);
            }
            DropSelectedServerRpc(_selectedIndex, position, direction,
                new NetworkObjectReference(support));
        }

        public void SpawnAdminItem(string itemId)
        {
            if (!IsOwner || !IsHost || !TryGetComponent<NetworkPlayerController>(out var controller)) return;
            controller.GetItemDropPose(out var position, out var direction, out var support);
            if (support != null)
            {
                position = support.transform.InverseTransformPoint(position);
                direction = support.transform.InverseTransformDirection(direction);
            }
            SpawnAdminItemServerRpc(new FixedString64Bytes(itemId), position, direction,
                new NetworkObjectReference(support));
        }

        public bool SetAdminStressItems(int count, float radius)
        {
            if (!IsOwner || !IsHost)
                return false;
            LootStressTest.RegisterCatalog(catalog, rarityEffectPrefab,
                GetComponent<PlayerEquipment>()?.WaterWaveProfile);
            return LootStressTest.SetTarget(count, transform.position, radius);
        }

        public void PickupStressItem(int id)
        {
            if (IsOwner) PickupStressItemServerRpc(id);
        }

        public bool UpdateChestInteraction(IPlayerInteractable target, bool pressed, bool held, bool released)
        {
            var isChest = LootStressTest.TryClientTarget(target, out var id, out var definition) && definition.IsChest;
            if (_localChest != int.MaxValue && (!isChest || _localChest != id || (!held && !released))) CancelLocalChestHold();
            if (!isChest) return false;
            if (pressed)
            {
                _localChest = id; _localChestStarted = Time.unscaledTime;
                _nextChestHeartbeat = Time.unscaledTime + 0.15f;
                ChestHoldServerRpc(id, true);
            }
            if (_localChest == id && held)
            {
                LootStressTest.SetChestHoldProgress(id, (Time.unscaledTime - _localChestStarted) /
                    Mathf.Max(0.3f, definition.ChestLoot.HoldDuration));
                if (Time.unscaledTime >= _nextChestHeartbeat)
                { _nextChestHeartbeat = Time.unscaledTime + 0.15f; ChestHoldServerRpc(id, true); }
            }
            if (_localChest == id && released)
            {
                var tap = Time.unscaledTime - _localChestStarted < 0.25f;
                CancelLocalChestHold();
                if (tap) PickupStressItem(id);
            }
            return true;
        }

        private void CancelLocalChestHold()
        {
            if (_localChest == int.MaxValue) return;
            if (IsSpawned) ChestHoldServerRpc(_localChest, false);
            _localChest = int.MaxValue;
            LootStressTest.SetChestHoldProgress(int.MaxValue, 0f);
        }

        [ServerRpc]
        private void ChestHoldServerRpc(int id, bool holding)
        {
            if (!holding) { if (_serverChest == id) _serverChest = int.MaxValue; return; }
            if (!LootStressTest.TryGetServerItem(id, out var definition, out var position) || !definition.IsChest ||
                !CanReachChest(position) || (_health != null && _health.IsDead)) { _serverChest = int.MaxValue; return; }
            var now = NetworkManager.ServerTime.Time;
            if (_serverChest != id || now - _serverChestHeartbeat > 0.5d)
            { _serverChest = id; _serverChestStarted = now; }
            _serverChestHeartbeat = now;
        }

        private void UpdateChestOpeningServer()
        {
            if (_serverChest == int.MaxValue) return;
            var now = NetworkManager.ServerTime.Time;
            if (now - _serverChestHeartbeat > 0.5d || (_health != null && _health.IsDead) ||
                !LootStressTest.TryGetServerItem(_serverChest, out var definition, out var position) ||
                !definition.IsChest || !CanReachChest(position)) { _serverChest = int.MaxValue; return; }
            if (now - _serverChestStarted < Mathf.Max(0.3f, definition.ChestLoot.HoldDuration)) return;
            LootStressTest.TryBeginChestOpening(_serverChest); _serverChest = int.MaxValue;
        }

        private bool CanReachChest(Vector3 position)
        {
            var feet = ServerInteractionPosition();
            if ((position - feet).sqrMagnitude > 16f) return false;
            var from = feet + Vector3.up * 1.3f;
            var delta = position + Vector3.up * 0.2f - from;
            var hits = UnityEngine.Physics.RaycastNonAlloc(from, delta.normalized, _chestLineHits,
                Mathf.Max(0f, delta.magnitude - 0.3f), ~0, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < hits; i++)
                if (_chestLineHits[i].collider.GetComponentInParent<NetworkPlayerController>() == null &&
                    _chestLineHits[i].collider.GetComponentInParent<StylizedWater3.WaterObject>() == null) return false;
            return true;
        }

        [ServerRpc]
        private void PickupStressItemServerRpc(int id, ServerRpcParams rpcParams = default)
        {
            if (!NetworkManager.ConnectedClients.TryGetValue(rpcParams.Receive.SenderClientId, out var client) ||
                client.PlayerObject == null || client.PlayerObject != NetworkObject ||
                !LootStressTest.TryGetServerItem(id, out var definition, out var position) ||
                Vector3.Distance(ServerInteractionPosition(), position) > 4f ||
                (definition.IsChest && !CanReachChest(position)) || !TryStoreSingleServer(definition)) return;
            LootStressTest.RemoveServerItem(id);
        }

        private bool TryStoreSingleServer(ItemDefinition definition)
        {
            if (!IsServer || definition == null) return false;
            var id = new FixedString64Bytes(definition.Id);
            for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                if (pass == 0 && (slot.IsEmpty || !slot.ItemId.Equals(id) || slot.Amount >= definition.MaximumStack)) continue;
                if (pass == 1 && !slot.IsEmpty) continue;
                _slots[i] = new InventorySlotState(definition.Id, (ushort)((slot.IsEmpty ? 0 : slot.Amount) + 1));
                return true;
            }
            return false;
        }

        [ServerRpc]
        private void SpawnAdminItemServerRpc(FixedString64Bytes itemId, Vector3 position, Vector3 direction,
            NetworkObjectReference supportReference, ServerRpcParams rpc = default)
        {
            // The panel is host-only, and the server also enforces that permission.
            if (rpc.Receive.SenderClientId != Unity.Netcode.NetworkManager.ServerClientId ||
                catalog == null || !catalog.TryGet(itemId.ToString(), out _) ||
                !ResolveDropPoseServer(ref position, ref direction, supportReference, out var support)) return;
            SpawnDropServer(itemId, position, direction, support);
        }

        private bool ResolveDropPoseServer(ref Vector3 position, ref Vector3 direction,
            NetworkObjectReference supportReference, out NetworkObject support)
        {
            support = null;
            if (!IsFinite(position) || !IsFinite(direction) || direction.sqrMagnitude < 0.01f) return false;
            var controller = GetComponent<NetworkPlayerController>();
            if (supportReference.TryGet(out var requested, NetworkManager))
            {
                if (controller == null || !controller.TryGetPositionOnPlatform(requested, out var feet)) return false;
                var localFeet = requested.transform.InverseTransformPoint(feet);
                if (Vector3.Distance(localFeet, position) > 3f) return false;
                support = requested;
                var frame = WorldItem.GetPhysicsFrame(support);
                position = frame.MultiplyPoint3x4(localFeet);
                direction = frame.MultiplyVector(direction).normalized;
            }
            else
            {
                var feet = ServerInteractionPosition();
                if (Vector3.Distance(feet, position) > 3f) return false;
                position = feet;
                direction.Normalize();
            }
            return true;
        }

        private Vector3 ServerInteractionPosition() => TryGetComponent<NetworkPlayerController>(out var controller)
            ? DotsEnemyRuntime.Feet(controller) : transform.position;

        private bool SpawnDropServer(FixedString64Bytes itemId, Vector3 position, Vector3 direction, NetworkObject support)
        {
            if (catalog == null || !catalog.TryGet(itemId.ToString(), out var definition)) return false;
            LootStressTest.RegisterCatalog(catalog, rarityEffectPrefab,
                GetComponent<PlayerEquipment>()?.WaterWaveProfile);
            return LootStressTest.SpawnWorldItemServer(definition, position, direction, support);
        }

        [ServerRpc]
        private void DropSelectedServerRpc(int selectedIndex, Vector3 position, Vector3 direction,
            NetworkObjectReference supportReference, ServerRpcParams rpcParams = default)
        {
            if (selectedIndex < 0 || selectedIndex >= _slots.Count)
                return;

            if (!NetworkManager.ConnectedClients.TryGetValue(rpcParams.Receive.SenderClientId, out var client))
                return;

            var playerObject = client.PlayerObject;
            if (playerObject == null || playerObject != NetworkObject || !IsFinite(position) || !IsFinite(direction))
                return;

            var controller = playerObject.GetComponent<NetworkPlayerController>();
            NetworkObject support = null;
            if (supportReference.TryGet(out var requestedSupport, NetworkManager))
            {
                if (controller == null || !controller.TryGetPositionOnPlatform(requestedSupport, out var authoritativePosition))
                    return;
                var localFeet = requestedSupport.transform.InverseTransformPoint(authoritativePosition);
                if (Vector3.Distance(localFeet, position) > 3f) return;
                support = requestedSupport;
                var frame = WorldItem.GetPhysicsFrame(support);
                position = frame.MultiplyPoint3x4(localFeet);
                direction = frame.MultiplyVector(direction).normalized;
            }
            else
            {
                var feet = ServerInteractionPosition();
                if (Vector3.Distance(feet, position) > 3f) return;
                position = feet;
            }

            var slot = _slots[selectedIndex];
            if (slot.IsEmpty)
                return;

            if (!SpawnDropServer(slot.ItemId, position, direction, support)) return;
            slot.Amount--;
            _slots[selectedIndex] = slot.Amount == 0 ? default : slot;
        }

        [ServerRpc]
        public void PickupServerRpc(NetworkObjectReference itemReference, ServerRpcParams rpcParams = default)
        {
            if (!itemReference.TryGet(out var networkObject) || !networkObject.TryGetComponent<WorldItem>(out var item))
                return;

            if (!NetworkManager.ConnectedClients.TryGetValue(rpcParams.Receive.SenderClientId, out var client))
                return;

            var playerObject = client.PlayerObject;
            if (playerObject == null || playerObject != NetworkObject)
                return;
            var playerPosition = ServerInteractionPosition();
            var controller = playerObject.GetComponent<NetworkPlayerController>();
            if (controller != null && controller.TryGetEnemyShipPositionOnServer(out var enemyPosition))
                playerPosition = enemyPosition;
            if (controller != null && item.SupportingObject != null &&
                controller.TryGetPositionOnPlatform(item.SupportingObject, out var platformPosition))
                playerPosition = WorldItem.GetPhysicsFrame(item.SupportingObject).MultiplyPoint3x4(
                    item.SupportingObject.transform.InverseTransformPoint(platformPosition));
            if (Vector3.Distance(playerPosition, item.GetServerPosition()) > 4f)
                return;

            var remaining = item.Amount;
            if (catalog == null || !catalog.TryGet(item.ItemId.ToString(), out var definition))
                return;
            // Merge stacks before allocating empty slots. A partial pickup keeps
            // the remaining units in the original world item.
            for (var pass = 0; pass < 2 && remaining > 0; pass++)
            for (var i = 0; i < _slots.Count && remaining > 0; i++)
            {
                var slot = _slots[i];
                if (pass == 0 && (slot.IsEmpty || !slot.ItemId.Equals(item.ItemId)))
                    continue;
                if (pass == 1 && !slot.IsEmpty)
                    continue;
                var space = definition.MaximumStack - (slot.IsEmpty ? 0 : slot.Amount);
                var added = Mathf.Min(space, remaining);
                if (added <= 0)
                    continue;
                _slots[i] = new InventorySlotState(item.ItemId.ToString(), (ushort)((slot.IsEmpty ? 0 : slot.Amount) + added));
                remaining -= (ushort)added;
            }
            if (remaining == 0)
                item.NetworkObject.Despawn(true);
            else if (remaining != item.Amount)
                item.SetState(item.ItemId, remaining);
        }

        private void OnListChanged(NetworkListEvent<InventorySlotState> changeEvent) => Changed?.Invoke();

        private static bool IsFinite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
