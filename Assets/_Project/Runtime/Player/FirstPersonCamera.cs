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
            if (eyeTarget == null || SessionMenuPresenter.InputCaptured || Mouse.current == null)
                return;

            var delta = Mouse.current.delta.ReadValue() * sensitivity;
            _yaw += delta.x;
            _pitch = Mathf.Clamp(_pitch - delta.y, pitchLimits.x, pitchLimits.y);

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

        private void SnapToEyes()
        {
            if (eyeTarget == null)
                return;

            var frameRotation = referenceFrame != null ? referenceFrame.rotation : Quaternion.identity;
            var lookRotation = frameRotation * Quaternion.Euler(_pitch, _yaw, 0f);
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
