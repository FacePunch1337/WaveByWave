using Unity.Netcode;
using UnityEngine;

namespace WaveByWave.Ships
{
    /// <summary>Server-authored global wind direction, smoothly presented on every peer.</summary>
    public sealed class NetworkWindController : NetworkBehaviour
    {
        [SerializeField] private Vector2 initialDirection = new(0.7f, 0.7f);
        [SerializeField] private Vector2 directionChangeInterval = new(35f, 70f);
        [SerializeField, Range(0f, 180f)] private float maximumDirectionChange = 100f;
        [SerializeField, Min(1f)] private float directionTurnSpeed = 10f;

        private readonly NetworkVariable<Vector2> _targetDirection = new(Vector2.up);
        private Vector3 _direction = Vector3.forward;
        private float _nextDirectionChange;

        public Vector3 Direction => _direction.sqrMagnitude > 0.001f ? _direction.normalized : Vector3.forward;

        private void Awake()
        {
            _direction = ToWorldDirection(initialDirection);
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                var initial = initialDirection.sqrMagnitude > 0.001f ? initialDirection.normalized : Vector2.up;
                _targetDirection.Value = initial;
                ScheduleDirectionChange();
            }

            _direction = ToWorldDirection(_targetDirection.Value);
        }

        private void Update()
        {
            if (!IsSpawned)
                return;

            if (IsServer && Time.time >= _nextDirectionChange)
            {
                var angle = Random.Range(-maximumDirectionChange, maximumDirectionChange);
                var currentTarget = ToWorldDirection(_targetDirection.Value);
                var nextTarget = Quaternion.Euler(0f, angle, 0f) * currentTarget;
                _targetDirection.Value = new Vector2(nextTarget.x, nextTarget.z).normalized;
                ScheduleDirectionChange();
            }

            var target = ToWorldDirection(_targetDirection.Value);
            _direction = Vector3.RotateTowards(Direction, target,
                directionTurnSpeed * Mathf.Deg2Rad * Time.deltaTime, 0f).normalized;
        }

        private void ScheduleDirectionChange()
        {
            var minimum = Mathf.Max(1f, Mathf.Min(directionChangeInterval.x, directionChangeInterval.y));
            var maximum = Mathf.Max(minimum, Mathf.Max(directionChangeInterval.x, directionChangeInterval.y));
            _nextDirectionChange = Time.time + Random.Range(minimum, maximum);
        }

        private static Vector3 ToWorldDirection(Vector2 direction)
        {
            var world = new Vector3(direction.x, 0f, direction.y);
            return world.sqrMagnitude > 0.001f ? world.normalized : Vector3.forward;
        }
    }
}
