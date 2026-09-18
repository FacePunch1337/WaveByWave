using UnityEngine;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    [DefaultExecutionOrder(2500)]
    public sealed class ShipAnchor : MonoBehaviour, IPlayerInteractable
    {
        [SerializeField] private NetworkShipController ship;
        [SerializeField] private Transform rotor;
        [Tooltip("Feet positions and facing directions, parented to the rotating capstan.")]
        [SerializeField] private Transform[] handleStations;
        [SerializeField] private Renderer indicatorRenderer;
        [SerializeField, Min(0.1f)] private float soloRaiseDuration = 8f;
        [SerializeField, Min(0.1f)] private float lowerHoldDuration = 1f;
        [SerializeField, Min(0.1f)] private float dropDuration = 4f;
        [SerializeField] private Vector3 releaseProgressOffset = new(0f, 1.4f, 0f);
        [SerializeField, Min(0.1f)] private float raisingRevolutions = 1f;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private MaterialPropertyBlock _propertyBlock;
        private Quaternion _restRotation;
        private float _displayedCapstanAngle;
        private bool _visualInitialized;

        public NetworkShipController Ship => ship;
        public Transform Rotor => rotor != null ? rotor : transform;
        public int HandleCount => handleStations?.Length ?? 0;
        public float SoloRaiseDuration => Mathf.Max(0.1f, soloRaiseDuration);
        public float LowerHoldDuration => Mathf.Max(0.1f, lowerHoldDuration);
        public float DropDuration => Mathf.Max(0.1f, dropDuration);
        public Vector3 ReleaseProgressWorldPosition => transform.TransformPoint(releaseProgressOffset);
        public float RaisingRotationDegrees => raisingRevolutions * 360f;

        public Transform GetHandleStation(int index) =>
            index >= 0 && index < HandleCount ? handleStations[index] : null;

        private void Awake()
        {
            _propertyBlock = new MaterialPropertyBlock();
            ship ??= GetComponentInParent<NetworkShipController>();
            _restRotation = Rotor.localRotation;
        }

        private void LateUpdate()
        {
            if (ship == null || !ship.IsSpawned)
            {
                _visualInitialized = false;
                return;
            }

            // Handles and stations turn together; passenger presentation follows
            // at order 9000, after the ship has received its smooth render pose.
            var blend = 1f - Mathf.Exp(-20f * Time.deltaTime);
            if (!_visualInitialized)
            {
                _displayedCapstanAngle = ship.AnchorCapstanAngle;
                _visualInitialized = true;
            }
            _displayedCapstanAngle = Mathf.Lerp(_displayedCapstanAngle, ship.AnchorCapstanAngle, blend);
            Rotor.localRotation = _restRotation *
                                  Quaternion.AngleAxis(_displayedCapstanAngle, Vector3.up);

            var color = ship.AnchorLowered ? new Color(1f, 0.04f, 0.02f) : new Color(0.05f, 1f, 0.18f);
            if (indicatorRenderer == null)
                return;

            _propertyBlock ??= new MaterialPropertyBlock();
            indicatorRenderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetColor(BaseColorId, color);
            _propertyBlock.SetColor(EmissionColorId, color * 2.5f);
            indicatorRenderer.SetPropertyBlock(_propertyBlock);
        }

        public string GetInteractionPrompt(NetworkPlayerController player) =>
            ship != null && ship.AnchorDropping ? "Поймать якорь [E]" : "Взяться за ручку якоря [E]";

        public void Interact(NetworkPlayerController player)
        {
            if (player != null && ship != null && ship.IsSpawned)
                player.RequestAnchorHandle(this);
        }

        private void OnDrawGizmosSelected()
        {
            if (handleStations == null)
                return;
            Gizmos.color = Color.cyan;
            foreach (var station in handleStations)
            {
                if (station == null)
                    continue;
                Gizmos.DrawWireSphere(station.position, 0.2f);
                Gizmos.DrawRay(station.position, station.forward * 0.6f);
            }
        }
    }
}
