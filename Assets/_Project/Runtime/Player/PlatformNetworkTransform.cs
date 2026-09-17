using Unity.Netcode;
using UnityEngine;

namespace WaveByWave.Player
{
    /// <summary>
    /// Replicates completed KCC simulation poses, never the host's interpolated Transform.
    /// Physics and presentation sample the same timestamped stream at their own times.
    /// </summary>
    [DefaultExecutionOrder(1500)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MovingPlatform))]
    public sealed class PlatformNetworkTransform : NetworkBehaviour
    {
        public const float DefaultInterpolationDelay = 0.08f;

        [SerializeField, Range(0.04f, 0.3f)] private float interpolationDelay = DefaultInterpolationDelay;
        [SerializeField, Range(0f, 0.2f)] private float maximumExtrapolationTime = 0.1f;

        private const int SnapshotCapacity = 64;
        private const double MaximumClockCorrectionRate = 0.05;
        private readonly MotionSnapshot[] _snapshots = new MotionSnapshot[SnapshotCapacity];
        private MovingPlatform _platform;
        private int _snapshotStart;
        private int _snapshotCount;
        private double _serverClockOffset;
        private double _playbackServerTime;
        private double _playbackLocalTime;
        private bool _playbackClockInitialized;

        public double PresentationServerTime => _playbackClockInitialized
            ? _playbackServerTime + Time.unscaledTimeAsDouble - _playbackLocalTime
            : NetworkManager.ServerTime.Time - interpolationDelay;

        private struct MotionSnapshot
        {
            public double Time;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Velocity;
            public Vector3 AngularVelocity;
        }

        private void Awake() => _platform = GetComponent<MovingPlatform>();

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            _snapshotStart = 0;
            _snapshotCount = 0;
            _playbackClockInitialized = false;
            _serverClockOffset = NetworkManager.ServerTime.Time - Time.unscaledTimeAsDouble;
            if (!IsServer)
                AdvancePlaybackClock();
        }

        public override void OnNetworkDespawn()
        {
            _snapshotCount = 0;
            _playbackClockInitialized = false;
            base.OnNetworkDespawn();
        }

        private void Update()
        {
            if (!IsSpawned)
                return;

            if (IsServer)
            {
                // NGO advances its clock in PreUpdate. Map Unity's fixed clock to
                // that domain here, outside FixedUpdate's fixed-time properties.
                _serverClockOffset = NetworkManager.ServerTime.Time - Time.unscaledTimeAsDouble;
            }
            else
            {
                AdvancePlaybackClock();
            }
        }

        private void FixedUpdate()
        {
            if (!IsSpawned || !IsServer || !_platform.UsesKccMover)
                return;

            // Execution order 1500 follows KCC (1000). The completed transient pose
            // remains authoritative even when KCC has restored the visible root
            // to its interpolation start. Timestamp this physics step, not the
            // render frame in which NGO happens to send its messages.
            var mover = _platform.KccMover;
            ReceiveMotionSnapshotClientRpc(
                Time.fixedUnscaledTimeAsDouble + _serverClockOffset,
                mover.TransientPosition,
                mover.TransientRotation,
                mover.Velocity,
                mover.AngularVelocity);
        }

        [ClientRpc(Delivery = RpcDelivery.Unreliable)]
        private void ReceiveMotionSnapshotClientRpc(double serverTime, Vector3 position,
            Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
        {
            if (IsServer || !IsSpawned)
                return;

            if (_snapshotCount > 0 && serverTime <= GetSnapshot(_snapshotCount - 1).Time)
                return;

            if (_snapshotCount == SnapshotCapacity)
            {
                _snapshotStart = (_snapshotStart + 1) % SnapshotCapacity;
                _snapshotCount--;
            }

            _snapshots[(_snapshotStart + _snapshotCount) % SnapshotCapacity] = new MotionSnapshot
            {
                Time = serverTime,
                Position = position,
                Rotation = rotation,
                Velocity = velocity,
                AngularVelocity = angularVelocity
            };
            _snapshotCount++;
        }

        private void AdvancePlaybackClock()
        {
            var localTime = Time.unscaledTimeAsDouble;
            var targetTime = NetworkManager.ServerTime.Time - interpolationDelay;
            if (!_playbackClockInitialized)
            {
                _playbackServerTime = targetTime;
                _playbackClockInitialized = true;
            }
            else
            {
                var elapsed = System.Math.Max(0d, localTime - _playbackLocalTime);
                var advancedTime = _playbackServerTime + elapsed;
                var maximumCorrection = MaximumClockCorrectionRate * elapsed;
                // Packet arrival never resets presentation time. Network-clock
                // corrections are absorbed by a small change of playback speed.
                var correction = System.Math.Max(-maximumCorrection,
                    System.Math.Min(maximumCorrection, targetTime - advancedTime));
                _playbackServerTime = advancedTime + correction;
            }
            _playbackLocalTime = localTime;
        }

        public bool TryGetFixedPose(out Vector3 position, out Quaternion rotation)
        {
            var fixedSampleTime = _playbackServerTime +
                                  Time.fixedUnscaledTimeAsDouble - _playbackLocalTime;
            return TrySamplePose(fixedSampleTime, out position, out rotation);
        }

        private void LateUpdate()
        {
            if (!IsSpawned || IsServer || !_platform.UsesKccMover)
                return;

            // KCC has finished interpolation at order 1000. Render directly from
            // the snapshot timeline instead of resampling frames into physics
            // and back into frames. Passenger presentation follows this exact
            // pose at order 9000, including the ship's roll and pitch.
            if (TrySamplePose(PresentationServerTime, out var position, out var rotation))
                transform.SetPositionAndRotation(position, rotation);
        }

        private MotionSnapshot GetSnapshot(int index) =>
            _snapshots[(_snapshotStart + index) % SnapshotCapacity];

        private bool TrySamplePose(double sampleTime, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            if (_snapshotCount == 0 || !_playbackClockInitialized)
                return false;

            var previous = GetSnapshot(0);
            if (sampleTime <= previous.Time)
            {
                position = previous.Position;
                rotation = previous.Rotation;
                return true;
            }

            for (var i = 1; i < _snapshotCount; i++)
            {
                var next = GetSnapshot(i);
                if (sampleTime <= next.Time)
                {
                    var fraction = (float)((sampleTime - previous.Time) / (next.Time - previous.Time));
                    position = Vector3.Lerp(previous.Position, next.Position, fraction);
                    rotation = Quaternion.Slerp(previous.Rotation, next.Rotation, fraction);
                    return true;
                }
                previous = next;
            }

            // Bridge a short missing packet with accepted hull velocity, but
            // bound this lead when the server stops providing snapshots.
            var extrapolation = Mathf.Clamp((float)(sampleTime - previous.Time), 0f, maximumExtrapolationTime);
            position = previous.Position + previous.Velocity * extrapolation;
            var angularSpeed = previous.AngularVelocity.magnitude;
            rotation = angularSpeed > 0.0001f
                ? Quaternion.AngleAxis(angularSpeed * extrapolation * Mathf.Rad2Deg,
                    previous.AngularVelocity / angularSpeed) * previous.Rotation
                : previous.Rotation;
            return true;
        }
    }
}
