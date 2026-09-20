using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using WaveByWave.Player;
using WaveByWave.UI;

namespace WaveByWave.Customization
{
    public sealed class CustomizationMenu : MonoBehaviour
    {
        public static bool InputCaptured { get; private set; }

        private readonly Dictionary<PirateCustomizationCategory, Text> _values = new();
        private NetworkPlayerController _player;
        private NetworkPirateAppearance _appearance;
        private GameObject _canvas;

        public void Initialize(NetworkPlayerController player, NetworkPirateAppearance appearance)
        {
            _player = player;
            _appearance = appearance;
            Build();
            _appearance.Changed += Refresh;
            Refresh(_appearance.State);
            SetOpen(true);
        }

        public void CloseSilently()
        {
            SetOpen(false);
            Destroy(this);
        }

        private void Build()
        {
            _canvas = new GameObject("Pirate Customization UI", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            _canvas.transform.SetParent(transform, false);
            var canvas = _canvas.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 180;
            var scaler = _canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var panel = new GameObject("Wardrobe", typeof(RectTransform), typeof(Image),
                typeof(VerticalLayoutGroup));
            panel.transform.SetParent(_canvas.transform, false);
            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.67f, 0.08f);
            panelRect.anchorMax = new Vector2(0.97f, 0.92f);
            panelRect.offsetMin = panelRect.offsetMax = Vector2.zero;
            panel.GetComponent<Image>().color = new Color(0.025f, 0.045f, 0.065f, 0.94f);
            var layout = panel.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(28, 28, 28, 28);
            layout.spacing = 14f;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;

            CreateLabel(panel.transform, "ПИРАТСКИЙ ГАРДЕРОБ", 32, 64f, FontStyle.Bold);
            CreateLabel(panel.transform, "Изменения сразу видны всем игрокам", 18, 34f, FontStyle.Normal,
                new Color(0.65f, 0.78f, 0.86f));

            CreateRow(panel.transform, PirateCustomizationCategory.Pirate, "Персонаж");
            CreateRow(panel.transform, PirateCustomizationCategory.Hair, "Волосы");
            CreateRow(panel.transform, PirateCustomizationCategory.Bandana, "Бандана");
            CreateRow(panel.transform, PirateCustomizationCategory.Hat, "Шляпа");
            CreateRow(panel.transform, PirateCustomizationCategory.Coat, "Одежда");

            var spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
            spacer.transform.SetParent(panel.transform, false);
            spacer.GetComponent<LayoutElement>().flexibleHeight = 1f;

            var save = CreateButton(panel.transform, "СОХРАНИТЬ И ВЫЙТИ", 58f,
                new Color(0.12f, 0.55f, 0.43f));
            save.onClick.AddListener(() =>
            {
                _appearance?.SaveLocal();
                _player?.ExitCustomization();
            });
            CreateLabel(panel.transform, "Esc — выйти без отдельного сохранения", 16, 28f,
                FontStyle.Normal, new Color(0.55f, 0.65f, 0.7f));
        }

        private void CreateRow(Transform parent, PirateCustomizationCategory category, string title)
        {
            var row = new GameObject(title, typeof(RectTransform), typeof(Image), typeof(LayoutElement),
                typeof(HorizontalLayoutGroup));
            row.transform.SetParent(parent, false);
            row.GetComponent<Image>().color = new Color(0.075f, 0.11f, 0.14f, 0.96f);
            row.GetComponent<LayoutElement>().preferredHeight = 82f;
            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 8, 8);
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = false;

            var titleLabel = CreateLabel(row.transform, title, 19, 58f, FontStyle.Bold);
            titleLabel.GetComponent<LayoutElement>().preferredWidth = 150f;
            var left = CreateButton(row.transform, "‹", 54f, new Color(0.18f, 0.27f, 0.33f), 58f);
            var value = CreateLabel(row.transform, "—", 20, 58f, FontStyle.Normal);
            value.GetComponent<LayoutElement>().preferredWidth = 150f;
            value.GetComponent<LayoutElement>().flexibleWidth = 1f;
            _values[category] = value;
            var right = CreateButton(row.transform, "›", 54f, new Color(0.18f, 0.27f, 0.33f), 58f);
            left.onClick.AddListener(() => _appearance?.Cycle(category, -1));
            right.onClick.AddListener(() => _appearance?.Cycle(category, 1));
        }

        private static Text CreateLabel(Transform parent, string value, int size, float height,
            FontStyle style, Color? color = null)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<LayoutElement>().preferredHeight = height;
            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = TextAnchor.MiddleCenter;
            text.text = value;
            text.color = color ?? Color.white;
            text.raycastTarget = false;
            return text;
        }

        private static Button CreateButton(Transform parent, string label, float height, Color color,
            float width = -1f)
        {
            var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button),
                typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var element = go.GetComponent<LayoutElement>();
            element.preferredHeight = height;
            if (width > 0f)
                element.preferredWidth = width;
            var image = go.GetComponent<Image>();
            image.color = color;
            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            CreateLabel(go.transform, label, width > 0f ? 34 : 20, height, FontStyle.Bold);
            return button;
        }

        private void Refresh(PirateAppearanceState state)
        {
            if (_appearance == null)
                return;
            foreach (var pair in _values)
                pair.Value.text = _appearance.GetOptionName(pair.Key, state.Get(pair.Key));
        }

        private void SetOpen(bool open)
        {
            InputCaptured = open;
            if (_canvas != null)
                _canvas.SetActive(open);
            var unlocked = open || SessionMenuPresenter.InputCaptured || EquipmentAdminPanel.InputCaptured;
            Cursor.visible = unlocked;
            Cursor.lockState = unlocked ? CursorLockMode.None : CursorLockMode.Locked;
        }

        private void OnDestroy()
        {
            if (_appearance != null)
                _appearance.Changed -= Refresh;
            if (InputCaptured)
                SetOpen(false);
            if (_canvas != null)
                Destroy(_canvas);
        }
    }
}
