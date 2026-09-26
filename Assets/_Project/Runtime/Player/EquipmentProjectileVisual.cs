using UnityEngine;
using WaveByWave.Effects;
using WaveByWave.Ships;

namespace WaveByWave.Player
{
    [DefaultExecutionOrder(9600)]
    public sealed class EquipmentProjectileVisual : MonoBehaviour
    {
        private PlayerEquipment _equipment;
        private Vector3 _origin, _velocity, _point, _normal;
        private float _gravity, _lifetime;
        private double _started, _impactAt;
        private bool _impact, _water, _show, _waterEntry, _waterEntryShown;
        private Vector3 _waterEntryPoint;
        private double _waterEntryAt;
        private GameObject _waterImpact, _groundImpact;
        private Renderer _renderer;
        private TrailRenderer _trail;

        public static EquipmentProjectileVisual Create(PlayerEquipment equipment, Vector3 origin, Vector3 velocity,
            float gravity, double started, float lifetime, GameObject projectilePrefab)
        {
            if (projectilePrefab == null) return null;
            var ball = Instantiate(projectilePrefab);
            ball.transform.SetPositionAndRotation(origin, Quaternion.identity);
            if (!ball.TryGetComponent<EquipmentProjectileVisual>(out var view))
            { Debug.LogError("Musket projectile prefab requires EquipmentProjectileVisual.", projectilePrefab); Destroy(ball); return null; }
            view._equipment = equipment; view._origin = origin; view._velocity = velocity;
            view._gravity = gravity; view._started = started; view._lifetime = lifetime;
            view._renderer = ball.GetComponentInChildren<Renderer>();
            view._trail = ball.GetComponentInChildren<TrailRenderer>();
            if (view._trail != null) view._trail.emitting = false;
            return view;
        }
        public void SetImpact(Vector3 point, Vector3 normal, bool water, bool show, double at,
            GameObject waterImpact, GameObject groundImpact)
        {
            _impact = true; _point = point; _normal = normal; _water = water; _show = show;
            _impactAt = at; _waterImpact = waterImpact; _groundImpact = groundImpact;
        }
        public void SetWaterEntry(Vector3 point, double at, GameObject waterImpact)
        {
            _waterEntry = true;
            _waterEntryPoint = point;
            _waterEntryAt = at;
            _waterImpact = waterImpact;
        }
        private void LateUpdate()
        {
            if (_equipment == null || !_equipment.IsSpawned) { Destroy(gameObject); return; }
            var now = _equipment.NetworkManager.ServerTime.Time;
            var t = (float)(now - _started);
            if (_renderer != null) _renderer.enabled = t >= 0f;
            if (_trail != null) _trail.emitting = t >= 0f;
            if (_waterEntry && !_waterEntryShown && now >= _waterEntryAt)
            {
                _waterEntryShown = true;
                CannonEffects.Hit(_waterEntryPoint, Vector3.up, true, _waterImpact, null);
            }
            if (_impact && now >= _impactAt)
            {
                transform.position = _point;
                if (_show) CannonEffects.Hit(_point, _normal, _water, _waterImpact, _groundImpact);
                Destroy(gameObject); return;
            }
            if (t > _lifetime + 1f) { Destroy(gameObject); return; }
            transform.position = _origin + _velocity * Mathf.Max(0f, t) + Vector3.down * (0.5f * _gravity * t * t);
        }
        public static void CreateWaterPour(Vector3 origin, Vector3 direction, GameObject prefab) =>
            OneShotEffect.Spawn(prefab, origin, direction);
    }
}
