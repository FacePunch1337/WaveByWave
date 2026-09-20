using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace WaveByWave.Player
{
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

        public Animator CurrentAnimator => animator;

        public void SetAnimator(Animator value)
        {
            animator = value;
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
