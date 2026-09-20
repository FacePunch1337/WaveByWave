using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace WaveByWave.Player
{
    public sealed class InventoryHud : MonoBehaviour
    {
        private readonly List<Image> _backgrounds = new();
        private readonly List<Text> _labels = new();
        private PlayerInventory _inventory;
        private GameObject _canvasObject;
        private Font _font;

        public void Initialize(PlayerInventory inventory)
        {
            _inventory = inventory;
            Build();
            _inventory.Changed += Refresh;
            Refresh();
        }

        private void Build()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _canvasObject = new GameObject("Inventory HUD", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            DontDestroyOnLoad(_canvasObject);
            var canvas = _canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;

            var scaler = _canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var bar = new GameObject("Slots", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            bar.transform.SetParent(_canvasObject.transform, false);
            var barRect = (RectTransform)bar.transform;
            barRect.anchorMin = new Vector2(0.5f, 0f);
            barRect.anchorMax = new Vector2(0.5f, 0f);
            barRect.pivot = new Vector2(0.5f, 0f);
            barRect.anchoredPosition = new Vector2(0f, 24f);
            barRect.sizeDelta = new Vector2(_inventory.Capacity * 112f, 96f);
            var layout = bar.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = false;

            for (var i = 0; i < _inventory.Capacity; i++)
            {
                var slot = new GameObject($"Slot {i + 1}", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
                slot.transform.SetParent(bar.transform, false);
                slot.GetComponent<RectTransform>().sizeDelta = new Vector2(104f, 88f);
                var image = slot.GetComponent<Image>();
                image.color = new Color(0.035f, 0.055f, 0.075f, 0.9f);
                var element = slot.GetComponent<LayoutElement>();
                element.preferredWidth = 104f;
                element.preferredHeight = 88f;
                _backgrounds.Add(image);

                var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
                textObject.transform.SetParent(slot.transform, false);
                var rect = (RectTransform)textObject.transform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = new Vector2(5f, 5f);
                rect.offsetMax = new Vector2(-5f, -5f);
                var label = textObject.GetComponent<Text>();
                label.font = _font;
                label.fontSize = 16;
                label.alignment = TextAnchor.MiddleCenter;
                label.color = Color.white;
                _labels.Add(label);
            }
        }

        private void Refresh()
        {
            if (_inventory == null)
                return;

            for (var i = 0; i < _labels.Count; i++)
            {
                var slot = _inventory.GetSlot(i);
                _labels[i].text = slot.IsEmpty
                    ? $"{i + 1}\n—"
                    : $"{i + 1}\n{_inventory.GetDisplayName(slot)}{(slot.Amount > 1 ? $" ×{slot.Amount}" : "")}";
                _labels[i].color = _inventory.TryGetDefinition(i, out var definition)
                    ? definition.RarityColor : Color.white;
                _backgrounds[i].color = i == _inventory.SelectedIndex
                    ? new Color(0.75f, 0.42f, 0.08f, 0.95f)
                    : new Color(0.035f, 0.055f, 0.075f, 0.9f);
            }
        }

        private void LateUpdate()
        {
            if (_canvasObject != null)
                _canvasObject.SetActive(!PlayerEquipment.InputCaptured);
        }

        private void OnDestroy()
        {
            if (_inventory != null)
                _inventory.Changed -= Refresh;
            if (_canvasObject != null)
                Destroy(_canvasObject);
        }
    }
}
