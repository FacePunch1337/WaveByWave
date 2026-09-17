using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using WaveByWave.Items;

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
            "cutlass", "musket", "hook", "bucket", "shovel"
        };

        private NetworkList<InventorySlotState> _slots;
        private int _selectedIndex;

        public event Action Changed;
        public int Capacity => capacity;
        public int SelectedIndex => _selectedIndex;
        public ItemCatalog Catalog => catalog;
        public int Count => _slots?.Count ?? 0;

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
                    _slots.Add(i < startingItemIds.Length ? new InventorySlotState(startingItemIds[i]) : default);
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

        [ServerRpc]
        private void UseSelectedServerRpc(int selectedIndex, bool special, ServerRpcParams rpcParams = default)
        {
            if (!NetworkManager.ConnectedClients.TryGetValue(rpcParams.Receive.SenderClientId, out var client))
                return;

            var player = client.PlayerObject;
            if (player == null || player != NetworkObject || selectedIndex < 0 || selectedIndex >= _slots.Count || _slots[selectedIndex].IsEmpty)
                return;

            Debug.Log($"Player {OwnerClientId} used {_slots[selectedIndex].ItemId} ({(special ? "special" : "primary")}).");
        }

        public void DropSelected(Vector3 position, Vector3 direction) =>
            DropSelectedServerRpc(_selectedIndex, position, direction);

        [ServerRpc]
        private void DropSelectedServerRpc(int selectedIndex, Vector3 position, Vector3 direction, ServerRpcParams rpcParams = default)
        {
            if (worldItemPrefab == null || selectedIndex < 0 || selectedIndex >= _slots.Count)
                return;

            if (!NetworkManager.ConnectedClients.TryGetValue(rpcParams.Receive.SenderClientId, out var client))
                return;

            var playerObject = client.PlayerObject;
            if (playerObject == null || playerObject != NetworkObject || Vector3.Distance(playerObject.transform.position, position) > 3f)
                return;

            var slot = _slots[selectedIndex];
            if (slot.IsEmpty)
                return;

            _slots[selectedIndex] = default;
            var item = Instantiate(worldItemPrefab, position, Quaternion.identity);
            item.SetState(slot.ItemId, slot.Amount);
            item.NetworkObject.Spawn();
            if (item.TryGetComponent<Rigidbody>(out var body))
                body.linearVelocity = direction.normalized * 2f + Vector3.up * 1.5f;
        }

        [ServerRpc]
        public void PickupServerRpc(NetworkObjectReference itemReference, ServerRpcParams rpcParams = default)
        {
            if (!itemReference.TryGet(out var networkObject) || !networkObject.TryGetComponent<WorldItem>(out var item))
                return;

            if (!NetworkManager.ConnectedClients.TryGetValue(rpcParams.Receive.SenderClientId, out var client))
                return;

            var playerObject = client.PlayerObject;
            if (playerObject == null || playerObject != NetworkObject || Vector3.Distance(transform.position, item.transform.position) > 4f)
                return;

            for (var i = 0; i < _slots.Count; i++)
            {
                if (!_slots[i].IsEmpty)
                    continue;

                _slots[i] = new InventorySlotState(item.ItemId.ToString(), item.Amount);
                item.NetworkObject.Despawn(true);
                return;
            }
        }

        private void OnListChanged(NetworkListEvent<InventorySlotState> changeEvent) => Changed?.Invoke();
    }
}
