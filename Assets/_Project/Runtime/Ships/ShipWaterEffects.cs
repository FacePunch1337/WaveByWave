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
            main.loop = true; main.duration = 1f; main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.65f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1f, 2.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.07f);
            main.startColor = new Color(0.55f, 0.85f, 1f, 0.7f);
            main.gravityModifier = 0.5f; main.maxParticles = 48;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.cullingMode = ParticleSystemCullingMode.Pause;
            var shape = particles.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = 14f; shape.radius = 0.055f;
            var emission = particles.emission; emission.rateOverTime = 22f;
            var size = particles.sizeOverLifetime; size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 2f; renderer.velocityScale = 0.1f;
            particles.Play();
            return particles;
        }
    }
}
