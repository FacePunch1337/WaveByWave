using UnityEngine;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    [DefaultExecutionOrder(3000)]
    public sealed class CannonBallVisual : MonoBehaviour
    {
        private ShipCannonBattery _battery;
        private ShipCannon _cannon;
        private PlatformNetworkTransform _motion;
        private int _id;
        private Vector3 _origin, _velocity, _gravity, _hitPoint, _normal;
        private double _started, _impactAt;
        private float _lifetime;
        private bool _flashed, _impact, _water, _show;
        private GameObject _splash;
        private Material _effects, _debris;
        private TrailRenderer _trail;
        private Renderer _renderer;

        public void Initialize(ShipCannonBattery battery, ShipCannon cannon, int id, Vector3 origin, Vector3 velocity, Vector3 gravity,
            double started, float lifetime, Material effects)
        {
            _battery = battery; _cannon = cannon; _id = id; _origin = origin; _velocity = velocity; _gravity = gravity;
            _started = started; _lifetime = lifetime; _effects = effects;
            _motion = battery.GetComponent<PlatformNetworkTransform>();
            _trail = GetComponent<TrailRenderer>();
            _renderer = GetComponent<Renderer>();
            _renderer.enabled = false;
        }
        public void SetImpact(Vector3 point, Vector3 normal, bool water, bool show, double at,
            GameObject splash, Material effects, Material debris)
        {
            _impact = true; _hitPoint = point; _normal = normal; _water = water; _show = show;
            _impactAt = at; _splash = splash; _effects = effects; _debris = debris;
        }
        private void LateUpdate()
        {
            if (_battery == null || !_battery.IsSpawned) { Destroy(gameObject); return; }
            var now = !_battery.IsServer && _motion != null ? _motion.PresentationServerTime : _battery.NetworkManager.ServerTime.Time;
            if (now < _started) return;
            var age = (float)(System.Math.Min(now, _impact ? _impactAt : now) - _started);
            transform.position = _origin + _velocity * age + _gravity * (0.5f * age * age);
            if (!_flashed)
            {
                _flashed = true;
                var muzzle = _cannon != null ? _cannon.Muzzle : null;
                CannonEffects.Muzzle(muzzle != null ? muzzle.position : _origin,
                    muzzle != null ? muzzle.forward : _velocity.normalized, _effects);
                _renderer.enabled = true;
                _trail.Clear();
                _trail.emitting = true;
            }
            if (_impact && now >= _impactAt)
            {
                if (_show) CannonEffects.Hit(_hitPoint, _normal, _water, _splash, _effects, _debris);
                Destroy(gameObject);
            }
            else if (age > _lifetime + 2f) Destroy(gameObject);
        }
        private void OnDestroy() => CannonEffects.Forget(_battery, _id);
    }

}
