using UnityEngine;
using UnityEngine.InputSystem;
using WaveByWave.UI;

namespace WaveByWave.Player
{
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(Camera))]
    public sealed class FirstPersonCamera : MonoBehaviour
    {
        [SerializeField] private Transform eyeTarget;
        [SerializeField] private Transform characterBody;
        [SerializeField] private Transform referenceFrame;
        [Tooltip("Additional camera offset in the local space of the Camera Holder / eye target.")]
        [SerializeField] private Vector3 positionOffset;
        [SerializeField, Min(0.01f)] private float sensitivity = 0.08f;
        [SerializeField] private Vector2 pitchLimits = new(-82f, 82f);

        private NetworkPlayerController _player;
        private Vector3 _authoredLocalPosition;
        private float _yaw;
        private float _pitch;
        private float _lookTransitionYawOffset;
        private float _lookTransitionPitchOffset;
        private float _lookTransitionStarted;
        private float _lookTransitionDuration;
        private bool _aimLimited;
        private Vector2 _aimYawLimits, _aimPitchLimits;
        private float _aimSpeed;
        public Vector2 AimAngles => new(_yaw, -_pitch);

        private void Awake()
        {
            _authoredLocalPosition = transform.localPosition;

            if (referenceFrame == transform || referenceFrame == eyeTarget)
                referenceFrame = null;

            var view = GetComponent<Camera>();
            view.fieldOfView = 75f;
            view.nearClipPlane = 0.03f;
        }

        public void SetTarget(Transform target, Transform body)
        {
            eyeTarget = target;
            characterBody = body;
            _player = characterBody != null ? characterBody.GetComponent<NetworkPlayerController>() : null;
            _yaw = characterBody != null ? characterBody.eulerAngles.y : 0f;
            _pitch = 0f;
            SnapToEyes();
        }

        public void SetReferenceFrame(Transform frame)
        {
            if (referenceFrame == frame)
                return;

            var worldForward = transform.forward;
            _lookTransitionDuration = 0f;
            referenceFrame = frame;
            var frameRotation = referenceFrame != null ? referenceFrame.rotation : Quaternion.identity;
            var localForward = Quaternion.Inverse(frameRotation) * worldForward;
            if (localForward.sqrMagnitude < 0.0001f)
                return;

            localForward.Normalize();
            _yaw = Mathf.Atan2(localForward.x, localForward.z) * Mathf.Rad2Deg;
            _pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(localForward.y, -1f, 1f)) * Mathf.Rad2Deg,
                pitchLimits.x, pitchLimits.y);
            SnapToEyes();
        }

        private void OnEnable()
        {
            if (!Application.isPlaying)
                return;

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void Update()
        {
            if (eyeTarget == null || SessionMenuPresenter.InputCaptured)
                return;

            var delta = Mouse.current != null ? Mouse.current.delta.ReadValue() * sensitivity : Vector2.zero;
            if (_aimLimited)
            {
                var maxDelta = _aimSpeed * Time.unscaledDeltaTime;
                if (Keyboard.current != null)
                {
                    delta.x += ((Keyboard.current.dKey.isPressed ? 1f : 0f) -
                        (Keyboard.current.aKey.isPressed ? 1f : 0f)) * maxDelta;
                    delta.y += ((Keyboard.current.wKey.isPressed ? 1f : 0f) -
                        (Keyboard.current.sKey.isPressed ? 1f : 0f)) * maxDelta;
                }
                delta.x = Mathf.Clamp(delta.x, -maxDelta, maxDelta);
                delta.y = Mathf.Clamp(delta.y, -maxDelta, maxDelta);
            }
            _yaw += delta.x;
            _pitch = Mathf.Clamp(_pitch - delta.y, pitchLimits.x, pitchLimits.y);
            ClampAim();

            // At the helm the station owns the body's rotation, while the player may still look around freely.
            if (characterBody != null && (_player == null || !_player.IsAtControlStation))
            {
                var frameRotation = referenceFrame != null ? referenceFrame.rotation : Quaternion.identity;
                var forward = frameRotation * Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward;
                forward = Vector3.ProjectOnPlane(forward, Vector3.up);
                if (forward.sqrMagnitude > 0.0001f)
                {
                    var bodyRotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
                    if (_player != null)
                        _player.SetDesiredBodyRotation(bodyRotation);
                    else
                        characterBody.rotation = bodyRotation;
                }
            }
        }

        private void LateUpdate() => SnapToEyes();

        public void RefreshPose() => SnapToEyes();

        public void SetAimLimits(Vector2 yaw, Vector2 elevation, float speed)
        {
            _aimLimited = true;
            _aimYawLimits = yaw;
            _aimPitchLimits = new Vector2(-elevation.y, -elevation.x);
            _aimSpeed = speed;
        }
        public void ClearAimLimits() => _aimLimited = false;
        private void ClampAim()
        {
            if (!_aimLimited) return;
            _yaw = Mathf.Clamp(_yaw, _aimYawLimits.x, _aimYawLimits.y);
            _pitch = Mathf.Clamp(_pitch, _aimPitchLimits.x, _aimPitchLimits.y);
        }

        public void SetLookRotation(Quaternion worldRotation)
        {
            _lookTransitionDuration = 0f;
            var frameRotation = referenceFrame != null ? referenceFrame.rotation : Quaternion.identity;
            var localForward = Quaternion.Inverse(frameRotation) * (worldRotation * Vector3.forward);
            _yaw = Mathf.Atan2(localForward.x, localForward.z) * Mathf.Rad2Deg;
            _pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(localForward.y, -1f, 1f)) * Mathf.Rad2Deg,
                pitchLimits.x, pitchLimits.y);
            SnapToEyes();
        }

        public void BlendLookRotation(Quaternion worldRotation, float duration)
        {
            var frameRotation = referenceFrame != null ? referenceFrame.rotation : Quaternion.identity;
            var localForward = Quaternion.Inverse(frameRotation) * transform.forward;
            var currentYaw = Mathf.Atan2(localForward.x, localForward.z) * Mathf.Rad2Deg;
            var currentPitch = -Mathf.Asin(Mathf.Clamp(localForward.y, -1f, 1f)) * Mathf.Rad2Deg;
            SetLookRotation(worldRotation);
            _lookTransitionYawOffset = Mathf.DeltaAngle(_yaw, currentYaw);
            _lookTransitionPitchOffset = currentPitch - _pitch;
            _lookTransitionStarted = Time.unscaledTime;
            _lookTransitionDuration = Mathf.Max(0f, duration);
            SnapToEyes();
        }

        private void SnapToEyes()
        {
            if (eyeTarget == null)
                return;

            var frameRotation = referenceFrame != null ? referenceFrame.rotation : Quaternion.identity;
            var transitionWeight = _lookTransitionDuration > 0f
                ? 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.Clamp01((Time.unscaledTime - _lookTransitionStarted) / _lookTransitionDuration))
                : 0f;
            // Fade the grab offset rather than overwriting mouse input each frame.
            var lookRotation = frameRotation * Quaternion.Euler(
                Mathf.Clamp(_pitch + _lookTransitionPitchOffset * transitionWeight, pitchLimits.x, pitchLimits.y),
                _yaw + _lookTransitionYawOffset * transitionWeight, 0f);
            if (transform == eyeTarget || transform.parent == eyeTarget)
            {
                // The owner camera lives in the player prefab. Keeping its position local avoids
                // a second world-space follow pass and removes speed-dependent render jitter.
                transform.localPosition = transform == eyeTarget
                    ? _authoredLocalPosition + positionOffset
                    : positionOffset;
                transform.rotation = lookRotation;
            }
            else
            {
                transform.SetPositionAndRotation(eyeTarget.TransformPoint(positionOffset), lookRotation);
            }
        }
    }
}
