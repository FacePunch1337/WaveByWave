using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using WaveByWave.Commerce;
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
        [Header("Commerce")]
        [SerializeField, Min(0)] private int startingBalance = 500;
        [SerializeField] private GameObject orderLetterHandVisual;
        [SerializeField] private string[] startingItemIds =
        {
            "cutlass", "musket", "hook", "bucket", "shovel", "cannonball", "plank", "food"
        };
        [SerializeField] private int[] startingItemAmounts = { 1, 1, 1, 1, 1, 20, 5, 5 };
        private readonly NetworkVariable<float> _health = new(100f);
        private readonly NetworkVariable<int> _equippedSlot = new();
        private readonly NetworkVariable<int> _balance = new();
        private readonly NetworkVariable<PurchaseOrderStatus> _orderStatus = new();
        private readonly NetworkVariable<int> _orderTotal = new();
        private readonly NetworkVariable<int> _deliveryDay = new();
        private readonly NetworkVariable<FixedString128Bytes> _commerceMessage = new();

        private NetworkList<InventorySlotState> _slots;
        private int _selectedIndex;
        private int _serverSelectedIndex;
        private uint _selectionRevision;
        private uint _serverSelectionRevision;
        private OrderLineState[] _serverOrderLines = Array.Empty<OrderLineState>();
        private OrderDesk _serverOrderDesk;
        private OrderDeliveryCart _serverDeliveryCart;
        private int _serverOrderId;
        private bool _economyInitialized;
        private GameObject _heldLetterVisual;

        public event Action Changed;
        public event Action CommerceChanged;
        public int Capacity => capacity;
        public int SelectedIndex => _selectedIndex;
        public int EquippedIndex => IsOwner ? _selectedIndex : _equippedSlot.Value;
        public int ServerSelectedIndex => _serverSelectedIndex;
        public uint SelectionRevision => _selectionRevision;
        public ItemCatalog Catalog => catalog;
        public int Count => _slots?.Count ?? 0;
        public float Health => _health.Value;
        public int Balance => _balance.Value;
        public int OrderTotal => _orderTotal.Value;
        public int DeliveryDay => _deliveryDay.Value;
        public PurchaseOrderStatus OrderStatus => _orderStatus.Value;

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

            if (IsServer && !_economyInitialized)
            {
                _balance.Value = Mathf.Max(0, startingBalance);
                _economyInitialized = true;
            }

            if (IsOwner)
            {
                gameObject.AddComponent<InventoryHud>().Initialize(this);
                gameObject.AddComponent<CommerceHud>().Initialize(this);
            }

            _balance.OnValueChanged += OnCommerceValueChanged;
            _orderStatus.OnValueChanged += OnOrderStatusChanged;
            _orderTotal.OnValueChanged += OnCommerceValueChanged;
            _deliveryDay.OnValueChanged += OnCommerceValueChanged;
            _commerceMessage.OnValueChanged += OnCommerceMessageChanged;

            Changed?.Invoke();
            RefreshHeldLetterVisual();
            CommerceChanged?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            _slots.OnListChanged -= OnListChanged;
            _balance.OnValueChanged -= OnCommerceValueChanged;
            _orderStatus.OnValueChanged -= OnOrderStatusChanged;
            _orderTotal.OnValueChanged -= OnCommerceValueChanged;
            _deliveryDay.OnValueChanged -= OnCommerceValueChanged;
            _commerceMessage.OnValueChanged -= OnCommerceMessageChanged;
            if (IsServer && _serverOrderDesk != null)
                _serverOrderDesk.ClearLetterServer(OwnerClientId, _serverOrderId);
            if (_heldLetterVisual != null)
                Destroy(_heldLetterVisual);
        }

        private void Update()
        {
            if (IsServer && _orderStatus.Value == PurchaseOrderStatus.InTransit &&
                _serverDeliveryCart != null && _serverDeliveryCart.CurrentDay >= _deliveryDay.Value)
            {
                if (_serverDeliveryCart.ReceiveDeliveryServer(_serverOrderLines))
                {
                    _orderStatus.Value = PurchaseOrderStatus.Delivered;
                    _commerceMessage.Value = "Заказ доставлен в тележку";
                }
            }

            if (!IsSpawned || !IsOwner || Keyboard.current == null || EquipmentAdminPanel.InputCaptured ||
                WaveByWave.UI.SessionMenuPresenter.InputCaptured || WaveByWave.UI.OrderMenuPresenter.InputCaptured)
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

        public string GetOrderStatusText() => _orderStatus.Value switch
        {
            PurchaseOrderStatus.LetterOnDesk => "Письмо лежит на столе",
            PurchaseOrderStatus.LetterHeld => "Письмо в руках — отнесите в ящик",
            PurchaseOrderStatus.InTransit => $"Доставка: день {_deliveryDay.Value}",
            PurchaseOrderStatus.Delivered => "Заказ доставлен в тележку",
            _ => _commerceMessage.Value.IsEmpty ? "Заказов нет" : _commerceMessage.Value.ToString()
        };

        public void RequestCreateOrder(OrderDesk desk, OrderLineState[] lines)
        {
            if (IsOwner && desk != null && lines != null && lines.Length > 0)
                CreateOrderServerRpc(new NetworkObjectReference(desk.NetworkObject), lines);
        }

        public void RequestTakeOrderLetter(OrderDesk desk, int orderId)
        {
            if (IsOwner && desk != null)
                TakeOrderLetterServerRpc(new NetworkObjectReference(desk.NetworkObject), orderId);
        }

        public void RequestSubmitOrder(OrderMailbox mailbox)
        {
            if (IsOwner && mailbox != null)
                SubmitOrderServerRpc(new NetworkObjectReference(mailbox.NetworkObject));
        }

        [ServerRpc]
        private void CreateOrderServerRpc(NetworkObjectReference deskReference, OrderLineState[] lines,
            ServerRpcParams rpc = default)
        {
            if (rpc.Receive.SenderClientId != OwnerClientId || lines == null || lines.Length == 0 ||
                lines.Length > 32 || !deskReference.TryGet(out var deskObject, NetworkManager) ||
                !deskObject.TryGetComponent<OrderDesk>(out var desk) ||
                Vector3.Distance(transform.position, desk.transform.position) > 5f ||
                (_orderStatus.Value != PurchaseOrderStatus.None &&
                 _orderStatus.Value != PurchaseOrderStatus.Delivered))
                return;

            var validated = new System.Collections.Generic.List<OrderLineState>(lines.Length);
            var total = 0L;
            foreach (var line in lines)
            {
                if (line.Quantity == 0 || catalog == null ||
                    !catalog.TryGet(line.ItemId.ToString(), out var definition) || !definition.CanBeOrdered)
                    continue;
                var lineCost = (long)definition.PurchasePrice * line.Quantity;
                if (lineCost <= 0 || total + lineCost > int.MaxValue)
                    return;
                total += lineCost;
                validated.Add(new OrderLineState(definition.Id, line.Quantity));
            }

            if (validated.Count == 0 || total <= 0)
                return;
            var orderId = ++_serverOrderId;
            if (!desk.TryCreateLetterServer(OwnerClientId, orderId))
            {
                _commerceMessage.Value = "Стол занят другим письмом";
                return;
            }

            _serverOrderLines = validated.ToArray();
            _serverOrderDesk = desk;
            _serverDeliveryCart = null;
            _orderTotal.Value = (int)total;
            _deliveryDay.Value = 0;
            _orderStatus.Value = PurchaseOrderStatus.LetterOnDesk;
            _commerceMessage.Value = "Письмо с заказом готово";
        }

        [ServerRpc]
        private void TakeOrderLetterServerRpc(NetworkObjectReference deskReference, int orderId,
            ServerRpcParams rpc = default)
        {
            if (rpc.Receive.SenderClientId != OwnerClientId ||
                _orderStatus.Value != PurchaseOrderStatus.LetterOnDesk || orderId != _serverOrderId ||
                !deskReference.TryGet(out var deskObject, NetworkManager) ||
                !deskObject.TryGetComponent<OrderDesk>(out var desk) || desk != _serverOrderDesk ||
                Vector3.Distance(transform.position, desk.transform.position) > 5f ||
                !desk.TryTakeLetterServer(OwnerClientId, orderId))
                return;
            _orderStatus.Value = PurchaseOrderStatus.LetterHeld;
            _commerceMessage.Value = "Отнесите письмо в почтовый ящик";
        }

        [ServerRpc]
        private void SubmitOrderServerRpc(NetworkObjectReference mailboxReference, ServerRpcParams rpc = default)
        {
            if (rpc.Receive.SenderClientId != OwnerClientId ||
                _orderStatus.Value != PurchaseOrderStatus.LetterHeld ||
                !mailboxReference.TryGet(out var mailboxObject, NetworkManager) ||
                !mailboxObject.TryGetComponent<OrderMailbox>(out var mailbox) ||
                Vector3.Distance(transform.position, mailbox.transform.position) > 5f)
                return;
            if (mailbox.DeliveryCart == null || !mailbox.DeliveryCart.IsSpawned)
            {
                _commerceMessage.Value = "К ящику не привязана тележка доставки";
                return;
            }
            if (_balance.Value < _orderTotal.Value)
            {
                _commerceMessage.Value = "Недостаточно денег для отправки заказа";
                return;
            }

            _balance.Value -= _orderTotal.Value;
            _serverDeliveryCart = mailbox.DeliveryCart;
            _deliveryDay.Value = _serverDeliveryCart.CurrentDay + 1;
            _orderStatus.Value = PurchaseOrderStatus.InTransit;
            _commerceMessage.Value = $"Заказ оплачен. Доставка в день {_deliveryDay.Value}";
        }

        private void OnCommerceValueChanged(int previous, int current) => CommerceChanged?.Invoke();
        private void OnCommerceMessageChanged(FixedString128Bytes previous, FixedString128Bytes current) =>
            CommerceChanged?.Invoke();
        private void OnOrderStatusChanged(PurchaseOrderStatus previous, PurchaseOrderStatus current)
        {
            RefreshHeldLetterVisual();
            CommerceChanged?.Invoke();
        }

        private void RefreshHeldLetterVisual()
        {
            if (!IsOwner)
                return;
            var shouldShow = _orderStatus.Value == PurchaseOrderStatus.LetterHeld;
            if (!shouldShow)
            {
                if (_heldLetterVisual != null)
                    Destroy(_heldLetterVisual);
                _heldLetterVisual = null;
                return;
            }
            if (_heldLetterVisual != null)
                return;
            var controller = GetComponent<NetworkPlayerController>();
            var parent = controller != null ? controller.OwnerView : null;
            if (parent == null)
                return;
            _heldLetterVisual = orderLetterHandVisual != null
                ? Instantiate(orderLetterHandVisual, parent, false)
                : GameObject.CreatePrimitive(PrimitiveType.Cube);
            _heldLetterVisual.name = "Held SM_Props_Paper_01";
            _heldLetterVisual.transform.SetParent(parent, false);
            _heldLetterVisual.transform.localPosition = new Vector3(0.28f, -0.28f, 0.55f);
            _heldLetterVisual.transform.localRotation = Quaternion.Euler(18f, -12f, -8f);
            if (orderLetterHandVisual == null)
            {
                _heldLetterVisual.transform.localScale = new Vector3(0.25f, 0.012f, 0.18f);
                _heldLetterVisual.GetComponent<Renderer>().material.color = new Color(0.94f, 0.87f, 0.68f);
            }
            foreach (var collider in _heldLetterVisual.GetComponentsInChildren<Collider>())
                collider.enabled = false;
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
                if (Vector3.Distance(transform.position, position) > 3f) return false;
                position = transform.position;
                direction.Normalize();
            }
            return true;
        }

        private bool SpawnDropServer(FixedString64Bytes itemId, Vector3 position, Vector3 direction, NetworkObject support)
        {
            if (worldItemPrefab == null) return false;
            var item = Instantiate(worldItemPrefab, position, Quaternion.identity);
            item.SetState(itemId, 1);
            if (!item.PrepareDrop(position, direction, support))
            {
                Destroy(item.gameObject);
                return false;
            }
            item.NetworkObject.Spawn();
            return true;
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
