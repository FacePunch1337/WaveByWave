using UnityEngine;
using UnityEngine.UI;
using WaveByWave.Player;

namespace WaveByWave.Combat
{
    internal sealed class HealthBarView : MonoBehaviour
    {
        private NetworkHealth _health;
        private RectTransform _fill;
        private CanvasGroup _group;
        private Text _label;
        private Image _damageVignette;
        private bool _screenSpace;
        private float _height;
        private float _vignetteAlpha;
        private static Sprite _vignetteSprite;

        public static HealthBarView Create(NetworkHealth health, bool screenSpace, float height)
        {
            var root = new GameObject(screenSpace ? "Player Health HUD" : "World Health Bar",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup), typeof(HealthBarView));
            var view = root.GetComponent<HealthBarView>();
            view.Initialize(health, screenSpace, height);
            return view;
        }

        private void Initialize(NetworkHealth health, bool screenSpace, float height)
        {
            _health = health;
            _screenSpace = screenSpace;
            _height = height;
            if (screenSpace)
                DontDestroyOnLoad(gameObject);
            else
                transform.SetParent(health.transform, false);
            _group = GetComponent<CanvasGroup>();
            var canvas = GetComponent<Canvas>();
            canvas.renderMode = screenSpace ? RenderMode.ScreenSpaceOverlay : RenderMode.WorldSpace;
            canvas.sortingOrder = screenSpace ? 60 : 20;
            var scaler = GetComponent<CanvasScaler>();
            scaler.uiScaleMode = screenSpace ? CanvasScaler.ScaleMode.ScaleWithScreenSize : CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var rootRect = (RectTransform)transform;
            if (screenSpace)
            {
                rootRect.anchorMin = Vector2.zero;
                rootRect.anchorMax = Vector2.one;
                rootRect.offsetMin = Vector2.zero;
                rootRect.offsetMax = Vector2.zero;
                BuildBar(transform, new Vector2(0.5f, 0f), new Vector2(0f, 136f), new Vector2(420f, 34f), true);
                BuildDamageVignette();
            }
            else
            {
                rootRect.sizeDelta = new Vector2(2.4f, 0.24f);
                rootRect.localScale = Vector3.one * 0.01f;
                BuildBar(transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(240f, 24f), false);
            }
            SetValue(health.NormalizedHealth, health.CurrentHealth, health.MaximumHealth);
        }

        private void BuildBar(Transform parent, Vector2 anchor, Vector2 position, Vector2 size, bool label)
        {
            var frame = CreateImage("Health Frame", parent, new Color(0.02f, 0.025f, 0.035f, 0.92f));
            var frameRect = frame.rectTransform;
            frameRect.anchorMin = frameRect.anchorMax = anchor;
            frameRect.pivot = new Vector2(0.5f, 0.5f);
            frameRect.anchoredPosition = position;
            frameRect.sizeDelta = size;

            var background = CreateImage("Missing Health", frame.transform, new Color(0.22f, 0.035f, 0.04f, 0.95f));
            Stretch(background.rectTransform, 4f);
            var fill = CreateImage("Health", background.transform, new Color(0.96f, 0.96f, 0.92f, 1f));
            _fill = fill.rectTransform;
            _fill.anchorMin = Vector2.zero;
            _fill.anchorMax = Vector2.one;
            _fill.pivot = new Vector2(0f, 0.5f);
            _fill.offsetMin = Vector2.zero;
            _fill.offsetMax = Vector2.zero;

            if (!label)
                return;
            var textObject = new GameObject("Health Text", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(frame.transform, false);
            var textRect = (RectTransform)textObject.transform;
            Stretch(textRect, 0f);
            _label = textObject.GetComponent<Text>();
            _label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _label.fontSize = 19;
            _label.fontStyle = FontStyle.Bold;
            _label.alignment = TextAnchor.MiddleCenter;
            _label.color = new Color(0.05f, 0.06f, 0.07f, 1f);
            _label.raycastTarget = false;
        }

        private void BuildDamageVignette()
        {
            _damageVignette = CreateImage("Damage Vignette", transform, Color.clear);
            _damageVignette.sprite = GetVignetteSprite();
            _damageVignette.type = Image.Type.Simple;
            Stretch(_damageVignette.rectTransform, 0f);
            _damageVignette.transform.SetAsFirstSibling();
            _damageVignette.raycastTarget = false;
        }

        public void SetValue(float normalized, float current, float maximum)
        {
            if (_fill != null)
                _fill.anchorMax = new Vector2(Mathf.Clamp01(normalized), 1f);
            if (_label != null)
                _label.text = $"{Mathf.CeilToInt(current)} / {Mathf.CeilToInt(maximum)}";
        }

        public void SetDead(bool dead)
        {
            if (_group != null)
                _group.alpha = dead && !_screenSpace ? 0f : 1f;
            if (_label != null && dead)
                _label.text = "RESPAWNING…";
        }

        public void Flash()
        {
            if (_damageVignette != null)
                _vignetteAlpha = 0.78f;
        }

        private void LateUpdate()
        {
            if (_health == null)
            {
                Destroy(gameObject);
                return;
            }

            if (_screenSpace && _group != null)
            {
                _group.alpha = PlayerEquipment.InputCaptured ? 0f : 1f;
                _group.blocksRaycasts = !PlayerEquipment.InputCaptured;
            }

            if (!_screenSpace)
            {
                transform.position = _health.transform.position + Vector3.up * _height;
                var camera = Camera.main;
                if (camera != null)
                    transform.rotation = Quaternion.LookRotation(transform.position - camera.transform.position, camera.transform.up);
            }

            if (_damageVignette != null && _vignetteAlpha > 0f)
            {
                _vignetteAlpha = Mathf.MoveTowards(_vignetteAlpha, 0f, Time.unscaledDeltaTime * 2.8f);
                _damageVignette.color = new Color(0.72f, 0.01f, 0.015f, _vignetteAlpha);
            }
        }

        private static Sprite GetVignetteSprite()
        {
            if (_vignetteSprite != null)
                return _vignetteSprite;

            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Damage Vignette (Runtime)",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var nx = Mathf.Abs((x + 0.5f) / size * 2f - 1f);
                var ny = Mathf.Abs((y + 0.5f) / size * 2f - 1f);
                var edge = Mathf.Max(nx, ny);
                var alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.38f, 1f, edge));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            _vignetteSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
            _vignetteSprite.name = "Damage Vignette (Runtime)";
            _vignetteSprite.hideFlags = HideFlags.HideAndDontSave;
            return _vignetteSprite;
        }

        private static Image CreateImage(string name, Transform parent, Color color)
        {
            var child = new GameObject(name, typeof(RectTransform), typeof(Image));
            child.transform.SetParent(parent, false);
            var image = child.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static void Stretch(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }
    }
}
