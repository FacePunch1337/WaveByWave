using UnityEngine;

namespace WaveByWave.Player
{
    [CreateAssetMenu(menuName = "Wave By Wave/Camera/Locomotion effect", fileName = "LocomotionCameraEffect")]
    public sealed class LocomotionCameraEffect : PlayerCameraEffectProfile
    {
        [Header("Idle")]
        [Min(0f)] public float IdleFrequency = 0.25f;
        [Min(0f)] public float IdleHorizontalAmplitude = 0.002f;
        [Min(0f)] public float IdleVerticalAmplitude = 0.004f;
        [Min(0f)] public float IdlePitchAmplitude = 0.12f;
        [Header("Walking")]
        [Min(0f)] public float WalkFrequency = 1.8f;
        [Min(0f)] public float WalkHorizontalAmplitude = 0.01f;
        [Min(0f)] public float WalkVerticalAmplitude = 0.018f;
        [Min(0f)] public float WalkRollAmplitude = 0.35f;
        [Header("Sprinting")]
        [Min(0f)] public float SprintFrequency = 2.6f;
        [Min(0f)] public float SprintHorizontalAmplitude = 0.016f;
        [Min(0f)] public float SprintVerticalAmplitude = 0.03f;
        [Min(0f)] public float SprintRollAmplitude = 0.7f;
        [Header("Jump and landing")]
        [Min(0f)] public float JumpDip = 0.02f;
        [Min(0f)] public float JumpPitch = 0.45f;
        [Min(0f)] public float LandingDip = 0.05f;
        [Min(0f)] public float LandingPitch = 1.2f;
        [Min(0.01f)] public float LandingReferenceSpeed = 8f;
        [Header("Smoothing")]
        [Min(0.01f)] public float BlendSpeed = 12f;
        [Min(0.01f)] public float ImpulseReturnTime = 0.18f;
        [Min(0.1f)] public float WalkReferenceSpeed = 5f;

        public override void Evaluate(in CameraEffectContext context, ref CameraEffectPose pose,
            CameraEffectPlayback playback)
        {
            var dt = context.DeltaTime;
            if (dt <= 0f) return;
            if (playback.WasGrounded && !context.Grounded && context.VerticalSpeed > 0.2f)
            {
                playback.ImpulsePosition += Vector3.down * JumpDip;
                playback.ImpulseRotation += Vector3.left * JumpPitch;
            }
            if (!playback.WasGrounded && context.Grounded)
            {
                var strength = Mathf.Clamp01(-playback.PreviousVerticalSpeed / LandingReferenceSpeed);
                playback.ImpulsePosition += Vector3.down * LandingDip * strength;
                playback.ImpulseRotation += Vector3.right * LandingPitch * strength;
            }
            playback.WasGrounded = context.Grounded;
            playback.PreviousVerticalSpeed = context.VerticalSpeed;

            var movement = context.Grounded ? Mathf.Clamp01(context.HorizontalSpeed / WalkReferenceSpeed) : 0f;
            var blend = 1f - Mathf.Exp(-BlendSpeed * dt);
            playback.SprintBlend = Mathf.Lerp(playback.SprintBlend, context.Sprinting ? 1f : 0f, blend);
            var targetPosition = Vector3.zero;
            var targetRotation = Vector3.zero;
            if (movement > 0.01f)
            {
                var sprint = playback.SprintBlend;
                var frequency = Mathf.Lerp(WalkFrequency, SprintFrequency, sprint);
                playback.Phase = Mathf.Repeat(playback.Phase + frequency * Mathf.PI * 2f * movement * dt,
                    Mathf.PI * 4f);
                var sway = Mathf.Sin(playback.Phase * 0.5f);
                targetPosition.x = sway * Mathf.Lerp(WalkHorizontalAmplitude, SprintHorizontalAmplitude, sprint) * movement;
                targetPosition.y = Mathf.Sin(playback.Phase) *
                    Mathf.Lerp(WalkVerticalAmplitude, SprintVerticalAmplitude, sprint) * movement;
                targetRotation.z = -sway * Mathf.Lerp(WalkRollAmplitude, SprintRollAmplitude, sprint) * movement;
            }
            else
            {
                playback.Phase = 0f;
                if (context.Grounded)
                {
                    playback.IdlePhase = Mathf.Repeat(playback.IdlePhase + IdleFrequency * Mathf.PI * 2f * dt,
                        Mathf.PI * 2f);
                    targetPosition.x = Mathf.Cos(playback.IdlePhase) * IdleHorizontalAmplitude;
                    targetPosition.y = Mathf.Sin(playback.IdlePhase) * IdleVerticalAmplitude;
                    targetRotation.x = Mathf.Sin(playback.IdlePhase) * IdlePitchAmplitude;
                }
            }
            playback.ImpulsePosition = Vector3.SmoothDamp(playback.ImpulsePosition, Vector3.zero,
                ref playback.ImpulsePositionVelocity, ImpulseReturnTime, Mathf.Infinity, dt);
            playback.ImpulseRotation = Vector3.SmoothDamp(playback.ImpulseRotation, Vector3.zero,
                ref playback.ImpulseRotationVelocity, ImpulseReturnTime, Mathf.Infinity, dt);
            playback.Position = Vector3.Lerp(playback.Position, targetPosition + playback.ImpulsePosition, blend);
            playback.Rotation = Vector3.Lerp(playback.Rotation, targetRotation + playback.ImpulseRotation, blend);
            pose.Position += playback.Position;
            pose.Rotation += playback.Rotation;
        }
    }
}
