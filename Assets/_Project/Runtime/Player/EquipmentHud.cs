using UnityEngine;
using UnityEngine.UI;
using WaveByWave.Items;

namespace WaveByWave.Player
{
    public sealed class EquipmentHud : MonoBehaviour
    {
        private PlayerEquipment _equipment;
        private PlayerInventory _inventory;
        private GameObject _hud;
        private Image _stamina;
        private Text _state;

        public void Initialize(PlayerEquipment equipment, PlayerInventory inventory)
        {
            _equipment = equipment; _inventory = inventory;
            _hud = new GameObject("Equipment HUD", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            DontDestroyOnLoad(_hud);
            _hud.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            _hud.GetComponent<Canvas>().sortingOrder = 65;
            var scaler = _hud.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            var back = new GameObject("Stamina", typeof(RectTransform), typeof(Image));
            back.transform.SetParent(_hud.transform, false);
            var rect = back.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f); rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 130f); rect.sizeDelta = new Vector2(300f, 10f);
            back.GetComponent<Image>().color = new Color(0.02f, 0.035f, 0.04f, 0.8f);
            back.GetComponent<Image>().raycastTarget = false;
            var fill = new GameObject("Stamina fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(back.transform, false);
            var fillRect = fill.GetComponent<RectTransform>(); fillRect.anchorMin = Vector2.zero; fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = new Vector2(2f, 2f); fillRect.offsetMax = new Vector2(-2f, -2f);
            _stamina = fill.GetComponent<Image>(); _stamina.type = Image.Type.Filled;
            _stamina.fillMethod = Image.FillMethod.Horizontal; _stamina.raycastTarget = false;
            // Image.Filled needs a sprite, including for a rectangular stamina bar.
            _stamina.sprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 1, 1), Vector2.one * 0.5f);
            var state = new GameObject("Tool state", typeof(RectTransform), typeof(Text));
            state.transform.SetParent(_hud.transform, false);
            var stateRect = state.GetComponent<RectTransform>(); stateRect.anchorMin = stateRect.anchorMax = Vector2.one * 0.5f;
            stateRect.anchoredPosition = new Vector2(0f, -60f); stateRect.sizeDelta = new Vector2(260f, 30f);
            _state = state.GetComponent<Text>(); _state.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _state.fontSize = 17; _state.alignment = TextAnchor.MiddleCenter; _state.color = Color.white; _state.raycastTarget = false;
        }
        private void Update()
        {
            if (_hud == null) return;
            var visible = _equipment != null && _equipment.IsSpawned && _equipment.IsOwner && !PlayerEquipment.InputCaptured;
            _hud.SetActive(visible); if (!visible) return;
            _stamina.fillAmount = _equipment.Stamina / Mathf.Max(1f, _equipment.MaximumStamina);
            _stamina.color = _stamina.fillAmount < 0.2f ? new Color(1f, 0.35f, 0.2f) : new Color(0.35f, 0.9f, 0.65f);
            _state.text = _inventory.TryGetDefinition(_inventory.SelectedIndex, out var item) &&
                item.EquipmentKind == ItemEquipmentKind.Bucket && _equipment.BucketFull ? "Ведро наполнено" : "";
        }
        private void OnDestroy()
        {
            if (_stamina != null && _stamina.sprite != null) Destroy(_stamina.sprite);
            if (_hud != null) Destroy(_hud);
        }
    }
}
