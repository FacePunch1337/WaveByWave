using Unity.Netcode;
using UnityEngine;
using WaveByWave.Ships;

namespace WaveByWave.Enemies
{
    [DisallowMultipleComponent]
    public sealed class EnemyShipProjectileVisual : MonoBehaviour
    {
        private Vector3 _origin, _velocity, _gravity, _point, _normal;
        private float _started, _lifetime, _impactAt;
        private bool _impact, _water, _show, _flashed;
        private GameObject _muzzle, _waterEffect, _impactEffect;
        private Renderer[] _renderers;
        private TrailRenderer[] _trails;

        public void Initialize(Vector3 origin, Vector3 velocity, Vector3 gravity, float started, float lifetime,
            GameObject muzzle, GameObject waterEffect, GameObject impactEffect)
        {
            _origin = origin;
            _velocity = velocity;
            _gravity = gravity;
            _started = started;
            _lifetime = lifetime;
            _muzzle = muzzle;
            _waterEffect = waterEffect;
            _impactEffect = impactEffect;
            _renderers = GetComponentsInChildren<Renderer>(true);
            _trails = GetComponentsInChildren<TrailRenderer>(true);
            foreach (var renderer in _renderers) renderer.enabled = false;
            foreach (var trail in _trails) trail.emitting = false;
        }

        public void SetImpact(Vector3 point, Vector3 normal, bool water, bool show, float at)
        {
            _impact = true;
            _point = point;
            _normal = normal;
            _water = water;
            _show = show;
            _impactAt = at;
        }

        private void LateUpdate()
        {
            var manager = NetworkManager.Singleton;
            var now = manager != null && manager.IsListening ? (float)manager.ServerTime.Time : Time.time;
            if (now < _started) return;
            var sampleTime = _impact ? Mathf.Min(now, _impactAt) : now;
            var age = Mathf.Max(0f, sampleTime - _started);
            transform.position = _origin + _velocity * age + _gravity * (0.5f * age * age);
            if (!_flashed)
            {
                _flashed = true;
                CannonEffects.Muzzle(_origin, _velocity.normalized, _muzzle);
                foreach (var renderer in _renderers) renderer.enabled = true;
                foreach (var trail in _trails) { trail.Clear(); trail.emitting = true; }
            }
            if (_impact && now >= _impactAt)
            {
                if (_show) CannonEffects.Hit(_point, _normal, _water, _waterEffect, _impactEffect);
                Destroy(gameObject);
            }
            else if (age > _lifetime + 2f) Destroy(gameObject);
        }
    }
}
