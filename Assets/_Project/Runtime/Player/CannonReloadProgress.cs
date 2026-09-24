using UnityEngine;
using UnityEngine.UI;
using WaveByWave.UI;

namespace WaveByWave.Player
{
    [DefaultExecutionOrder(9500)]
    [DisallowMultipleComponent]
    public sealed class CannonReloadProgress : MonoBehaviour
    {
        private NetworkPlayerController _player;
        private GameObject _indicator;
        public static readonly Vector2 AimViewportPoint = new(0.5f, 0.5f);
        private GameObject _hud;
        private Image _fill;
        private Texture2D _ringTexture;
        private Sprite _ringSprite;

        public void Initialize(NetworkPlayerController player)
        {
            _player = player;
            if (_hud != null) return;
            _hud=GameUiPrefabs.Create("HUD/AimAndReload", owner: this);
            if(_hud!=null)
            {
                GameUiPrefabs.Persist(_hud);
                _indicator=_hud.transform.Find("Aim Center/Cannon Reload Progress").gameObject;
                _fill=GameUiPrefabs.Find<Image>(_indicator,"Reload Fill");
                return;
            }
            _hud = new GameObject("Player Aim HUD", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            if(Application.isPlaying) DontDestroyOnLoad(_hud);
            var canvas = _hud.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 70;
            var scaler = _hud.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var center = new GameObject("Aim Center", typeof(RectTransform));
            center.transform.SetParent(_hud.transform, false);
            var centerRect = (RectTransform)center.transform;
            centerRect.anchorMin = centerRect.anchorMax = AimViewportPoint;
            centerRect.pivot = AimViewportPoint;
            centerRect.sizeDelta = Vector2.zero;
            centerRect.anchoredPosition = Vector2.zero;
            AddImage("Crosshair Outline", center.transform, 8f, new Color(0f, 0f, 0f, 0.8f));
            AddImage("Crosshair", center.transform, 4f, Color.white);

            _indicator = new GameObject("Cannon Reload Progress", typeof(RectTransform));
            _indicator.transform.SetParent(center.transform, false);
            var rect = (RectTransform)_indicator.transform;
            rect.anchorMin = rect.anchorMax = AimViewportPoint;
            rect.sizeDelta = Vector2.one * 64f;
            rect.pivot = AimViewportPoint;
            rect.anchoredPosition = Vector2.zero;
            BuildRingSprite();
            var background = AddImage("Ring Background", _indicator.transform, 64f,
                new Color(0.035f, 0.055f, 0.075f, 0.9f));
            background.sprite = _ringSprite;
            _fill = AddImage("Reload Fill", _indicator.transform, 64f, new Color(1f, 0.67f, 0.25f));
            _fill.sprite = _ringSprite;
            _fill.type = Image.Type.Filled;
            _fill.fillMethod = Image.FillMethod.Radial360;
            _fill.fillOrigin = (int)Image.Origin360.Top;
            _fill.fillClockwise = true;
            _fill.fillAmount = 0f;
            _indicator.SetActive(false);
            _hud.SetActive(false);
        }

        private static Image AddImage(string name, Transform parent, float size, Color color)
        {
            var graphic = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            graphic.transform.SetParent(parent, false);
            var rect = (RectTransform)graphic.transform;
            rect.anchorMin = rect.anchorMax = AimViewportPoint;
            rect.pivot = AimViewportPoint;
            rect.sizeDelta = Vector2.one * size;
            rect.anchoredPosition = Vector2.zero;
            var image = graphic.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private void BuildRingSprite()
        {
            const int size = 128;
            _ringTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "HUD Reload Ring", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var radius = new Vector2(x + 0.5f - size * 0.5f, y + 0.5f - size * 0.5f).magnitude;
                var alpha = Mathf.Clamp01(Mathf.Min(61f - radius, radius - 51f));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }
            _ringTexture.SetPixels32(pixels);
            _ringTexture.Apply(false, true);
            _ringSprite = Sprite.Create(_ringTexture, new Rect(0f, 0f, size, size), AimViewportPoint,
                100f, 0, SpriteMeshType.FullRect);
        }

        private void LateUpdate()
        {
            if (_hud == null) return;
            var visible = _player != null && _player.IsOwner && _player.IsSpawned &&
                _player.OwnerView != null && _player.OwnerView.gameObject.activeInHierarchy &&
                !PlayerEquipment.InputCaptured;
            _hud.SetActive(visible);
            if (!visible) return;

            // The crosshair and the interaction ray use this same camera viewport center.
            var camera = _player.OwnerView.GetComponent<Camera>();
            var viewport = camera != null ? camera.rect : new Rect(0f, 0f, 1f, 1f);
            var center = (RectTransform)_indicator.transform.parent;
            center.anchorMin = center.anchorMax = viewport.position + Vector2.Scale(viewport.size, AimViewportPoint);
            var cannon = _player.ActiveCannon;
            var battery = cannon != null ? cannon.Battery : null;
            var loading = battery != null && battery.IsSpawned;
            var state = loading ? battery.GetState(battery.GetCannonIndex(cannon)) : default;
            loading = loading && state.Operator == _player.OwnerClientId && state.ReloadEnd > 0d;
            var equipment = _player.GetComponent<PlayerEquipment>();
            var handheld = equipment != null && !_player.IsAtControlStation &&
                _player.Inventory.TryGetDefinition(_player.Inventory.SelectedIndex, out var item) &&
                item.EquipmentKind == WaveByWave.Items.ItemEquipmentKind.Musket && equipment.Reloading;
            var charge = equipment != null && equipment.ChargingHook;
            loading = loading || handheld || charge;
            _indicator.SetActive(loading);
            if (!loading) return;
            if (handheld || charge)
            {
                _fill.fillAmount = charge ? equipment.HookCharge : equipment.ReloadProgress;
                return;
            }
            // Use authoritative reload time, independently of delayed ship presentation.
            var remaining = state.ReloadEnd - battery.NetworkManager.ServerTime.Time;
            _fill.fillAmount = Mathf.Clamp01(1f - (float)remaining / Mathf.Max(0.01f, state.ReloadDuration));
        }

        private void OnDisable()
        {
            if (_hud != null) _hud.SetActive(false);
        }

        private void OnDestroy()
        {
            if (!Application.isPlaying) return;
            if (_hud != null) GameUiPrefabs.Release(_hud, this);
            if (_ringSprite != null) Destroy(_ringSprite);
            if (_ringTexture != null) Destroy(_ringTexture);
        }
    }
}
