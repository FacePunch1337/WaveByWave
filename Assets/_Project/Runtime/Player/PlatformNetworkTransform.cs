using Unity.Netcode.Components;

namespace WaveByWave.Player
{
    /// <summary>
    /// Captures NGO's interpolated pose before KCC starts its next fixed step.
    /// MovingPlatform applies that same sample after KCC for remote rendering.
    /// </summary>
    public sealed class PlatformNetworkTransform : NetworkTransform
    {
        private MovingPlatform _platform;

        protected override void Awake()
        {
            base.Awake();
            _platform = GetComponent<MovingPlatform>();
        }

        protected override void OnTransformUpdated()
        {
            base.OnTransformUpdated();
            if (IsSpawned && !CanCommitToTransform && _platform != null)
                _platform.CaptureNetworkRenderPose(transform.position, transform.rotation);
        }
    }
}
