using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using WaveByWave.Player;

namespace WaveByWave.Items
{
    [RequireComponent(typeof(NetworkObject), typeof(Collider))]
    public sealed class WorldItem : NetworkBehaviour, IPlayerInteractable
    {
        private readonly NetworkVariable<FixedString64Bytes> _itemId = new();
        private readonly NetworkVariable<ushort> _amount = new(1);

        public FixedString64Bytes ItemId => _itemId.Value;
        public ushort Amount => _amount.Value;

        public void SetState(FixedString64Bytes itemId, ushort amount)
        {
            if (!IsServer && IsSpawned)
                return;

            _itemId.Value = itemId;
            _amount.Value = amount;
        }

        public string GetInteractionPrompt(NetworkPlayerController player) => $"Подобрать {_itemId.Value}";

        public void Interact(NetworkPlayerController player)
        {
            if (player != null && player.IsOwner)
                player.Inventory.PickupServerRpc(new NetworkObjectReference(NetworkObject));
        }
    }
}
