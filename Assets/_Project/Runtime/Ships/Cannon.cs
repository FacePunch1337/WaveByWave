using UnityEngine;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    [DefaultExecutionOrder(2600)]
    public sealed class Cannon : MonoBehaviour, IPlayerInteractable
    {
        [Header("Mount")]
        [SerializeField] private Transform yawPivot;
        [SerializeField] private Transform elevationPivot;
        [SerializeField] private Transform muzzle;
        [SerializeField] private Transform station;
        [SerializeField] private Vector2 yawLimits = new(-45f, 45f);
        [SerializeField] private Vector2 elevationLimits = new(-10f, 60f);
        [SerializeField, Min(1f)] private float aimSpeed = 50f;

        [Header("Shot")]
        [SerializeField, Min(0f)] private float baseDamage = 40f;
        [SerializeField, Min(0.1f)] private float reloadDuration = 3f;
        [SerializeField, Min(1f)] private float muzzleSpeed = 55f;
        [SerializeField, Min(0.02f)] private float ballRadius = 0.13f;
        [SerializeField, Min(1f)] private float projectileLifetime = 15f;
        [SerializeField, Min(0.01f)] private float gravity = 9.81f;
        [SerializeField] private GameObject projectilePrefab;
        [SerializeField] private GameObject muzzleEffectPrefab;
        [SerializeField] private GameObject impactEffectPrefab;
        [SerializeField] private GameObject waterSplashPrefab;

        [Header("Recoil")]
        [SerializeField, Min(0f)] private float recoilDistance = 0.3f;
        [SerializeField, Min(0.01f)] private float recoilKickTime = 0.06f;
        [SerializeField, Min(0.01f)] private float recoilReturnTime = 0.55f;

        private CannonNetworkController _controller;
        private ShipCannonBattery _battery;
        private bool _localAim;
        private Vector2 _aim;
        private Vector3 _restElevationPosition;
        private float _recoilStarted = -100f;

        public CannonNetworkController Controller => _controller != null ? _controller :
            (_controller = GetComponentInParent<CannonNetworkController>());
        public ShipCannonBattery Battery => _battery != null ? _battery :
            (_battery = GetComponentInParent<ShipCannonBattery>());
        public CannonState State => Controller != null ? Controller.GetState(Controller.GetCannonIndex(this)) :
            new CannonState { Operator = NetworkShipController.NoHelmsman };
        public Transform Station => station != null ? station : transform;
        public Transform Muzzle => muzzle;
        public Vector2 YawLimits => yawLimits;
        public Vector2 ElevationLimits => elevationLimits;
        public float AimSpeed => aimSpeed;
        public float BaseDamage => baseDamage;
        public float ReloadDuration => reloadDuration;
        public float MuzzleSpeed => muzzleSpeed;
        public float BallRadius => ballRadius;
        public float ProjectileLifetime => projectileLifetime;
        public Vector3 Gravity => Vector3.down * gravity;
        internal GameObject ProjectilePrefab => projectilePrefab;
        internal GameObject MuzzleEffectPrefab => muzzleEffectPrefab;
        internal GameObject ImpactEffectPrefab => impactEffectPrefab;
        internal GameObject WaterSplashPrefab => waterSplashPrefab;

        public float DamageWithModifiers(float shipModifier, float projectileModifier) =>
            Mathf.Max(0f, baseDamage * (1f + shipModifier) + projectileModifier);

        private void Awake()
        {
            if (elevationPivot != null) _restElevationPosition = elevationPivot.localPosition;
            _controller = GetComponentInParent<CannonNetworkController>();
            _battery = GetComponentInParent<ShipCannonBattery>();
        }

        public Vector2 ClampAim(Vector2 aim) => new(Mathf.Clamp(aim.x, yawLimits.x, yawLimits.y),
            Mathf.Clamp(aim.y, elevationLimits.x, elevationLimits.y));
        public void PlayRecoil() => _recoilStarted = Time.time;
        public void SetLocalAim(Vector2 aim) { _localAim = true; _aim = ClampAim(aim); }
        public void ReleaseLocalAim() => _localAim = false;

        private void LateUpdate()
        {
            if (yawPivot == null || elevationPivot == null) return;
            var controller = Controller;
            if (!_localAim && controller != null && controller.IsSpawned)
            {
                var state = controller.GetState(controller.GetCannonIndex(this));
                _aim = Vector2.Lerp(_aim, new Vector2(state.Yaw, state.Elevation),
                    1f - Mathf.Exp(-25f * Time.deltaTime));
            }
            yawPivot.localRotation = Quaternion.Euler(0f, _aim.x, 0f);
            elevationPivot.localRotation = Quaternion.Euler(-_aim.y, 0f, 0f);
            var elapsed = Time.time - _recoilStarted;
            var recoil = elapsed < recoilKickTime ? Mathf.SmoothStep(0f, 1f, elapsed / recoilKickTime) :
                1f - Mathf.SmoothStep(0f, 1f, (elapsed - recoilKickTime) / recoilReturnTime);
            elevationPivot.localPosition = _restElevationPosition -
                elevationPivot.localRotation * Vector3.forward * (recoilDistance * recoil);
        }

        public void GetMuzzlePose(Vector2 aim, out Vector3 position, out Vector3 direction)
        {
            aim = ClampAim(aim);
            var yaw = Quaternion.Euler(0f, aim.x, 0f);
            var pitch = Quaternion.Euler(-aim.y, 0f, 0f);
            position = transform.TransformPoint(yawPivot.localPosition + yaw *
                (_restElevationPosition + pitch * muzzle.localPosition));
            direction = transform.rotation * yaw * pitch * Vector3.forward;
            var controller = Controller;
            if (controller != null && controller.IsServer && controller.TryGetComponent<Rigidbody>(out var body))
            {
                var root = controller.transform;
                var local = root.InverseTransformPoint(position);
                position = body.position + body.rotation * Vector3.Scale(local, root.lossyScale);
                direction = body.rotation * Quaternion.Inverse(root.rotation) * direction;
            }
        }

        public string GetInteractionPrompt(NetworkPlayerController player) => "Встать за пушку [E]";
        public void Interact(NetworkPlayerController player)
        {
            if (player != null && Controller != null && Controller.IsSpawned) player.RequestCannon(this);
        }

        private void OnValidate()
        {
            yawLimits.x = Mathf.Clamp(yawLimits.x, -85f, 0f);
            yawLimits.y = Mathf.Clamp(yawLimits.y, 0f, 85f);
            elevationLimits.x = Mathf.Clamp(elevationLimits.x, -45f, 0f);
            elevationLimits.y = Mathf.Clamp(elevationLimits.y, 0f, 85f);
        }

        private void OnDrawGizmosSelected()
        {
            var origin = elevationPivot != null ? elevationPivot.position : transform.position;
            DrawArc(origin, yawLimits, true, new Color(0.2f, 1f, 0.4f));
            DrawArc(origin, elevationLimits, false, new Color(0.2f, 0.6f, 1f));
            if (station == null) return;
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(station.position, 0.15f);
            Gizmos.DrawLine(station.position, station.position + station.forward);
        }

        private void DrawArc(Vector3 origin, Vector2 limits, bool horizontal, Color color)
        {
            Gizmos.color = color;
            Vector3 Direction(float angle) => transform.rotation *
                (horizontal ? Quaternion.Euler(0f, angle, 0f) : Quaternion.Euler(-angle, 0f, 0f)) *
                Vector3.forward * 3f;
            var previous = origin + Direction(limits.x);
            Gizmos.DrawLine(origin, previous);
            for (var i = 1; i <= 32; i++)
            {
                var next = origin + Direction(Mathf.Lerp(limits.x, limits.y, i / 32f));
                Gizmos.DrawLine(previous, next);
                previous = next;
            }
            Gizmos.DrawLine(origin, previous);
        }
    }
}
