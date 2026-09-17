using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace WaveByWave.Player
{
    [RequireComponent(typeof(NetworkObject))]
    public sealed class PlayerAnimationSync : NetworkBehaviour
    {
        private static readonly int SpeedHash = Animator.StringToHash("Speed");
        private static readonly int GroundedHash = Animator.StringToHash("Grounded");
        private static readonly int VerticalSpeedHash = Animator.StringToHash("VerticalSpeed");
        private static readonly int ActionHash = Animator.StringToHash("Action");

        [SerializeField] private Animator animator;

        private readonly NetworkVariable<float> _speed = new(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<float> _verticalSpeed = new(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<bool> _grounded = new(
            true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<int> _actionSequence = new(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<FixedString32Bytes> _action = new(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

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
            if (!IsOwner)
                return;

            _speed.Value = speed;
            _grounded.Value = grounded;
            _verticalSpeed.Value = verticalSpeed;
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
            animator.SetBool(GroundedHash, _grounded.Value);
            animator.SetFloat(VerticalSpeedHash, _verticalSpeed.Value);
        }

        private void OnActionSequenceChanged(int previous, int current)
        {
            if (!IsOwner && current != previous)
                animator?.SetTrigger(ActionHash);
        }
    }
}
