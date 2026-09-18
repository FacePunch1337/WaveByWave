using UnityEngine;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    public sealed class ShipTreasureChest : MonoBehaviour, IPlayerInteractable
    {
        public string GetInteractionPrompt(NetworkPlayerController player) => "Сдать выбранное сокровище [E]";
        public void Interact(NetworkPlayerController player)
        {
            var battery = GetComponentInParent<ShipCannonBattery>();
            if (player != null && player.IsOwner && battery != null && battery.IsSpawned)
                battery.DepositTreasureServerRpc(player.Inventory.SelectedIndex);
        }
    }
}
