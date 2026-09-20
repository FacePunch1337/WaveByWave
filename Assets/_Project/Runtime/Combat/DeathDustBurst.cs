using UnityEngine;

namespace WaveByWave.Combat
{
    internal sealed class DeathDustBurst : MonoBehaviour
    {
        private static Material _material;

        public static void Create(Vector3 position, float scale)
        {
            var root = new GameObject("Death Dust Burst", typeof(ParticleSystem), typeof(DeathDustBurst));
            root.transform.position = position;
            var particles = root.GetComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = particles.main;
            main.duration = 0.2f;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.65f, 1.15f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.4f * scale, 3.8f * scale);
            main.startSize = new ParticleSystem.MinMaxCurve(0.11f * scale, 0.28f * scale);
            main.startColor = new ParticleSystem.MinMaxGradient(Color.white, new Color(1f, 1f, 1f, 0.7f));
            main.gravityModifier = -0.08f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 64;

            var emission = particles.emission;
            emission.enabled = false;
            var shape = particles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.48f * scale;

            var noise = particles.noise;
            noise.enabled = true;
            noise.strength = 0.65f * scale;
            noise.frequency = 1.1f;
            noise.scrollSpeed = 0.8f;

            var color = particles.colorOverLifetime;
            color.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.9f, 0.45f), new GradientAlphaKey(0f, 1f) });
            color.color = gradient;

            var renderer = root.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            renderer.material = GetMaterial();
            renderer.sortingOrder = 100;

            particles.Emit(42);
            particles.Play();
            Destroy(root, 1.5f);
        }

        private static Material GetMaterial()
        {
            if (_material != null)
                return _material;
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ??
                         Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Unlit/Color");
            _material = new Material(shader)
            {
                name = "Death Dust (Runtime)",
                hideFlags = HideFlags.HideAndDontSave,
                color = Color.white
            };
            return _material;
        }
    }
}
