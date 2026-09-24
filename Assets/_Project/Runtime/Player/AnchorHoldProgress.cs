using UnityEngine;
using UnityEngine.UI;
using WaveByWave.UI;

namespace WaveByWave.Player
{
    // Per-owner world-space indicator; no shared facing state or networked UI.
    [DefaultExecutionOrder(9500)]
    [DisallowMultipleComponent]
    public sealed class AnchorHoldProgress : MonoBehaviour
    {
        private NetworkPlayerController _player;
        private GameObject _bar;
        private RectTransform _fill;

        public void Initialize(NetworkPlayerController player)
        {
            _player = player;
            if (_bar != null)
                return;

            _bar=GameUiPrefabs.Create("World/AnchorProgress",transform);
            if(_bar!=null)
            {
                _fill=GameUiPrefabs.Find<RectTransform>(_bar,"Fill");
                _bar.GetComponent<Canvas>().worldCamera=player.OwnerView!=null?player.OwnerView.GetComponent<Camera>():null;
                return;
            }
            _bar = new GameObject("Anchor Release Progress", typeof(RectTransform), typeof(Canvas), typeof(Image));
            _bar.transform.SetParent(transform, false);
            _bar.transform.localScale = Vector3.one * 0.003f;
            var rect = (RectTransform)_bar.transform;
            rect.sizeDelta = new Vector2(180f, 8f);
            var canvas = _bar.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = player.OwnerView != null ? player.OwnerView.GetComponent<Camera>() : null;
            var background = _bar.GetComponent<Image>();
            background.color = new Color(0.035f, 0.055f, 0.075f, 0.9f);
            background.raycastTarget = false;

            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(_bar.transform, false);
            _fill = (RectTransform)fill.transform;
            _fill.anchorMin = Vector2.zero;
            _fill.anchorMax = Vector2.one;
            _fill.offsetMin = _fill.offsetMax = Vector2.zero;
            var image = fill.GetComponent<Image>();
            image.color = new Color(1f, 0.62f, 0.18f);
            image.raycastTarget = false;
            _bar.SetActive(false);
        }

        private void LateUpdate()
        {
            if (_bar == null)
                return;
            var anchor = _player != null ? _player.AnchorBeingReleased : null;
            var view = _player != null ? _player.OwnerView : null;
            var visible = _player != null && _player.IsOwner && _player.IsSpawned &&
                          !PlayerEquipment.InputCaptured && view != null &&
                          anchor != null && anchor.Ship != null && anchor.Ship.IsSpawned &&
                          anchor.Ship.CanBeginAnchorDrop;
            _bar.SetActive(visible);
            if (!visible)
                return;

            // Player/camera presentation has finished at order 9000. The bar
            // follows the anchor's current render pose and faces this owner only.
            _bar.transform.SetPositionAndRotation(anchor.ReleaseProgressWorldPosition, view.rotation);
            _fill.anchorMax = new Vector2(_player.AnchorReleaseHoldProgress, 1f);
        }
    }
}
