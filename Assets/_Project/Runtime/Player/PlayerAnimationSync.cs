using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace WaveByWave.Player
{
    // The procedural look pass runs after Mecanim but before HeldItemView (9700), so the
    // hand solver can compensate for the changed shoulders and keep both hands on the item.
    [DefaultExecutionOrder(9600)]
    [RequireComponent(typeof(NetworkObject))]
    public sealed class PlayerAnimationSync : NetworkBehaviour
    {
        private static readonly int SpeedHash = Animator.StringToHash("Speed");
        private static readonly int MoveXHash = Animator.StringToHash("MoveX");
        private static readonly int MoveYHash = Animator.StringToHash("MoveY");
        private static readonly int GroundedHash = Animator.StringToHash("Grounded");
        private static readonly int VerticalSpeedHash = Animator.StringToHash("VerticalSpeed");
        private static readonly int SprintingHash = Animator.StringToHash("Sprinting");
        private static readonly int SwimmingHash = Animator.StringToHash("Swimming");
        private static readonly int SwimForwardHash = Animator.StringToHash("SwimForward");
        private static readonly int LocomotionRateHash = Animator.StringToHash("LocomotionRate");
        private static readonly int ActionHash = Animator.StringToHash("Action");

        [SerializeField] private Animator animator;

        [Header("Spine and head IK")]
        [SerializeField] private bool enableLookIk = true;
        [SerializeField, Tooltip("Maximum downward/upward bend driven by the camera.")]
        private Vector2 lookPitchLimits = new(-42f, 58f);
        [SerializeField, Min(0f), Tooltip("Maximum horizontal look offset while the body is fixed, for example at a station.")]
        private float lookYawLimit = 65f;
        [SerializeField, Min(0f), Tooltip("How far the lower back trails a fast mouse turn. The head compensates to keep looking forward.")]
        private float turnLagSeconds = 0.065f;
        [SerializeField, Min(0f)] private float maximumTurnTwist = 18f;
        [SerializeField, Min(0.01f)] private float lookSharpness = 13f;
        [SerializeField, Range(0f, 1f)] private float spineLookShare = 0.24f;
        [SerializeField, Range(0f, 1f)] private float chestLookShare = 0.2f;
        [SerializeField, Range(0f, 1f)] private float upperChestLookShare = 0.14f;
        [SerializeField, Range(0f, 1f)] private float neckLookShare = 0.16f;
        [SerializeField, Range(0f, 1f)] private float headLookShare = 0.26f;

        private readonly NetworkVariable<float> _speed = new(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<float> _moveX = new(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<float> _moveY = new(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<float> _verticalSpeed = new(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<bool> _grounded = new(
            true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<bool> _sprinting = new(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<bool> _swimming = new(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<float> _swimForward = new(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<float> _locomotionRate = new(
            1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<int> _actionSequence = new(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<FixedString32Bytes> _action = new(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<Vector3> _lookPose = new(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<Vector3> _cameraPositionOffset = new(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private NetworkPlayerController _player;
        private FirstPersonCamera _ownerCamera;
        private Transform _poseReference;
        private Animator _boundAnimator;
        private Transform _spine;
        private Transform _chest;
        private Transform _upperChest;
        private Transform _neck;
        private Transform _head;
        private Vector3 _smoothedLookPose;
        private Vector3 _smoothedRemoteCameraOffset;
        private float _previousAimYaw;
        private bool _aimYawInitialized;
        private float _nextLookPublish;
        private float _nextCameraPositionHeartbeat;
        private Vector3 _lastSubmittedCameraOffset;
        private bool _cameraOffsetSubmitted;

        public Animator CurrentAnimator => animator;

        public bool TryGetPresentationLookRotation(out Quaternion rotation)
        {
            rotation = default;
            if (_poseReference == null)
                return false;

            // The carried chest does not use PlayerEquipment's held-input stream, so remote
            // copies must read the same independently replicated look pose that drives the body.
            var localForward = Quaternion.Euler(-_smoothedLookPose.x, _smoothedLookPose.y, 0f) *
                               Vector3.forward;
            var worldForward = _poseReference.TransformDirection(localForward);
            if (worldForward.sqrMagnitude < 0.0001f)
                return false;

            rotation = Quaternion.LookRotation(worldForward.normalized, _poseReference.up);
            return true;
        }

        private void Awake()
        {
            _player = GetComponent<NetworkPlayerController>();
            RefreshHumanoidBones();
        }

        public void SetAnimator(Animator value)
        {
            animator = value;
            _boundAnimator = null;
            RefreshHumanoidBones();
            ApplyLocomotion();
        }

        public override void OnNetworkSpawn()
        {
            _actionSequence.OnValueChanged += OnActionSequenceChanged;
            ApplyLocomotion();
        }

        public override void OnNetworkDespawn()
        {
            _actionSequence.OnValueChanged -= OnActionSequenceChanged;
            _aimYawInitialized = false;
        }

        public void SetLocomotion(float speed, bool grounded, float verticalSpeed)
        {
            SetLocomotion(new Vector2(0f, speed), grounded, verticalSpeed, false, false, 0f, 1f);
        }

        public void SetLocomotion(Vector2 move, bool grounded, float verticalSpeed, bool sprinting,
            bool swimming, float swimForward, float locomotionRate = 1f)
        {
            if (!IsOwner)
                return;

            move = Vector2.ClampMagnitude(move, 1f);
            _speed.Value = move.magnitude;
            _moveX.Value = move.x;
            _moveY.Value = move.y;
            _grounded.Value = grounded;
            _verticalSpeed.Value = verticalSpeed;
            _sprinting.Value = sprinting;
            _swimming.Value = swimming;
            _swimForward.Value = Mathf.Clamp01(swimForward);
            _locomotionRate.Value = Mathf.Clamp(locomotionRate, 0.1f, 3f);
            ApplyLocomotion();
        }

        public void BeginJump(float upwardSpeed)
        {
            if (!IsOwner)
                return;

            _grounded.Value = false;
            _verticalSpeed.Value = Mathf.Max(0.1f, upwardSpeed);
            _sprinting.Value = false;
            _swimming.Value = false;
            _locomotionRate.Value = 1f;
            ApplyLocomotion();
        }

        public void PlayAction(string action)
        {
            if (!IsOwner)
                return;

            _action.Value = action;
            _actionSequence.Value++;
            animator?.SetTrigger(ActionHash);
        }

        private void Update()
        {
            if (!IsOwner)
                ApplyLocomotion();
        }

        private void LateUpdate()
        {
            if (!IsSpawned)
                return;

            RefreshHumanoidBones();
            var targetPose = IsOwner ? BuildOwnerLookPose() : _lookPose.Value;
            if (IsOwner)
                PublishPresentationPose(targetPose);
            else
                ApplyRemoteCameraPosition();

            if (!enableLookIk || _boundAnimator == null || _head == null)
                return;

            var blend = 1f - Mathf.Exp(-lookSharpness * Time.unscaledDeltaTime);
            _smoothedLookPose = new Vector3(
                Mathf.LerpAngle(_smoothedLookPose.x, targetPose.x, blend),
                Mathf.LerpAngle(_smoothedLookPose.y, targetPose.y, blend),
                Mathf.LerpAngle(_smoothedLookPose.z, targetPose.z, blend));

            ApplyLookPose(_smoothedLookPose);
        }

        private Vector3 BuildOwnerLookPose()
        {
            var view = _player != null ? _player.OwnerView : null;
            if (view == null || _poseReference == null)
            {
                _aimYawInitialized = false;
                return Vector3.zero;
            }

            _ownerCamera ??= view.GetComponent<FirstPersonCamera>();
            var localLook = _poseReference.InverseTransformDirection(view.forward).normalized;
            var pitch = Mathf.Asin(Mathf.Clamp(localLook.y, -1f, 1f)) * Mathf.Rad2Deg;
            var yaw = Mathf.Atan2(localLook.x, localLook.z) * Mathf.Rad2Deg;
            pitch = Mathf.Clamp(pitch, lookPitchLimits.x, lookPitchLimits.y);
            yaw = Mathf.Clamp(yaw, -lookYawLimit, lookYawLimit);

            var aimYaw = _ownerCamera != null ? _ownerCamera.AimAngles.x : view.eulerAngles.y;
            var turnTwist = 0f;
            if (_aimYawInitialized && Time.unscaledDeltaTime > 0.0001f)
            {
                var angularSpeed = Mathf.DeltaAngle(_previousAimYaw, aimYaw) / Time.unscaledDeltaTime;
                turnTwist = Mathf.Clamp(angularSpeed * turnLagSeconds,
                    -maximumTurnTwist, maximumTurnTwist);
            }
            _previousAimYaw = aimYaw;
            _aimYawInitialized = true;
            return new Vector3(pitch, yaw, turnTwist);
        }

        private void PublishPresentationPose(Vector3 pose)
        {
            if (Time.unscaledTime < _nextLookPublish)
                return;

            // Fifteen samples per second are enough because every client smooths the pose.
            _nextLookPublish = Time.unscaledTime + 1f / 15f;
            if ((_lookPose.Value - pose).sqrMagnitude > 0.04f)
                _lookPose.Value = pose;
            if (_ownerCamera == null)
                return;

            var cameraOffset = _ownerCamera.NetworkPositionOffset;
            var changed = !_cameraOffsetSubmitted ||
                          (_lastSubmittedCameraOffset - cameraOffset).sqrMagnitude > 0.000001f;
            if (!changed && Time.unscaledTime < _nextCameraPositionHeartbeat)
                return;

            _lastSubmittedCameraOffset = cameraOffset;
            _cameraOffsetSubmitted = true;
            _nextCameraPositionHeartbeat = Time.unscaledTime + 0.5f;
            if (IsServer)
                _cameraPositionOffset.Value = cameraOffset;
            else
                SubmitCameraPositionOffsetServerRpc(cameraOffset);
        }

        [ServerRpc(Delivery = RpcDelivery.Unreliable)]
        private void SubmitCameraPositionOffsetServerRpc(Vector3 offset, ServerRpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId != OwnerClientId || !IsFinite(offset) || offset.sqrMagnitude > 25f)
                return;
            _cameraPositionOffset.Value = offset;
        }

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        private void ApplyRemoteCameraPosition()
        {
            var view = _player != null ? _player.OwnerView : null;
            if (view == null)
                return;

            _ownerCamera ??= view.GetComponent<FirstPersonCamera>();
            if (_ownerCamera == null)
                return;

            var blend = 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime);
            _smoothedRemoteCameraOffset = Vector3.Lerp(_smoothedRemoteCameraOffset,
                _cameraPositionOffset.Value, blend);
            _ownerCamera.ApplyReplicatedPositionOffset(_smoothedRemoteCameraOffset);
        }

        private void RefreshHumanoidBones()
        {
            if (animator == _boundAnimator)
                return;

            _boundAnimator = animator;
            _spine = _chest = _upperChest = _neck = _head = null;
            _smoothedLookPose = Vector3.zero;
            _aimYawInitialized = false;
            _poseReference = transform.Find("Presentation Root/Visual") ?? transform.Find("Visual") ?? transform;
            if (_boundAnimator == null || !_boundAnimator.isHuman)
                return;

            _spine = _boundAnimator.GetBoneTransform(HumanBodyBones.Spine);
            _chest = _boundAnimator.GetBoneTransform(HumanBodyBones.Chest);
            _upperChest = _boundAnimator.GetBoneTransform(HumanBodyBones.UpperChest);
            _neck = _boundAnimator.GetBoneTransform(HumanBodyBones.Neck);
            _head = _boundAnimator.GetBoneTransform(HumanBodyBones.Head);
        }

        private void ApplyLookPose(Vector3 pose)
        {
            var totalLookWeight = AvailableWeight(_spine, spineLookShare) +
                                  AvailableWeight(_chest, chestLookShare) +
                                  AvailableWeight(_upperChest, upperChestLookShare) +
                                  AvailableWeight(_neck, neckLookShare) +
                                  AvailableWeight(_head, headLookShare);
            if (totalLookWeight <= 0.0001f)
                return;

            var lowerTurnWeight = AvailableWeight(_spine, 0.45f) +
                                  AvailableWeight(_chest, 0.35f) +
                                  AvailableWeight(_upperChest, 0.2f);
            var upperTurnWeight = AvailableWeight(_neck, 0.35f) + AvailableWeight(_head, 0.65f);

            ApplyBone(_spine, spineLookShare / totalLookWeight,
                lowerTurnWeight > 0f ? -0.45f / lowerTurnWeight : 0f, pose);
            ApplyBone(_chest, chestLookShare / totalLookWeight,
                lowerTurnWeight > 0f ? -0.35f / lowerTurnWeight : 0f, pose);
            ApplyBone(_upperChest, upperChestLookShare / totalLookWeight,
                lowerTurnWeight > 0f ? -0.2f / lowerTurnWeight : 0f, pose);
            ApplyBone(_neck, neckLookShare / totalLookWeight,
                upperTurnWeight > 0f ? 0.35f / upperTurnWeight : 0f, pose);
            ApplyBone(_head, headLookShare / totalLookWeight,
                upperTurnWeight > 0f ? 0.65f / upperTurnWeight : 0f, pose);
        }

        private static float AvailableWeight(Transform bone, float weight) => bone != null ? weight : 0f;

        private void ApplyBone(Transform bone, float lookWeight, float turnWeight, Vector3 pose)
        {
            if (bone == null || _poseReference == null)
                return;

            var pitch = pose.x * lookWeight;
            var yaw = pose.y * lookWeight + pose.z * turnWeight;
            var delta = Quaternion.AngleAxis(yaw, _poseReference.up) *
                        Quaternion.AngleAxis(-pitch, _poseReference.right);
            bone.rotation = delta * bone.rotation;
        }

        private void ApplyLocomotion()
        {
            if (animator == null)
                return;

            animator.SetFloat(SpeedHash, _speed.Value);
            animator.SetFloat(MoveXHash, _moveX.Value);
            animator.SetFloat(MoveYHash, _moveY.Value);
            animator.SetBool(GroundedHash, _grounded.Value);
            animator.SetFloat(VerticalSpeedHash, _verticalSpeed.Value);
            animator.SetBool(SprintingHash, _sprinting.Value);
            animator.SetBool(SwimmingHash, _swimming.Value);
            animator.SetFloat(SwimForwardHash, _swimForward.Value);
            animator.SetFloat(LocomotionRateHash, _locomotionRate.Value);
        }

        private void OnActionSequenceChanged(int previous, int current)
        {
            if (!IsOwner && current != previous)
                animator?.SetTrigger(ActionHash);
        }
    }
}
