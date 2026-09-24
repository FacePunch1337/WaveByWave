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

        [Header("Pitch position offset")]
        [SerializeField, Tooltip("Move the Camera Holder between the three offsets as the player looks up and down.")]
        private bool usePitchPositionOffset;
        [SerializeField, Tooltip("Additional local offset while looking straight ahead.")]
        private Vector3 centerPositionOffset;
        [SerializeField, Tooltip("Additional local offset at the upper pitch limit.")]
        private Vector3 lookUpPositionOffset;
        [SerializeField, Tooltip("Additional local offset at the lower pitch limit.")]
        private Vector3 lookDownPositionOffset;
        [SerializeField, Min(0.01f), Tooltip("How quickly the Camera Holder reaches the requested offset.")]
        private float pitchPositionSharpness = 12f;

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
        private Camera _view;
        private Camera _externalView;
        private bool _authoredPositionInitialized;
        private Vector3 _currentPitchPositionOffset;
        private bool _pitchPositionInitialized;
        private int _pitchPositionUpdatedFrame = -1;
        public Vector2 AimAngles => new(_yaw, -_pitch);
        public Vector3 NetworkPositionOffset => positionOffset + _currentPitchPositionOffset;

        private void Awake()
        {
            CacheAuthoredPosition();

            if (referenceFrame == transform || referenceFrame == eyeTarget)
                referenceFrame = null;

            _view = GetComponent<Camera>();
            _view.fieldOfView = 75f;
            _view.nearClipPlane = 0.03f;
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
            if (eyeTarget == null || PlayerEquipment.InputCaptured)
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
                var bodyRotation = frameRotation * Quaternion.Euler(0f, _yaw, 0f);
                if (_player != null)
                    _player.SetDesiredBodyRotation(bodyRotation);
                else
                    characterBody.rotation = bodyRotation;
            }
        }

        private void LateUpdate() => SnapToEyes();

        public void RefreshPose() => SnapToEyes();

        public void SetExternalView(Camera externalView)
        {
            if (externalView == null)
            {
                Debug.LogError("Customization station has no dedicated camera assigned.", this);
                return;
            }

            _externalView = externalView;
            _externalView.enabled = true;
            if (_view != null)
                _view.enabled = false;
        }

        public void ClearExternalPose()
        {
            if (_externalView != null)
                _externalView.enabled = false;
            _externalView = null;
            if (_view != null)
                _view.enabled = true;
            SnapToEyes();
        }

        public void SetAimLimits(Vector2 yaw, Vector2 elevation, float speed)
        {
            _aimLimited = true;
            _aimYawLimits = yaw;
            _aimPitchLimits = new Vector2(-elevation.y, -elevation.x);
            _aimSpeed = speed;
        }
        public void ClearAimLimits() => _aimLimited = false;

        public void SetPitchPositionOffsetEnabled(bool enabled) => usePitchPositionOffset = enabled;

        public void ApplyReplicatedPositionOffset(Vector3 localOffset)
        {
            // Remote Camera Holders stay inactive, so their Awake is not guaranteed to run.
            // Cache the prefab-authored position lazily before applying the replicated offset.
            CacheAuthoredPosition();
            transform.localPosition = transform == eyeTarget
                ? _authoredLocalPosition + localOffset
                : localOffset;
        }

        public void ApplyReplicatedPose(Vector3 localOffset, Quaternion worldRotation)
        {
            ApplyReplicatedPositionOffset(localOffset);
            transform.rotation = worldRotation;
        }

        private void CacheAuthoredPosition()
        {
            if (_authoredPositionInitialized)
                return;
            _authoredLocalPosition = transform.localPosition;
            _authoredPositionInitialized = true;
        }

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
            var effectivePitch = Mathf.Clamp(_pitch + _lookTransitionPitchOffset * transitionWeight,
                pitchLimits.x, pitchLimits.y);
            var lookRotation = frameRotation * Quaternion.Euler(
                effectivePitch,
                _yaw + _lookTransitionYawOffset * transitionWeight, 0f);
            UpdatePitchPositionOffset(effectivePitch);
            var localPositionOffset = positionOffset + _currentPitchPositionOffset;
            if (transform == eyeTarget || transform.parent == eyeTarget)
            {
                // The owner camera lives in the player prefab. Keeping its position local avoids
                // a second world-space follow pass and removes speed-dependent render jitter.
                transform.localPosition = transform == eyeTarget
                    ? _authoredLocalPosition + localPositionOffset
                    : localPositionOffset;
                transform.rotation = lookRotation;
            }
            else
            {
                transform.SetPositionAndRotation(eyeTarget.TransformPoint(localPositionOffset), lookRotation);
            }
        }

        private void UpdatePitchPositionOffset(float effectivePitch)
        {
            var target = ResolvePitchPositionOffset(effectivePitch);
            if (!_pitchPositionInitialized)
            {
                _currentPitchPositionOffset = target;
                _pitchPositionInitialized = true;
                _pitchPositionUpdatedFrame = Time.frameCount;
                return;
            }

            // SnapToEyes can also be requested by the character controller. Advance smoothing
            // once per rendered frame so those extra pose refreshes do not change its speed.
            if (_pitchPositionUpdatedFrame == Time.frameCount)
                return;

            _pitchPositionUpdatedFrame = Time.frameCount;
            var blend = 1f - Mathf.Exp(-pitchPositionSharpness * Time.unscaledDeltaTime);
            _currentPitchPositionOffset = Vector3.Lerp(_currentPitchPositionOffset, target, blend);
        }

        private Vector3 ResolvePitchPositionOffset(float effectivePitch)
        {
            if (!usePitchPositionOffset)
                return Vector3.zero;

            if (effectivePitch < 0f)
            {
                var upRange = Mathf.Max(0.01f, -pitchLimits.x);
                return Vector3.Lerp(centerPositionOffset, lookUpPositionOffset,
                    Mathf.Clamp01(-effectivePitch / upRange));
            }

            var downRange = Mathf.Max(0.01f, pitchLimits.y);
            return Vector3.Lerp(centerPositionOffset, lookDownPositionOffset,
                Mathf.Clamp01(effectivePitch / downRange));
        }
    }
}
