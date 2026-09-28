using UnityEngine;

namespace WaveByWave.Ships
{
    public static class ShipWaterEffects
    {
        public static ParticleSystem CreateLeak(Transform parent, Material material)
        {
            var go = new GameObject("Hull leak spray");
            go.transform.SetParent(parent, false);
            var particles = go.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particles.main;
            main.loop = true; main.duration = 1f; main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.9f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.2f, 4.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.11f);
            main.startColor = new Color(0.55f, 0.9f, 1f, 0.95f);
            main.gravityModifier = 0.7f; main.maxParticles = 160;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.cullingMode = ParticleSystemCullingMode.Pause;
            var shape = particles.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = 11f; shape.radius = 0.075f;
            var emission = particles.emission; emission.rateOverTime = 60f;
            var color = particles.colorOverLifetime;
            color.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.8f, 0.97f, 1f), 0f),
                    new GradientColorKey(new Color(0.2f, 0.72f, 1f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.78f, 0.65f),
                    new GradientAlphaKey(0f, 1f)
                });
            color.color = new ParticleSystem.MinMaxGradient(gradient);
            var size = particles.sizeOverLifetime; size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.65f), new Keyframe(0.18f, 1f), new Keyframe(1f, 0.2f)));
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 3.5f; renderer.velocityScale = 0.18f;
            renderer.sortingOrder = 25;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            particles.Play();
            return particles;
        }
    }
}
