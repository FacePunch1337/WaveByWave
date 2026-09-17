using UnityEngine;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    public sealed class ShipHelm : MonoBehaviour, IPlayerInteractable
    {
        [SerializeField] private NetworkShipController ship;
        [SerializeField] private Transform station;
        [Header("Wheel visual")]
        [SerializeField] private Transform wheelVisual;
        [SerializeField] private Vector3 localRotationAxis = Vector3.forward;
        [SerializeField] private bool invertVisualRotation;
        [SerializeField, Min(1f)] private float visualRotationSpeed = 360f;

        private Quaternion _straightLocalRotation;
        private float _displayedAngle;

        public NetworkShipController Ship => ship;
        public Transform Station => station != null ? station : transform;

        private void Awake()
        {
            ship ??= GetComponentInParent<NetworkShipController>();
            if (wheelVisual != null)
                _straightLocalRotation = wheelVisual.localRotation;
            if (localRotationAxis.sqrMagnitude < 0.001f)
                localRotationAxis = Vector3.forward;
        }

        private void OnValidate()
        {
            if (localRotationAxis.sqrMagnitude < 0.001f)
                localRotationAxis = Vector3.forward;
        }

        private void Update()
        {
            if (ship == null || wheelVisual == null)
                return;

            var targetAngle = ship.HelmAngle * (invertVisualRotation ? -1f : 1f);
            // Do not use MoveTowardsAngle here: the wheel intentionally travels through
            // multiple complete revolutions and its unwrapped angle must be preserved.
            _displayedAngle = Mathf.MoveTowards(_displayedAngle, targetAngle,
                visualRotationSpeed * Time.deltaTime);
            wheelVisual.localRotation = _straightLocalRotation *
                                        Quaternion.AngleAxis(_displayedAngle, localRotationAxis.normalized);
        }

        public string GetInteractionPrompt(NetworkPlayerController player) =>
            player != null && player.IsAtHelm ? "Покинуть штурвал [E]" : "Встать за штурвал [E]";

        public void Interact(NetworkPlayerController player)
        {
            if (player != null)
                player.EnterHelm(this);
        }
    }
}
