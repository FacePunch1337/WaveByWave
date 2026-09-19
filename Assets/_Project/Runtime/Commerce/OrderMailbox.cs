using Unity.Netcode;
using UnityEngine;
using WaveByWave.Player;

namespace WaveByWave.Commerce
{
    [RequireComponent(typeof(NetworkObject))]
    public sealed class OrderMailbox : NetworkBehaviour, IPlayerInteractable
    {
        [SerializeField] private OrderDeliveryCart deliveryCart;
        [SerializeField] private bool createTemporaryVisual = true;
        [SerializeField] private Vector3 temporaryVisualScale = new(0.65f, 1.1f, 0.45f);

        public OrderDeliveryCart DeliveryCart => deliveryCart;

        private void Awake()
        {
            if (!createTemporaryVisual || GetComponentInChildren<Renderer>() != null)
                return;
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "Temporary Mailbox Visual";
            visual.transform.SetParent(transform, false);
            visual.transform.localPosition = Vector3.up * temporaryVisualScale.y * 0.5f;
            visual.transform.localScale = temporaryVisualScale;
            visual.GetComponent<Collider>().enabled = false;
            visual.GetComponent<Renderer>().material.color = new Color(0.14f, 0.22f, 0.3f);
        }

        public string GetInteractionPrompt(NetworkPlayerController player) =>
            player != null && player.Inventory != null &&
            player.Inventory.OrderStatus == PurchaseOrderStatus.LetterHeld
                ? "Отправить письмо с заказом [E]"
                : "Почтовый ящик";

        public void Interact(NetworkPlayerController player)
        {
            if (player != null && player.IsOwner && player.Inventory != null)
                player.Inventory.RequestSubmitOrder(this);
        }
    }
}
