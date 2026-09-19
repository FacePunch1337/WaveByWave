using UnityEngine;
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
        private bool _impact, _water, _show;
        private GameObject _splash;
        private Material _effect, _debris;
        private Renderer _renderer;
        private TrailRenderer _trail;

        public static EquipmentProjectileVisual Create(PlayerEquipment equipment, Vector3 origin, Vector3 velocity,
            float gravity, double started, float lifetime, float radius, Material metal, Material effect)
        {
            var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.name = "Musket projectile (analytic)";
            var collider = ball.GetComponent<Collider>(); collider.enabled = false; Destroy(collider);
            ball.transform.SetPositionAndRotation(origin, Quaternion.identity);
            ball.transform.localScale = Vector3.one * radius * 2f;
            var view = ball.AddComponent<EquipmentProjectileVisual>();
            view._equipment = equipment; view._origin = origin; view._velocity = velocity;
            view._gravity = gravity; view._started = started; view._lifetime = lifetime;
            view._renderer = ball.GetComponent<Renderer>(); view._renderer.sharedMaterial = metal;
            view._trail = ball.AddComponent<TrailRenderer>();
            view._trail.sharedMaterial = effect; view._trail.time = 0.08f;
            view._trail.startWidth = radius; view._trail.endWidth = 0f;
            view._trail.startColor = new Color(1f, 0.85f, 0.4f, 0.8f);
            view._trail.endColor = new Color(1f, 0.8f, 0.3f, 0f);
            return view;
        }
        public void SetImpact(Vector3 point, Vector3 normal, bool water, bool show, double at,
            GameObject splash, Material effect, Material debris)
        {
            _impact = true; _point = point; _normal = normal; _water = water; _show = show;
            _impactAt = at; _splash = splash; _effect = effect; _debris = debris;
        }
        private void LateUpdate()
        {
            if (_equipment == null || !_equipment.IsSpawned) { Destroy(gameObject); return; }
            var now = _equipment.NetworkManager.ServerTime.Time;
            var t = (float)(now - _started);
            _renderer.enabled = t >= 0f; _trail.emitting = t >= 0f;
            if (_impact && now >= _impactAt)
            {
                transform.position = _point;
                if (_show) CannonEffects.Hit(_point, _normal, _water, _splash, _effect, _debris);
                Destroy(gameObject); return;
            }
            if (t > _lifetime + 1f) { Destroy(gameObject); return; }
            transform.position = _origin + _velocity * Mathf.Max(0f, t) + Vector3.down * (0.5f * _gravity * t * t);
        }
        public static void CreateWaterPour(Vector3 origin, Vector3 direction, Material material)
        {
            var water = new GameObject("Bucket water pour");
            water.transform.SetPositionAndRotation(origin, Quaternion.LookRotation(direction));
            var ps = water.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = false; main.duration = 0.3f; main.startLifetime = 0.65f;
            main.startSpeed = 4f; main.startSize = 0.07f; main.startColor = new Color(0.5f, 0.85f, 1f, 0.8f);
            main.gravityModifier = 1f; main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = ps.emission; emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 35) });
            var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = 12f; shape.radius = 0.08f;
            ps.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
            ps.Play(); Destroy(water, 1.2f);
        }
    }
}
