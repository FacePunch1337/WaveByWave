using UnityEngine;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    public sealed class ShipTreasureChest : MonoBehaviour, IPlayerInteractable
    {
        [SerializeField] private Vector3 levelBarOffset = new(0f, 1.8f, 0f);
        private ShipCannonBattery _battery;

        private void OnGUI()
        {
            _battery ??= GetComponentInParent<ShipCannonBattery>();
            if (_battery == null || !_battery.IsSpawned || Camera.main == null) return;
            var screen = Camera.main.WorldToScreenPoint(transform.TransformPoint(levelBarOffset));
            if (screen.z <= 0f) return;
            var rect = new Rect(screen.x - 70f, Screen.height - screen.y - 30f, 140f, 28f);
            GUI.Box(rect, $"Уровень {_battery.CrewLevel}");
            var bar = new Rect(rect.x + 5f, rect.y + 20f, 130f, 5f);
            var previous = GUI.color;
            GUI.color = new Color(0.1f, 0.1f, 0.1f, 0.85f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            bar.width *= _battery.LevelProgress;
            GUI.color = new Color(1f, 0.78f, 0.24f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        public string GetInteractionPrompt(NetworkPlayerController player) => "Сдать выбранное сокровище [E]";
        public void Interact(NetworkPlayerController player)
        {
            var battery = GetComponentInParent<ShipCannonBattery>();
            if (player != null && player.IsOwner && battery != null && battery.IsSpawned)
                battery.DepositTreasureServerRpc(player.Inventory.SelectedIndex);
        }
    }
}
