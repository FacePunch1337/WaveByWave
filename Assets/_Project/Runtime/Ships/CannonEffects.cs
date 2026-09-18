using System.Collections.Generic;
using UnityEngine;

namespace WaveByWave.Ships
{
    public static class CannonEffects
    {
        private static readonly Dictionary<(ShipCannonBattery, int), CannonBallVisual> Shots = new();

        public static void Shot(ShipCannonBattery battery, ShipCannon cannon, int id, Vector3 origin, Vector3 velocity, Vector3 gravity,
            double started, float radius, float lifetime, Material ballMaterial, Material effectMaterial)
        {
            if (!battery.IsClient) return;
            var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.name = "Cannonball";
            ball.GetComponent<Collider>().enabled = false;
            Object.Destroy(ball.GetComponent<Collider>());
            ball.transform.localScale = Vector3.one * radius * 2f;
            ball.transform.position = origin;
            ball.GetComponent<Renderer>().sharedMaterial = ballMaterial;
            var trail = ball.AddComponent<TrailRenderer>();
            trail.sharedMaterial = effectMaterial;
            trail.time = 0.3f;
            trail.startWidth = radius * 0.8f;
            trail.endWidth = 0f;
            trail.startColor = new Color(0.7f, 0.7f, 0.65f, 0.6f);
            trail.endColor = new Color(0.5f, 0.5f, 0.5f, 0f);
            trail.emitting = false;
            var visual = ball.AddComponent<CannonBallVisual>();
            visual.Initialize(battery, cannon, id, origin, velocity, gravity, started, lifetime, effectMaterial);
            Shots[(battery, id)] = visual;
        }

        public static void Impact(ShipCannonBattery battery, int id, Vector3 point, Vector3 normal, bool water,
            bool show, double at, GameObject splash, Material effectMaterial, Material debrisMaterial)
        {
            if (!battery.IsClient) return;
            if (Shots.TryGetValue((battery, id), out var ball) && ball != null)
                ball.SetImpact(point, normal, water, show, at, splash, effectMaterial, debrisMaterial);
        }

        public static void Forget(ShipCannonBattery battery, int id) => Shots.Remove((battery, id));
        public static void ClearShots(ShipCannonBattery battery)
        {
            var remove = new List<CannonBallVisual>();
            foreach (var entry in Shots) if (entry.Key.Item1 == battery && entry.Value != null) remove.Add(entry.Value);
            foreach (var shot in remove) Object.Destroy(shot.gameObject);
        }

        public static void Muzzle(Vector3 point, Vector3 forward, Material material)
        {
            Burst("Cannon flash", point, forward, material, new Color(1f, 0.65f, 0.12f), 12, 0.15f, 0.6f, 7f, 0f);
            Burst("Cannon smoke", point, forward, material, new Color(0.65f, 0.65f, 0.6f, 0.4f), 30, 1.8f, 0.7f, 2f, -0.05f);
        }

        public static void Hit(Vector3 point, Vector3 normal, bool water, GameObject splash, Material effectMaterial, Material debrisMaterial)
        {
            if (water && splash != null)
            {
                var effect = Object.Instantiate(splash, point, Quaternion.identity);
                Object.Destroy(effect, 6f);
                return;
            }
            if (water)
            {
                Burst("Water splash", point, Vector3.up, effectMaterial, new Color(0.6f, 0.9f, 1f), 45, 1f, 0.2f, 6f, 1f);
                return;
            }
            Burst("Impact dust", point + normal * 0.08f, normal, effectMaterial, new Color(0.6f, 0.53f, 0.4f, 0.5f), 30, 1.2f, 0.5f, 3f, 0.1f);
            for (var i = 0; i < 8; i++)
            {
                var piece = GameObject.CreatePrimitive(PrimitiveType.Cube);
                piece.name = "Impact fragment";
                piece.GetComponent<Collider>().enabled = false;
                Object.Destroy(piece.GetComponent<Collider>());
                piece.GetComponent<Renderer>().sharedMaterial = debrisMaterial;
                piece.transform.position = point + normal * 0.12f;
                piece.transform.localScale = Vector3.one * Random.Range(0.04f, 0.12f);
                piece.AddComponent<CannonFragment>().Velocity = normal * Random.Range(2f, 5f) + Random.insideUnitSphere * 2f;
                Object.Destroy(piece, 1.3f);
            }
        }

        private static void Burst(string name, Vector3 position, Vector3 direction, Material material, Color color,
            int count, float life, float size, float speed, float gravity)
        {
            var effect = new GameObject(name);
            effect.transform.SetPositionAndRotation(position, Quaternion.LookRotation(direction));
            var particles = effect.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particles.main;
            main.loop = false;
            main.duration = life;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.6f, life);
            main.startSize = new ParticleSystem.MinMaxCurve(size * 0.3f, size);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.5f, speed);
            main.startColor = color;
            main.gravityModifier = gravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = count;
            var emission = particles.emission;
            emission.rateOverTime = 0f;
            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 35f;
            shape.radius = 0.1f;
            var fade = particles.colorOverLifetime;
            fade.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            fade.color = gradient;
            effect.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
            particles.Play();
            particles.Emit(count);
            Object.Destroy(effect, life + 0.1f);
        }
    }

}
