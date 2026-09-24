using UnityEngine;
using UnityEngine.UI;

namespace WaveByWave.UI
{
    public sealed class StaminaBarView : MonoBehaviour
    {
        [SerializeField] private Image fill;
        [SerializeField] private Text valueLabel;
        [SerializeField, Range(0f, 1f)] private float lowStaminaThreshold = .2f;
        [SerializeField] private Color lowStaminaColor = new(1f, .35f, .2f, 1f);
        private Color _normalColor;
        private bool _ownsSprite;

        private void Awake()
        {
            if (fill != null) _normalColor = fill.color;
        }

        public void SetValue(float current, float maximum)
        {
            if (valueLabel != null) valueLabel.text = $"{Mathf.CeilToInt(current)}/{Mathf.CeilToInt(maximum)}";
            if (fill == null) return;
            fill.fillAmount = Mathf.Clamp01(current / Mathf.Max(1f, maximum));
            fill.color = fill.fillAmount < lowStaminaThreshold ? lowStaminaColor : _normalColor;
        }

        public static GameObject BuildTemplate()
        {
            var root = UiDefaults.Canvas("Stamina HUD", 65);
            var back = UiDefaults.Image("Stamina", root.transform, new Color(.02f, .035f, .04f, .8f));
            var rect = back.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, 0f);
            rect.pivot = new Vector2(.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 130f);
            rect.sizeDelta = new Vector2(300f, 10f);
            back.raycastTarget = false;
            var image = UiDefaults.Image("Stamina fill", back.transform, new Color(.35f, .9f, .65f));
            UiDefaults.Stretch(image.rectTransform, 2);
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Horizontal;
            image.raycastTarget = false;
            image.sprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 1, 1), Vector2.one * .5f);
            var view = root.AddComponent<StaminaBarView>();
            view.fill = image;
            view._normalColor = image.color;
            view._ownsSprite = true;
            return root;
        }

        private void OnDestroy()
        {
            if (Application.isPlaying && _ownsSprite && fill != null && fill.sprite != null)
                Destroy(fill.sprite);
        }
    }
}
