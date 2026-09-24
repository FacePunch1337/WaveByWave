using UnityEngine;
using WaveByWave.UI;
using UnityEngine.UI;
using WaveByWave.Items;

namespace WaveByWave.Player
{
    public sealed class EquipmentHud : MonoBehaviour
    {
        private PlayerEquipment _equipment;
        private PlayerInventory _inventory;
        private GameObject _hud;
        private GameObject _staminaHud;
        private StaminaBarView _stamina;
        private Text _state;

        public void Initialize(PlayerEquipment equipment, PlayerInventory inventory)
        {
            _equipment = equipment; _inventory = inventory;
            _hud = GameUiPrefabs.Create("HUD/Equipment", owner: this) ?? BuildTemplate();
            _staminaHud = GameUiPrefabs.Create("HUD/Stamina", owner: this) ?? StaminaBarView.BuildTemplate();
            _stamina = _staminaHud.GetComponent<StaminaBarView>();
            _state = GameUiPrefabs.Find<Text>(_hud,"Tool state");
            if (Application.isPlaying)
            {
                GameUiPrefabs.Persist(_hud);
                GameUiPrefabs.Persist(_staminaHud);
            }
        }

        public static GameObject BuildTemplate()
        {
            var root = UiDefaults.Canvas("Equipment HUD", 65);
            var state = new GameObject("Tool state", typeof(RectTransform), typeof(Text));
            state.transform.SetParent(root.transform, false);
            var stateRect = state.GetComponent<RectTransform>(); stateRect.anchorMin = stateRect.anchorMax = Vector2.one * 0.5f;
            stateRect.anchoredPosition = new Vector2(0f, -60f); stateRect.sizeDelta = new Vector2(260f, 30f);
            var label = state.GetComponent<Text>(); label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 17; label.alignment = TextAnchor.MiddleCenter; label.color = Color.white; label.raycastTarget = false;
            return root;
        }
        private void Update()
        {
            if (_hud == null) return;
            var visible = _equipment != null && _equipment.IsSpawned && _equipment.IsOwner && !PlayerEquipment.InputCaptured;
            _hud.SetActive(visible);
            if (_staminaHud != null) _staminaHud.SetActive(visible);
            if (!visible) return;
            _stamina.SetValue(_equipment.Stamina, _equipment.MaximumStamina);
            _state.text = _inventory.TryGetDefinition(_inventory.SelectedIndex, out var item) &&
                item.EquipmentKind == ItemEquipmentKind.Bucket ? (_equipment.BucketFull
                    ? $"Ведро: {_equipment.BucketLitres:0.#} л — ЛКМ: выплеснуть" : "ЛКМ: зачерпнуть воду") : "";
        }
        private void OnDestroy()
        {
            if (!Application.isPlaying) return;
            if (_staminaHud != null) GameUiPrefabs.Release(_staminaHud, this);
            if (_hud != null) GameUiPrefabs.Release(_hud, this);
        }
    }
}
