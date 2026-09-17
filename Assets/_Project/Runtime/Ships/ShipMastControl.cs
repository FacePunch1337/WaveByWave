using UnityEngine;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    public sealed class ShipMastControl : MonoBehaviour, IPlayerInteractable
    {
        [Header("References")]
        [SerializeField] private NetworkShipController ship;
        [SerializeField] private Transform station;
        [SerializeField] private Renderer indicatorRenderer;
        [SerializeField] private Transform mastPivot;

        [Header("Rotation")]
        [SerializeField] private Vector2 rotationLimits = new(-65f, 65f);
        [SerializeField] private float initialAngle;
        [SerializeField, Min(1f)] private float rotationSpeed = 35f;
        [SerializeField, Min(1f)] private float visualRotationSpeed = 120f;

        [Header("Editor gizmo")]
        [SerializeField, Min(0.25f)] private float gizmoRadius = 4f;
        [SerializeField] private float gizmoHeight = 1f;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private MaterialPropertyBlock _propertyBlock;
        private Quaternion _baseLocalRotation;
        private float _displayedAngle;

        public NetworkShipController Ship => ship;
        public Transform Station => station != null ? station : transform;
        public float InitialAngle => ClampAngle(initialAngle);
        public float RotationSpeed => rotationSpeed;

        private void Awake()
        {
            ship ??= GetComponentInParent<NetworkShipController>();
            indicatorRenderer ??= GetComponentInChildren<Renderer>();
            mastPivot ??= transform;
            _propertyBlock = new MaterialPropertyBlock();
            _baseLocalRotation = mastPivot.localRotation;
            _displayedAngle = InitialAngle;
        }

        private void OnValidate()
        {
            if (rotationLimits.x > rotationLimits.y)
                rotationLimits = new Vector2(rotationLimits.y, rotationLimits.x);

            initialAngle = Mathf.Clamp(initialAngle, rotationLimits.x, rotationLimits.y);
        }

        private void Update()
        {
            if (ship == null || mastPivot == null)
                return;

            _displayedAngle = Mathf.MoveTowardsAngle(_displayedAngle, ship.MastAngle,
                visualRotationSpeed * Time.deltaTime);
            mastPivot.localRotation = _baseLocalRotation * Quaternion.AngleAxis(_displayedAngle, Vector3.up);
            ApplyIndicator();
        }

        public float ClampAngle(float angle) => Mathf.Clamp(angle, rotationLimits.x, rotationLimits.y);

        private void ApplyIndicator()
        {
            if (indicatorRenderer == null)
                return;

            _propertyBlock ??= new MaterialPropertyBlock();
            var normalized = Mathf.InverseLerp(rotationLimits.x, rotationLimits.y, _displayedAngle);
            var color = Color.Lerp(new Color(0.1f, 0.65f, 1f), new Color(1f, 0.55f, 0.08f), normalized);
            indicatorRenderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetColor(BaseColorId, color);
            _propertyBlock.SetColor(EmissionColorId, color * 2f);
            indicatorRenderer.SetPropertyBlock(_propertyBlock);
        }

        public string GetInteractionPrompt(NetworkPlayerController player) =>
            player != null && player.IsAtMastControl
                ? "Отойти от управления мачтой [E]"
                : "Повернуть мачту [E]";

        public void Interact(NetworkPlayerController player)
        {
            if (player != null)
                player.EnterMastControl(this);
        }

        private void OnDrawGizmosSelected()
        {
            if (mastPivot == null)
                return;

            var origin = mastPivot.position + mastPivot.up * gizmoHeight;
            var currentNetworkAngle = Application.isPlaying && ship != null ? ship.MastAngle : 0f;
            var currentSailNormal = ship != null ? ship.SailNormal : mastPivot.forward;
            var baseNormal = Quaternion.AngleAxis(-currentNetworkAngle, mastPivot.up) *
                             currentSailNormal;
            baseNormal = Vector3.ProjectOnPlane(baseNormal, mastPivot.up).normalized;
            if (baseNormal.sqrMagnitude < 0.001f)
                return;

            var minimumDirection = Quaternion.AngleAxis(rotationLimits.x, mastPivot.up) * baseNormal;
            var maximumDirection = Quaternion.AngleAxis(rotationLimits.y, mastPivot.up) * baseNormal;

            Gizmos.color = new Color(0.1f, 1f, 0.95f, 1f);
            Gizmos.DrawRay(origin, baseNormal * gizmoRadius);
            Gizmos.DrawSphere(origin, gizmoRadius * 0.035f);

            Gizmos.color = new Color(1f, 0.32f, 0.08f, 1f);
            Gizmos.DrawRay(origin, minimumDirection * gizmoRadius);
            Gizmos.DrawRay(origin, maximumDirection * gizmoRadius);

            const int segments = 32;
            var previous = origin + minimumDirection * gizmoRadius;
            Gizmos.color = new Color(1f, 0.75f, 0.1f, 1f);
            for (var i = 1; i <= segments; i++)
            {
                var angle = Mathf.Lerp(rotationLimits.x, rotationLimits.y, i / (float)segments);
                var direction = Quaternion.AngleAxis(angle, mastPivot.up) * baseNormal;
                var next = origin + direction * gizmoRadius;
                Gizmos.DrawLine(previous, next);
                previous = next;
            }

            var sailPlaneDirection = Vector3.Cross(mastPivot.up, baseNormal).normalized;
            Gizmos.color = new Color(0.15f, 0.65f, 1f, 1f);
            Gizmos.DrawLine(origin - sailPlaneDirection * gizmoRadius * 0.6f,
                origin + sailPlaneDirection * gizmoRadius * 0.6f);
        }
    }
}
