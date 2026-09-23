using UnityEngine;

namespace WaveByWave.Player
{
    public struct CameraEffectContext
    {
        public float DeltaTime;
        public float Time;
        public float HorizontalSpeed;
        public float VerticalSpeed;
        public bool Grounded;
        public bool Sprinting;
    }

    public struct CameraEffectPose
    {
        public Vector3 Position;
        public Vector3 Rotation;
    }

    // Settings live in assets; mutable playback state belongs to the local camera instance.
    public abstract class PlayerCameraEffectProfile : ScriptableObject
    {
        public abstract void Evaluate(in CameraEffectContext context, ref CameraEffectPose pose,
            CameraEffectPlayback playback);
    }

    public sealed class CameraEffectPlayback
    {
        public float Phase;
        public float IdlePhase;
        public float SprintBlend;
        public bool WasGrounded;
        public float PreviousVerticalSpeed;
        public Vector3 Position;
        public Vector3 Rotation;
        public Vector3 ImpulsePosition;
        public Vector3 ImpulseRotation;
        public Vector3 ImpulsePositionVelocity;
        public Vector3 ImpulseRotationVelocity;
    }
}
