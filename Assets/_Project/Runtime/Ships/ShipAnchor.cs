using UnityEngine;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    public sealed class ShipAnchor : MonoBehaviour, IPlayerInteractable
    {
        [SerializeField] private NetworkShipController ship;
        [SerializeField] private Renderer indicatorRenderer;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private MaterialPropertyBlock _propertyBlock;
        private bool _lastLowered;
        private bool _visualInitialized;

        public NetworkShipController Ship => ship;

        private void Awake()
        {
            _propertyBlock = new MaterialPropertyBlock();
            ship ??= GetComponentInParent<NetworkShipController>();
            indicatorRenderer ??= GetComponentInChildren<Renderer>();
        }

        private void Update()
        {
            if (ship == null || (_visualInitialized && _lastLowered == ship.AnchorLowered))
                return;

            _lastLowered = ship.AnchorLowered;
            _visualInitialized = true;
            var color = _lastLowered ? new Color(1f, 0.04f, 0.02f) : new Color(0.05f, 1f, 0.18f);
            if (indicatorRenderer == null)
                return;

            _propertyBlock ??= new MaterialPropertyBlock();
            indicatorRenderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetColor(BaseColorId, color);
            _propertyBlock.SetColor(EmissionColorId, color * 2.5f);
            indicatorRenderer.SetPropertyBlock(_propertyBlock);
        }

        public string GetInteractionPrompt(NetworkPlayerController player) =>
            ship != null && ship.AnchorLowered ? "Поднять якорь [E]" : "Опустить якорь [E]";

        public void Interact(NetworkPlayerController player)
        {
            if (player != null && ship != null)
                ship.ToggleAnchorServerRpc();
        }
    }
}
