using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using WaveByWave.Items;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    // The repair ring follows the breach and matches CannonReloadProgress.
    public sealed class ShipRepairPresentation : MonoBehaviour
    {
        private ShipFlooding _ship;
        private GameObject _hud;
        private RectTransform _ringRect;
        private Image _ringFill;
        private Texture2D _texture;
        private Sprite _sprite;

        public void Initialize(ShipFlooding ship) => _ship = ship;

        private void LateUpdate()
        {
            if (_ship == null || !_ship.IsSpawned || _ship.Hull == null) { Hide(); return; }
            var playerObject = NetworkManager.Singleton?.LocalClient?.PlayerObject;
            var player = playerObject != null ? playerObject.GetComponent<NetworkPlayerController>() : null;
            if (player == null || player.OwnerView == null || player.Inventory == null ||
                PlayerEquipment.InputCaptured ||
                !player.Inventory.TryGetDefinition(player.Inventory.SelectedIndex, out var item) ||
                item.SupplyKind != SupplyKind.Plank ||
                !_ship.FindRepairTarget(player.OwnerView.position, player.OwnerView.forward, out var index))
            { Hide(); return; }

            var camera = player.OwnerView.GetComponent<Camera>();
            if (camera == null) { Hide(); return; }
            var hole = _ship.GetHole(index);
            var screen = camera.WorldToScreenPoint(_ship.Hull.transform.TransformPoint(hole.Position));
            if (screen.z <= 0f) { Hide(); return; }
            EnsureRing();
            _hud.SetActive(true);
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)_hud.transform,
                    screen, null, out var at))
                _ringRect.anchoredPosition = at;
            _ringFill.fillAmount = hole.Repair;
        }

        private void Hide()
        { if (_hud != null) _hud.SetActive(false); }

        private void EnsureRing()
        {
            if (_hud != null) return;
            const int size = 128;
            _texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { name = "HUD Repair Ring", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
            {
                var radius = new Vector2(x + .5f - size * .5f, y + .5f - size * .5f).magnitude;
                var alpha = Mathf.Clamp01(Mathf.Min(61f - radius, radius - 51f));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }
            _texture.SetPixels32(pixels);
            _texture.Apply(false, true);
            _sprite = Sprite.Create(_texture, new Rect(0f, 0f, size, size), Vector2.one * .5f,
                100f, 0, SpriteMeshType.FullRect);

            _hud = new GameObject("Repair progress", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            var canvas = _hud.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 70;
            var scaler = _hud.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = .5f;

            var back = new GameObject("Repair ring", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            back.transform.SetParent(_hud.transform, false);
            _ringRect = back.GetComponent<RectTransform>();
            _ringRect.anchorMin = _ringRect.anchorMax = Vector2.one * .5f;
            _ringRect.pivot = Vector2.one * .5f;
            _ringRect.sizeDelta = Vector2.one * 64f;
            var background = back.GetComponent<Image>();
            background.sprite = _sprite;
            background.color = new Color(.035f, .055f, .075f, .9f);
            background.raycastTarget = false;

            var fill = new GameObject("Repair fill", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            fill.transform.SetParent(back.transform, false);
            var fillRect = fill.GetComponent<RectTransform>();
            fillRect.anchorMin = fillRect.anchorMax = Vector2.one * .5f;
            fillRect.pivot = Vector2.one * .5f;
            fillRect.sizeDelta = Vector2.one * 64f;
            _ringFill = fill.GetComponent<Image>();
            _ringFill.sprite = _sprite;
            _ringFill.color = new Color(1f, .67f, .25f);
            _ringFill.type = Image.Type.Filled;
            _ringFill.fillMethod = Image.FillMethod.Radial360;
            _ringFill.fillOrigin = (int)Image.Origin360.Top;
            _ringFill.fillClockwise = true;
            _ringFill.fillAmount = 0f;
            _ringFill.raycastTarget = false;
            _hud.SetActive(false);
        }

        private void OnDestroy()
        {
            if (_hud != null) Destroy(_hud);
            if (_sprite != null) Destroy(_sprite);
            if (_texture != null) Destroy(_texture);
        }
    }
}
