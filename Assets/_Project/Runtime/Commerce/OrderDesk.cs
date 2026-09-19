using Unity.Netcode;
using UnityEngine;
using WaveByWave.Player;
using WaveByWave.UI;

namespace WaveByWave.Commerce
{
    [RequireComponent(typeof(NetworkObject))]
    public sealed class OrderDesk : NetworkBehaviour, IPlayerInteractable
    {
        [SerializeField] private Transform letterAnchor;
        [SerializeField] private GameObject paperVisualPrefab;
        [SerializeField] private Vector3 fallbackLetterPosition = new(0f, 0.82f, 0f);
        [SerializeField] private Vector3 fallbackLetterScale = new(0.28f, 0.012f, 0.2f);

        private readonly NetworkVariable<bool> _hasLetter = new();
        private readonly NetworkVariable<ulong> _letterOwner = new(ulong.MaxValue);
        private readonly NetworkVariable<int> _letterOrderId = new();
        private GameObject _letterVisual;

        public bool HasLetter => _hasLetter.Value;

        private void Awake() => EnsureLetterVisual();

        public override void OnNetworkSpawn()
        {
            _hasLetter.OnValueChanged += OnLetterChanged;
            RefreshLetterVisual();
        }

        public override void OnNetworkDespawn()
        {
            _hasLetter.OnValueChanged -= OnLetterChanged;
        }

        private void OnLetterChanged(bool previous, bool current) => RefreshLetterVisual();

        private void EnsureLetterVisual()
        {
            if (_letterVisual != null)
                return;

            var parent = letterAnchor != null ? letterAnchor : transform;
            if (paperVisualPrefab != null)
                _letterVisual = Instantiate(paperVisualPrefab, parent, false);
            else
            {
                _letterVisual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _letterVisual.name = "Temporary SM_Props_Paper_01";
                _letterVisual.transform.SetParent(parent, false);
                _letterVisual.transform.localScale = fallbackLetterScale;
                if (_letterVisual.TryGetComponent<Renderer>(out var renderer))
                    renderer.material.color = new Color(0.94f, 0.87f, 0.68f);
            }

            if (letterAnchor == null)
                _letterVisual.transform.localPosition = fallbackLetterPosition;
            foreach (var collider in _letterVisual.GetComponentsInChildren<Collider>())
                collider.enabled = false;

            var pickup = _letterVisual.AddComponent<OrderLetterPickup>();
            pickup.Initialize(this);
            var trigger = _letterVisual.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            RefreshLetterVisual();
        }

        private void RefreshLetterVisual()
        {
            if (_letterVisual != null)
                _letterVisual.SetActive(_hasLetter.Value);
        }

        public string GetInteractionPrompt(NetworkPlayerController player)
        {
            if (!_hasLetter.Value)
                return "Открыть меню заказов [E]";
            return player != null && player.OwnerClientId == _letterOwner.Value
                ? "Взять письмо с заказом [E]"
                : "На столе лежит чужое письмо";
        }

        public void Interact(NetworkPlayerController player)
        {
            if (player == null || !player.IsOwner || player.Inventory == null)
                return;
            if (_hasLetter.Value)
                player.Inventory.RequestTakeOrderLetter(this, _letterOrderId.Value);
            else
                OrderMenuPresenter.Open(this, player.Inventory);
        }

        public bool TryCreateLetterServer(ulong ownerClientId, int orderId)
        {
            if (!IsServer || _hasLetter.Value)
                return false;
            _letterOwner.Value = ownerClientId;
            _letterOrderId.Value = orderId;
            _hasLetter.Value = true;
            return true;
        }

        public bool TryTakeLetterServer(ulong ownerClientId, int orderId)
        {
            if (!IsServer || !_hasLetter.Value || _letterOwner.Value != ownerClientId ||
                _letterOrderId.Value != orderId)
                return false;
            ClearLetterServer(ownerClientId, orderId);
            return true;
        }

        public void ClearLetterServer(ulong ownerClientId, int orderId)
        {
            if (!IsServer || !_hasLetter.Value || _letterOwner.Value != ownerClientId ||
                _letterOrderId.Value != orderId)
                return;
            _hasLetter.Value = false;
            _letterOwner.Value = ulong.MaxValue;
            _letterOrderId.Value = 0;
        }
    }

    public sealed class OrderLetterPickup : MonoBehaviour, IPlayerInteractable
    {
        private OrderDesk _desk;
        public void Initialize(OrderDesk desk) => _desk = desk;
        public string GetInteractionPrompt(NetworkPlayerController player) =>
            _desk != null ? _desk.GetInteractionPrompt(player) : string.Empty;
        public void Interact(NetworkPlayerController player) => _desk?.Interact(player);
    }
}
