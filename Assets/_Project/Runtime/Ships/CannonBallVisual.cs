using UnityEngine;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    [DefaultExecutionOrder(3000)]
    public sealed class CannonBallVisual : MonoBehaviour
    {
        private CannonNetworkController _controller;
        private Cannon _cannon;
        private PlatformNetworkTransform _motion;
        private int _id;
        private Vector3 _origin, _velocity, _gravity, _hitPoint, _normal;
        private double _started, _impactAt;
        private float _lifetime;
        private bool _flashed, _impact, _water, _show;
        private GameObject _muzzleEffect, _waterImpact, _groundImpact;
        private TrailRenderer _trail;
        private Renderer _renderer;

        public void Initialize(CannonNetworkController controller, Cannon cannon, int id,
            Vector3 origin, Vector3 velocity, Vector3 gravity,
            double started, float lifetime, GameObject muzzleEffect)
        {
            _controller = controller; _cannon = cannon; _id = id; _origin = origin; _velocity = velocity; _gravity = gravity;
            _started = started; _lifetime = lifetime; _muzzleEffect = muzzleEffect;
            _motion = controller.GetComponent<PlatformNetworkTransform>();
            _trail = GetComponent<TrailRenderer>();
            _renderer = GetComponent<Renderer>();
            if (_renderer != null) _renderer.enabled = false;
            if (_trail != null) _trail.emitting = false;
        }
        public void SetImpact(Vector3 point, Vector3 normal, bool water, bool show, double at,
            GameObject waterImpact, GameObject groundImpact)
        {
            _impact = true; _hitPoint = point; _normal = normal; _water = water; _show = show;
            _impactAt = at; _waterImpact = waterImpact; _groundImpact = groundImpact;
        }
        private void LateUpdate()
        {
            if (_controller == null || !_controller.IsSpawned) { Destroy(gameObject); return; }
            var now = !_controller.IsServer && _motion != null ? _motion.PresentationServerTime : _controller.NetworkManager.ServerTime.Time;
            if (now < _started) return;
            var age = (float)(System.Math.Min(now, _impact ? _impactAt : now) - _started);
            transform.position = _origin + _velocity * age + _gravity * (0.5f * age * age);
            if (!_flashed)
            {
                _flashed = true;
                var muzzle = _cannon != null ? _cannon.Muzzle : null;
                CannonEffects.Muzzle(muzzle != null ? muzzle.position : _origin,
                    muzzle != null ? muzzle.forward : _velocity.normalized, _muzzleEffect);
                if (_renderer != null) _renderer.enabled = true;
                if (_trail != null) { _trail.Clear(); _trail.emitting = true; }
            }
            if (_impact && now >= _impactAt)
            {
                if (_show) CannonEffects.Hit(_hitPoint, _normal, _water, _waterImpact, _groundImpact);
                Destroy(gameObject);
            }
            else if (age > _lifetime + 2f) Destroy(gameObject);
        }
        private void OnDestroy() => CannonEffects.Forget(_controller, _id);
    }

}
