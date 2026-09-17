using KinematicCharacterController;
using UnityEngine;
using WaveByWave.Ships;

namespace WaveByWave.Player
{
    // Capture the pose applied by NGO's PreLateUpdate before KCC performs its own LateUpdate
    // interpolation. This makes the remote ship and the local owner motor use one clock.
    [DefaultExecutionOrder(500)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody), typeof(PhysicsMover))]
    public sealed class MovingPlatform : MonoBehaviour, IMoverController
    {
        private const float NetworkPositionSmoothTime = 0.035f;
        private const float NetworkRotationSharpness = 24f;
        private const float NetworkTeleportDistance = 3f;

        private Vector3 _previousPosition;
        private Quaternion _previousRotation;
        private Vector3 _linearVelocity;
        private Vector3 _angularVelocity;
        private bool _hasPreviousPose;
        private NetworkShipController _networkShip;
        private Rigidbody _body;
        private PhysicsMover _kccMover;
        private Vector3 _networkKccTargetPosition;
        private Quaternion _networkKccTargetRotation = Quaternion.identity;
        private bool _hasNetworkKccTarget;
        private Vector3 _networkPositionVelocity;
        private bool _moverFailureReported;

        /// <summary>
        /// True when this platform is being moved by NetworkTransform interpolation on a client.
        /// A dynamic player cannot use the replicated world velocity for this case because the
        /// platform pose advances in render time rather than in every physics step.
        /// </summary>
        public bool UsesInterpolatedNetworkMotion =>
            _networkShip != null && _networkShip.IsSpawned && !_networkShip.IsServer;

        public bool UsesKccMover => _kccMover != null && _kccMover.enabled;
        public Rigidbody Body => _body;

        public static bool TryResolve(Collider collider, out MovingPlatform platform)
        {
            platform = null;
            if (collider == null)
                return false;

            // Compound ship colliders report the root Rigidbody even when the actual collider
            // is several transforms deep. Prefer that root, then fall back to walking parents.
            var attachedBody = collider.attachedRigidbody;
            if (attachedBody != null)
                platform = attachedBody.GetComponentInParent<MovingPlatform>();
            platform ??= collider.GetComponentInParent<MovingPlatform>();
            return platform != null;
        }

        private void OnEnable()
        {
            _previousPosition = transform.position;
            _previousRotation = transform.rotation;
            _hasPreviousPose = true;
            _networkShip = GetComponentInParent<NetworkShipController>();
            _body = GetComponentInParent<Rigidbody>();
            _kccMover = GetComponent<PhysicsMover>();

            if (Application.isPlaying)
            {
                if (_networkShip != null && _networkShip.IsSpawned)
                    EnableKccMover();
                else if (_kccMover != null)
                    _kccMover.enabled = false;
            }
        }

        public void EnableKccMover()
        {
            if (_networkShip == null || !_networkShip.IsSpawned || _body == null)
                return;

            _networkKccTargetPosition = transform.position;
            _networkKccTargetRotation = transform.rotation;
            _hasNetworkKccTarget = UsesInterpolatedNetworkMotion;
            _networkPositionVelocity = Vector3.zero;

            _kccMover ??= GetComponent<PhysicsMover>();
            if (_kccMover == null)
            {
                Debug.LogError("MovingPlatform requires a prefab-authored PhysicsMover.", this);
                return;
            }

            _body.isKinematic = true;
            _body.interpolation = RigidbodyInterpolation.None;
            _kccMover.MoveWithPhysics = true;
            _kccMover.MoverController = this;
            _kccMover.SetPositionAndRotation(transform.position, transform.rotation);
            _kccMover.enabled = true;
        }

        public void DisableKccMover()
        {
            if (_kccMover == null)
                return;

            if (ReferenceEquals(_kccMover.MoverController, this))
                _kccMover.MoverController = null;

            _kccMover.enabled = false;
            _hasNetworkKccTarget = false;
            _networkPositionVelocity = Vector3.zero;
        }

        public void UpdateMovement(out Vector3 goalPosition, out Quaternion goalRotation, float deltaTime)
        {
            // KCC requests every mover's goal before updating any character.
            // A ship query error must not abort that shared character phase.
            try
            {
                if (_networkShip != null &&
                    _networkShip.TryGetKccMoverTarget(out goalPosition, out goalRotation))
                {
                    _moverFailureReported = false;
                    return;
                }
            }
            catch (System.Exception exception)
            {
                goalPosition = _kccMover != null ? _kccMover.TransientPosition : transform.position;
                goalRotation = _kccMover != null ? _kccMover.TransientRotation : transform.rotation;
                if (!_moverFailureReported)
                {
                    Debug.LogException(exception, this);
                    _moverFailureReported = true;
                }
                return;
            }

            if (_hasNetworkKccTarget)
            {
                PredictNetworkMovement(out goalPosition, out goalRotation, deltaTime);
                return;
            }

            goalPosition = transform.position;
            goalRotation = transform.rotation;
        }

        private void PredictNetworkMovement(
            out Vector3 goalPosition,
            out Quaternion goalRotation,
            float deltaTime)
        {
            // CharacterPlayground movers provide a continuously changing goal every fixed tick.
            // NGO updates its interpolated transform in render time, so filter that authoritative
            // pose in fixed time. SmoothDamp keeps advancing between render samples without
            // introducing a second, potentially conflicting velocity source.
            var currentPosition = _kccMover.TransientPosition;
            var currentRotation = _kccMover.TransientRotation;
            if ((_networkKccTargetPosition - currentPosition).sqrMagnitude >
                NetworkTeleportDistance * NetworkTeleportDistance)
            {
                _networkPositionVelocity = Vector3.zero;
                goalPosition = _networkKccTargetPosition;
                goalRotation = _networkKccTargetRotation;
                return;
            }

            goalPosition = Vector3.SmoothDamp(
                currentPosition,
                _networkKccTargetPosition,
                ref _networkPositionVelocity,
                NetworkPositionSmoothTime,
                Mathf.Infinity,
                deltaTime);
            var correctionBlend = 1f - Mathf.Exp(
                -NetworkRotationSharpness * deltaTime);
            goalRotation = Quaternion.Slerp(
                currentRotation,
                _networkKccTargetRotation,
                correctionBlend);
        }

        /// <summary>
        /// KCC temporarily renders an interpolated mover pose between fixed ticks. The ship's
        /// authoritative water query must start from the last completed simulation pose.
        /// </summary>
        public void RestoreKccSimulationPose()
        {
            if (!UsesKccMover)
                return;

            transform.SetPositionAndRotation(_kccMover.TransientPosition, _kccMover.TransientRotation);
            _body.position = _kccMover.TransientPosition;
            _body.rotation = _kccMover.TransientRotation;
        }

        public Vector3 GetFrameDisplacement(Vector3 worldPoint)
        {
            if (!_hasPreviousPose)
                return Vector3.zero;

            var localPoint = Quaternion.Inverse(_previousRotation) * (worldPoint - _previousPosition);
            var movedPoint = transform.position + transform.rotation * localPoint;
            return movedPoint - worldPoint;
        }

        public Vector3 GetPointVelocity(Vector3 worldPoint)
        {
            if (UsesKccMover)
            {
                // Use the exact fixed-step goal that carries the capsule, including
                // impact recovery. Mixing NGO velocity with render-time heave
                // produces a different take-off velocity at a collision.
                return _kccMover.Velocity + Vector3.Cross(_kccMover.AngularVelocity,
                    worldPoint - _kccMover.TransientPosition);
            }
            var radius = worldPoint - transform.position;
            var measuredVelocity = _linearVelocity + Vector3.Cross(_angularVelocity, radius);
            if (_networkShip == null || !_networkShip.IsSpawned)
            {
                if (_body != null)
                {
                    var physicsVelocity = _body.GetPointVelocity(worldPoint);
                    if (physicsVelocity.sqrMagnitude > 0.000001f)
                        return physicsVelocity;
                }

                return measuredVelocity;
            }

            // Network interpolation can make a client's measured planar velocity arrive a frame
            // late. Use replicated sailing motion for translation/yaw, then add locally measured
            // heave plus pitch/roll so take-off inherits the complete deck-point velocity.
            var replicatedPlanarVelocity = _networkShip.GetPlanarPointVelocity(worldPoint);
            var waveAngularVelocity = _angularVelocity -
                                      Vector3.up * Vector3.Dot(_angularVelocity, Vector3.up);
            var waveVelocity = Vector3.up * _linearVelocity.y +
                               Vector3.Cross(waveAngularVelocity, radius);
            return replicatedPlanarVelocity + waveVelocity;
        }

        private void LateUpdate()
        {
            if (UsesKccMover && UsesInterpolatedNetworkMotion)
            {
                // NetworkTransform has applied its interpolated pose by PreLateUpdate. Preserve
                // that pose as the next KCC mover target before KCC replaces the visible pose
                // with its matching character/platform interpolation later in LateUpdate.
                _networkKccTargetPosition = transform.position;
                _networkKccTargetRotation = transform.rotation;
                _hasNetworkKccTarget = true;
            }

            if (_hasPreviousPose)
            {
                var deltaTime = Mathf.Max(Time.deltaTime, 0.0001f);
                var rawLinearVelocity = (transform.position - _previousPosition) / deltaTime;
                var deltaRotation = transform.rotation * Quaternion.Inverse(_previousRotation);
                deltaRotation.ToAngleAxis(out var angleDegrees, out var axis);
                if (angleDegrees > 180f)
                    angleDegrees -= 360f;

                var rawAngularVelocity = axis.sqrMagnitude > 0.001f && !float.IsNaN(axis.x)
                    ? axis.normalized * (angleDegrees * Mathf.Deg2Rad / deltaTime)
                    : Vector3.zero;
                var blend = 1f - Mathf.Exp(-16f * deltaTime);
                _linearVelocity = Vector3.Lerp(_linearVelocity, rawLinearVelocity, blend);
                _angularVelocity = Vector3.Lerp(_angularVelocity, rawAngularVelocity, blend);
            }

            _previousPosition = transform.position;
            _previousRotation = transform.rotation;
            _hasPreviousPose = true;
        }

        private void OnDisable()
        {
            if (_kccMover != null && ReferenceEquals(_kccMover.MoverController, this))
                _kccMover.MoverController = null;
            if (_kccMover != null)
                _kccMover.enabled = false;
        }
    }
}
