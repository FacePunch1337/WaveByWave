using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using WaveByWave.UI;

namespace WaveByWave.Player
{
    [DefaultExecutionOrder(-200)]
    public sealed class EquipmentAdminPanel : MonoBehaviour
    {
        public static bool InputCaptured { get; private set; }
        private PlayerInventory _inventory;
        private GameObject _canvas, _panel;
        public void Initialize(PlayerInventory inventory) => _inventory = inventory;
        private void Update()
        {
            if (_inventory == null || !_inventory.IsSpawned || !_inventory.IsOwner || !_inventory.IsHost) return;
            if (SessionMenuPresenter.InputCaptured && InputCaptured) SetOpen(false);
            if (!SessionMenuPresenter.InputCaptured && Keyboard.current != null && Keyboard.current.f2Key.wasPressedThisFrame)
            {
                if (_canvas == null) Build();
                SetOpen(!InputCaptured);
            }
        }
        private void Build()
        {
            _canvas = new GameObject("Admin item spawner", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            DontDestroyOnLoad(_canvas);
            _canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.GetComponent<Canvas>().sortingOrder = 200;
            var scaler = _canvas.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            _panel = new GameObject("Items", typeof(RectTransform), typeof(Image));
            _panel.transform.SetParent(_canvas.transform, false);
            var rect = _panel.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = Vector2.one * 0.5f;
            rect.sizeDelta = new Vector2(620f, 720f); _panel.GetComponent<Image>().color = new Color(0.035f, 0.055f, 0.075f, 0.98f);
            Label(_panel.transform, "Предметы — спавн перед игроком • F2", new Vector2(0, 320), new Vector2(570, 40), Color.white);
            var scrollObject = new GameObject("Catalog", typeof(RectTransform), typeof(Image), typeof(Mask), typeof(ScrollRect));
            scrollObject.transform.SetParent(_panel.transform, false);
            var scrollRect = scrollObject.GetComponent<RectTransform>(); scrollRect.sizeDelta = new Vector2(570f, 590f);
            scrollRect.anchoredPosition = new Vector2(0f, -10f);
            scrollObject.GetComponent<Image>().color = new Color(0, 0, 0, 0.1f);
            scrollObject.GetComponent<Mask>().showMaskGraphic = false;
            var contentObject = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentObject.transform.SetParent(scrollObject.transform, false);
            var content = contentObject.GetComponent<RectTransform>(); content.anchorMin = new Vector2(0, 1);
            content.anchorMax = Vector2.one; content.pivot = new Vector2(0.5f, 1); content.sizeDelta = Vector2.zero;
            var layout = contentObject.GetComponent<VerticalLayoutGroup>(); layout.spacing = 6f;
            layout.padding = new RectOffset(8, 8, 8, 8); layout.childControlHeight = true;
            layout.childForceExpandHeight = false; layout.childControlWidth = true;
            contentObject.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scroll = scrollObject.GetComponent<ScrollRect>(); scroll.content = content; scroll.viewport = scrollRect;
            scroll.horizontal = false; scroll.scrollSensitivity = 30f;
            if (_inventory.Catalog != null)
                foreach (var item in _inventory.Catalog.Items)
                {
                    if (item == null) continue;
                    var id = item.Id;
                    var button = new GameObject(id, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
                    button.transform.SetParent(content, false); button.GetComponent<LayoutElement>().preferredHeight = 42f;
                    button.GetComponent<Image>().color = new Color(0.1f, 0.15f, 0.19f, 1f);
                    button.GetComponent<Button>().targetGraphic = button.GetComponent<Image>();
                    button.GetComponent<Button>().onClick.AddListener(() => _inventory.SpawnAdminItem(id));
                    var label = Label(button.transform, item.DisplayName + "  •  " + item.Category + " / " + item.Rarity,
                        Vector2.zero, new Vector2(530, 38), item.RarityColor);
                    label.fontSize = 17;
                }
            var close = new GameObject("Close", typeof(RectTransform), typeof(Image), typeof(Button));
            close.transform.SetParent(_panel.transform, false); close.GetComponent<RectTransform>().sizeDelta = new Vector2(170, 35);
            close.GetComponent<RectTransform>().anchoredPosition = new Vector2(0, -330);
            close.GetComponent<Image>().color = new Color(0.25f, 0.32f, 0.37f);
            close.GetComponent<Button>().targetGraphic = close.GetComponent<Image>();
            close.GetComponent<Button>().onClick.AddListener(() => SetOpen(false));
            Label(close.transform, "Закрыть", Vector2.zero, new Vector2(160, 30), Color.white);
        }
        private static Text Label(Transform parent, string value, Vector2 position, Vector2 size, Color color)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(Text)); go.transform.SetParent(parent, false);
            go.GetComponent<RectTransform>().sizeDelta = size; go.GetComponent<RectTransform>().anchoredPosition = position;
            var text = go.GetComponent<Text>(); text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 21; text.alignment = TextAnchor.MiddleCenter; text.text = value; text.color = color;
            text.raycastTarget = false; return text;
        }
        private void SetOpen(bool open)
        {
            InputCaptured = open; if (_canvas != null) _canvas.SetActive(open);
            var unlocked = open || SessionMenuPresenter.InputCaptured;
            Cursor.visible = unlocked; Cursor.lockState = unlocked ? CursorLockMode.None : CursorLockMode.Locked;
        }
        private void OnDestroy()
        {
            if (InputCaptured) SetOpen(false);
            if (_canvas != null) Destroy(_canvas);
        }
    }
}
