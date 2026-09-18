using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using WaveByWave.Items;
using WaveByWave.Ships;

namespace WaveByWave.Player
{
    [RequireComponent(typeof(NetworkObject))]
    public sealed class PlayerInventory : NetworkBehaviour
    {
        [SerializeField, Min(5)] private int capacity = 8;
        [SerializeField] private ItemCatalog catalog;
        [SerializeField] private WorldItem worldItemPrefab;
        [SerializeField] private string[] startingItemIds =
        {
            "cutlass", "musket", "hook", "bucket", "shovel", "cannonball", "plank", "food"
        };
        [SerializeField] private int[] startingItemAmounts = { 1, 1, 1, 1, 1, 20, 5, 5 };
        private readonly NetworkVariable<float> _health = new(100f);

        private NetworkList<InventorySlotState> _slots;
        private int _selectedIndex;
        private int _serverSelectedIndex;
        private uint _selectionRevision;
        private uint _serverSelectionRevision;

        public event Action Changed;
        public int Capacity => capacity;
        public int SelectedIndex => _selectedIndex;
        public int ServerSelectedIndex => _serverSelectedIndex;
        public uint SelectionRevision => _selectionRevision;
        public ItemCatalog Catalog => catalog;
        public int Count => _slots?.Count ?? 0;
        public float Health => _health.Value;

        private void Awake()
        {
            _slots = new NetworkList<InventorySlotState>();
        }

        public override void OnNetworkSpawn()
        {
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
            if (!IsOwner || Keyboard.current == null)
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
            var controller = GetComponent<NetworkPlayerController>();
            var ship = controller != null ? controller.GetSupportingShipOnServer() : null;
            if (ship != null && ship.TryGetComponent<ShipCannonBattery>(out var battery))
                battery.NotifyInventorySelectionServer(OwnerClientId);
            return true;
        }

        public void ApplyDamageServer(float amount)
        {
            if (IsServer && !float.IsNaN(amount) && !float.IsInfinity(amount))
                _health.Value = Mathf.Max(0f, _health.Value - Mathf.Max(0f, amount));
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
                if (_health.Value < 100f && TryConsumeServer(selectedIndex, 1, out _))
                    _health.Value = Mathf.Min(100f, _health.Value + definition.Potency);
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
                support != null ? new NetworkObjectReference(support) : default);
        }

        [ServerRpc]
        private void DropSelectedServerRpc(int selectedIndex, Vector3 position, Vector3 direction,
            NetworkObjectReference supportReference, ServerRpcParams rpcParams = default)
        {
            if (worldItemPrefab == null || selectedIndex < 0 || selectedIndex >= _slots.Count)
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
                if (Vector3.Distance(playerObject.transform.position, position) > 3f) return;
                position = playerObject.transform.position;
            }

            var slot = _slots[selectedIndex];
            if (slot.IsEmpty)
                return;

            var item = Instantiate(worldItemPrefab, position, Quaternion.identity);
            item.SetState(slot.ItemId, 1);
            if (!item.PrepareDrop(position, direction, support))
            {
                Destroy(item.gameObject);
                return;
            }
            item.NetworkObject.Spawn();
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
            var playerPosition = transform.position;
            var controller = playerObject.GetComponent<NetworkPlayerController>();
            if (controller != null && item.SupportingObject != null &&
                controller.TryGetPositionOnPlatform(item.SupportingObject, out var platformPosition))
                playerPosition = platformPosition;
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
