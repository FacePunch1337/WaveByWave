using UnityEngine;
using UnityEngine.UI;
using WaveByWave.Player;

namespace WaveByWave.Commerce
{
    public sealed class CommerceHud : MonoBehaviour
    {
        private PlayerInventory _inventory;
        private GameObject _canvas;
        private Text _label;

        public void Initialize(PlayerInventory inventory)
        {
            _inventory = inventory;
            _canvas = new GameObject("Money HUD", typeof(Canvas), typeof(CanvasScaler));
            DontDestroyOnLoad(_canvas);
            _canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.GetComponent<Canvas>().sortingOrder = 55;
            var scaler = _canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var panel = new GameObject("Balance", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_canvas.transform, false);
            var rect = (RectTransform)panel.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-28f, -28f);
            rect.sizeDelta = new Vector2(330f, 86f);
            panel.GetComponent<Image>().color = new Color(0.035f, 0.055f, 0.075f, 0.9f);

            var text = new GameObject("Text", typeof(RectTransform), typeof(Text));
            text.transform.SetParent(panel.transform, false);
            var textRect = (RectTransform)text.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(14f, 8f);
            textRect.offsetMax = new Vector2(-14f, -8f);
            _label = text.GetComponent<Text>();
            _label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _label.fontSize = 20;
            _label.alignment = TextAnchor.MiddleRight;
            _label.color = new Color(1f, 0.82f, 0.32f);

            _inventory.CommerceChanged += Refresh;
            Refresh();
        }

        private void Refresh()
        {
            if (_inventory == null || _label == null)
                return;
            _label.text = $"Баланс: {_inventory.Balance:N0} монет\n{_inventory.GetOrderStatusText()}";
        }

        private void OnDestroy()
        {
            if (_inventory != null)
                _inventory.CommerceChanged -= Refresh;
            if (_canvas != null)
                Destroy(_canvas);
        }
    }
}
