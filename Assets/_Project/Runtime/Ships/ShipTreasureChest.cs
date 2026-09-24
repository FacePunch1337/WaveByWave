using UnityEngine;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    public sealed class ShipTreasureChest : MonoBehaviour, IPlayerInteractable
    {
        [SerializeField] private Vector3 levelBarOffset = new(0f, 1.8f, 0f);
        private ShipCannonBattery _battery;

        private GameObject _levelHud;
        private void Update()
        {
            _battery ??= GetComponentInParent<ShipCannonBattery>();
            var visible=_battery!=null&&_battery.IsSpawned&&!_battery.VoyageEnded&&Camera.main!=null;
            if(!visible){if(_levelHud!=null)_levelHud.SetActive(false);return;}
            if(_levelHud==null)_levelHud=WaveByWave.UI.GameUiPrefabs.Create("World/TreasureLevel",transform)??WaveByWave.UI.VoyageHud.BuildTreasureLevel();
            _levelHud.SetActive(true);
            _levelHud.transform.SetPositionAndRotation(transform.TransformPoint(levelBarOffset),Camera.main.transform.rotation);
            WaveByWave.UI.GameUiPrefabs.Find<UnityEngine.UI.Text>(_levelHud,"Label").text=$"Уровень {_battery.CrewLevel}";
            WaveByWave.UI.GameUiPrefabs.Find<RectTransform>(_levelHud,"Progress").anchorMax=new Vector2(_battery.LevelProgress,0);
        }
        private void OnDestroy(){if(_levelHud!=null)Destroy(_levelHud);}

        public string GetInteractionPrompt(NetworkPlayerController player) => "Сдать выбранное сокровище [ЛКМ]";
        public void Interact(NetworkPlayerController player)
        {
            var battery = GetComponentInParent<ShipCannonBattery>();
            if (player != null && player.IsOwner && player.Inventory != null &&
                !player.Inventory.IsCarryingChest &&
                player.Inventory.TryGetDefinition(player.Inventory.SelectedIndex, out var selectedItem) &&
                selectedItem.Category == WaveByWave.Items.ItemCategory.Treasure &&
                battery != null && battery.IsSpawned)
                battery.DepositTreasureServerRpc(player.Inventory.SelectedIndex);
        }
    }
}
