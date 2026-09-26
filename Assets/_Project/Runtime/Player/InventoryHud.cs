using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using WaveByWave.UI;

namespace WaveByWave.Player
{
    public sealed class InventoryHud : MonoBehaviour
    {
        private readonly List<InventorySlotView> _slots = new();
        private PlayerInventory _inventory;
        private GameObject _canvasObject;
        private Text _carriedLabel;

        public void Initialize(PlayerInventory inventory)
        {
            _inventory = inventory;
            _canvasObject = GameUiPrefabs.Create("HUD/Inventory", owner: this);
            if (_canvasObject == null)
            {
                Debug.LogError("Inventory HUD prefab is missing.", this);
                enabled = false;
                return;
            }
            GameUiPrefabs.Persist(_canvasObject);
            _carriedLabel = GameUiPrefabs.Find<Text>(_canvasObject, "Carried chest");
            var slots = _canvasObject.transform.Find("Slots");
            for (var i = 0; i < _inventory.Capacity; i++)
            {
                var slot = i < slots.childCount ? slots.GetChild(i) : Instantiate(slots.GetChild(slots.childCount - 1), slots, false);
                slot.gameObject.SetActive(true);
                _slots.Add(slot.GetComponent<InventorySlotView>());
            }
            for (var i = _inventory.Capacity; i < slots.childCount; i++) slots.GetChild(i).gameObject.SetActive(false);
            _inventory.Changed += Refresh;
            Refresh();
        }

        private void Refresh()
        {
            if (_inventory == null) return;
            if (_carriedLabel != null)
                _carriedLabel.text = _inventory.IsCarryingChest ? "СУНДУК В РУКАХ · Q — положить" : "";
            for (var i = 0; i < _slots.Count; i++)
            {
                var slot = _inventory.GetSlot(i);
                _inventory.TryGetDefinition(i, out var item);
                if (_slots[i] != null)
                    _slots[i].SetSlot(i, item, slot.Amount, i == _inventory.SelectedIndex && !_inventory.IsCarryingChest);
            }
        }

        private void LateUpdate()
        {
            if (_canvasObject != null)
                _canvasObject.SetActive(_inventory != null && _inventory.IsSpawned && !PlayerEquipment.InputCaptured);
        }

        private void OnDestroy()
        {
            if (!Application.isPlaying) return;
            if (_inventory != null) _inventory.Changed -= Refresh;
            GameUiPrefabs.Release(_canvasObject, this);
        }
    }
}
