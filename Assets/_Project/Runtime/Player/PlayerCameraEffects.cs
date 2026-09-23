using System;
using UnityEngine;

namespace WaveByWave.Player
{
    [Serializable]
    public sealed class CameraEffectSlot
    {
        public bool Enabled = true;
        public PlayerCameraEffectProfile Profile;
        [NonSerialized] public CameraEffectPlayback Playback;
    }

    [DefaultExecutionOrder(10000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(FirstPersonCamera))]
    public sealed class PlayerCameraEffects : MonoBehaviour
    {
        [SerializeField] private CameraEffectSlot[] effects = Array.Empty<CameraEffectSlot>();
        private NetworkPlayerController _player;

        private void Awake() => _player = GetComponentInParent<NetworkPlayerController>();

        private void LateUpdate()
        {
            if (_player == null || !_player.IsOwner || effects == null) return;
            var context = new CameraEffectContext
            {
                DeltaTime = Time.deltaTime,
                Time = Time.time,
                HorizontalSpeed = _player.CameraHorizontalSpeed,
                VerticalSpeed = _player.CameraVerticalSpeed,
                Grounded = _player.CameraGrounded,
                Sprinting = _player.CameraSprinting
            };
            var pose = new CameraEffectPose();
            foreach (var slot in effects)
            {
                if (slot == null || !slot.Enabled || slot.Profile == null) continue;
                slot.Playback ??= new CameraEffectPlayback { WasGrounded = context.Grounded };
                slot.Profile.Evaluate(in context, ref pose, slot.Playback);
            }
            transform.position += transform.rotation * pose.Position;
            transform.rotation *= Quaternion.Euler(pose.Rotation);
        }
    }
}
