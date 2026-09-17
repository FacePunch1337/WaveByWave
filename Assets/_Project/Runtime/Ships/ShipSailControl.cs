using System;
using UnityEngine;
using UnityEngine.Serialization;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    public sealed class ShipSailControl : MonoBehaviour, IPlayerInteractable
    {
        [SerializeField] private NetworkShipController ship;
        [SerializeField] private Transform station;
        [SerializeField] private Renderer indicatorRenderer;
        [SerializeField] private Transform[] sailVisuals;

        [Header("Wind interaction")]
        [Tooltip("Transform whose orientation represents the actual sail face. Defaults to the first sail visual.")]
        [SerializeField] private Transform windReference;
        [Tooltip("Outward-facing normal of the sail in Wind Reference local space.")]
        [SerializeField] private Vector3 localWindNormal = Vector3.forward;

        [Header("Visual scale")]
        [FormerlySerializedAs("furledScale")]
        [SerializeField, Min(0.001f)] private float furledScaleY = 0.08f;
        [SerializeField, Min(0.001f)] private float deployedScaleY = 1f;
        [SerializeField, Min(0.001f)] private float calmScaleZMultiplier = 1f;
        [SerializeField, Min(0.001f)] private float fullWindScaleZMultiplier = 1.3f;
        [SerializeField] private SailDepthAnchor depthAnchor = SailDepthAnchor.NegativeEdge;
        [SerializeField, Min(0.1f)] private float visualAdjustmentSpeed = 1.5f;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private MaterialPropertyBlock _propertyBlock;
        private SailVisualState[] _visualStates = Array.Empty<SailVisualState>();
        private float _displayedDeployment;

        private enum SailDepthAnchor
        {
            NegativeEdge,
            Center,
            PositiveEdge
        }

        public NetworkShipController Ship => ship;
        public Transform Station => station != null ? station : transform;
        public Vector3 SailNormal
        {
            get
            {
                var reference = windReference;
                if (reference == null && sailVisuals != null)
                {
                    foreach (var visual in sailVisuals)
                    {
                        if (visual == null)
                            continue;
                        reference = visual;
                        break;
                    }
                }

                var normal = reference != null
                    ? reference.TransformDirection(localWindNormal)
                    : ship != null ? ship.transform.forward : Vector3.forward;
                normal = Vector3.ProjectOnPlane(normal, Vector3.up);
                return normal.sqrMagnitude > 0.001f ? normal.normalized : Vector3.forward;
            }
        }

        private struct SailVisualState
        {
            public Transform Transform;
            public Vector3 FullScale;
            public Vector3 FixedAnchorInParent;
            public Vector3 LocalAnchorPoint;
        }

        private void Awake()
        {
            _propertyBlock = new MaterialPropertyBlock();
            ship ??= GetComponentInParent<NetworkShipController>();
            indicatorRenderer ??= GetComponentInChildren<Renderer>();
            if (windReference == null && sailVisuals != null)
            {
                foreach (var visual in sailVisuals)
                {
                    if (visual == null)
                        continue;
                    windReference = visual;
                    break;
                }
            }
            if (localWindNormal.sqrMagnitude < 0.001f)
                localWindNormal = Vector3.forward;
            CacheSailVisuals();
            _displayedDeployment = ship != null ? ship.InitialSailDeployment : 0f;
            ApplySailVisuals(_displayedDeployment, 0f);
        }

        private void CacheSailVisuals()
        {
            if (sailVisuals == null)
            {
                _visualStates = Array.Empty<SailVisualState>();
                return;
            }

            _visualStates = new SailVisualState[sailVisuals.Length];
            for (var i = 0; i < sailVisuals.Length; i++)
            {
                var visual = sailVisuals[i];
                if (visual == null || visual.parent == null)
                    continue;

                var meshFilter = visual.GetComponent<MeshFilter>();
                var localBounds = meshFilter != null && meshFilter.sharedMesh != null
                    ? meshFilter.sharedMesh.bounds
                    : new Bounds(Vector3.zero, Vector3.one);
                var anchorZ = depthAnchor switch
                {
                    SailDepthAnchor.PositiveEdge => localBounds.max.z,
                    SailDepthAnchor.Center => localBounds.center.z,
                    _ => localBounds.min.z
                };
                var localAnchorPoint = new Vector3(localBounds.center.x, localBounds.max.y, anchorZ);
                _visualStates[i] = new SailVisualState
                {
                    Transform = visual,
                    FullScale = visual.localScale,
                    LocalAnchorPoint = localAnchorPoint,
                    FixedAnchorInParent = visual.parent.InverseTransformPoint(visual.TransformPoint(localAnchorPoint))
                };
            }
        }

        private void Update()
        {
            if (ship == null)
                return;

            _displayedDeployment = Mathf.MoveTowards(_displayedDeployment, ship.SailDeployment,
                visualAdjustmentSpeed * Time.deltaTime);
            ApplySailVisuals(_displayedDeployment, ship.SailWindFill);
            ApplyIndicator(_displayedDeployment);
        }

        public float GetWindCapture(Vector3 windDirection)
        {
            var planarWind = Vector3.ProjectOnPlane(windDirection, Vector3.up);
            if (planarWind.sqrMagnitude < 0.001f)
                return 0f;

            return Mathf.Abs(Vector3.Dot(planarWind.normalized, SailNormal));
        }

        public Vector3 GetWindPressure(Vector3 windDirection)
        {
            var planarWind = Vector3.ProjectOnPlane(windDirection, Vector3.up);
            if (planarWind.sqrMagnitude < 0.001f)
                return Vector3.zero;

            planarWind.Normalize();
            var normal = SailNormal;
            return normal * Vector3.Dot(planarWind, normal);
        }

        private void OnValidate()
        {
            if (localWindNormal.sqrMagnitude < 0.001f)
                localWindNormal = Vector3.forward;
        }

        private void ApplySailVisuals(float deployment, float windFill)
        {
            var scaleY = Mathf.Lerp(furledScaleY, deployedScaleY, deployment);
            var scaleZMultiplier = Mathf.Lerp(calmScaleZMultiplier, fullWindScaleZMultiplier,
                Mathf.Clamp01(windFill) * deployment);
            foreach (var state in _visualStates)
            {
                var visual = state.Transform;
                if (visual == null || visual.parent == null)
                    continue;

                var scale = state.FullScale;
                scale.y = scaleY;
                scale.z = state.FullScale.z * scaleZMultiplier;
                visual.localScale = scale;
                var currentAnchor = visual.parent.InverseTransformPoint(visual.TransformPoint(state.LocalAnchorPoint));
                visual.localPosition += state.FixedAnchorInParent - currentAnchor;
            }
        }

        private void ApplyIndicator(float deployment)
        {
            if (indicatorRenderer == null)
                return;

            _propertyBlock ??= new MaterialPropertyBlock();
            var color = Color.Lerp(new Color(0.35f, 0.45f, 0.55f), new Color(1f, 0.75f, 0.08f), deployment);
            indicatorRenderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetColor(BaseColorId, color);
            _propertyBlock.SetColor(EmissionColorId, color * 2f);
            indicatorRenderer.SetPropertyBlock(_propertyBlock);
        }

        public string GetInteractionPrompt(NetworkPlayerController player) =>
            player != null && player.IsAtSailControl
                ? "Отойти от управления парусом [E]"
                : "Управлять парусом [E]";

        public void Interact(NetworkPlayerController player)
        {
            if (player != null)
                player.EnterSailControl(this);
        }
    }
}
