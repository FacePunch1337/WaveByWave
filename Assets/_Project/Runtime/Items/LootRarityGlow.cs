using UnityEngine;

namespace WaveByWave.Items
{
    [DefaultExecutionOrder(3500)]
    public sealed class LootRarityGlow : MonoBehaviour
    {
        private LineRenderer _beam;
        private ParticleSystem _sparks;
        private Color _color;
        public void Initialize(Color color, Material material)
        {
            _color = color;
            if (_beam == null)
            {
                var beam = new GameObject("Rarity glow");
                beam.transform.SetParent(transform, false);
                _beam = beam.AddComponent<LineRenderer>();
                _beam.useWorldSpace = true;
                _beam.positionCount = 3;
                _beam.startWidth = 0.28f;
                _beam.endWidth = 0.09f;
                _beam.numCapVertices = 4;
                _beam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _beam.receiveShadows = false;
                var sparks = new GameObject("Rarity sparks");
                sparks.transform.SetParent(transform, false);
                _sparks = sparks.AddComponent<ParticleSystem>();
                var main = _sparks.main;
                main.startLifetime = 1.4f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.06f);
                main.startSpeed = 0.12f;
                main.maxParticles = 16;
                main.simulationSpace = ParticleSystemSimulationSpace.Local;
                main.scalingMode = ParticleSystemScalingMode.Shape;
                var shape = _sparks.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.65f;
                var emission = _sparks.emission;
                emission.rateOverTime = 5f;
                var fade = _sparks.colorOverLifetime;
                fade.enabled = true;
                var gradient = new Gradient();
                gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                    new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.8f, 0.2f), new GradientAlphaKey(0f, 1f) });
                fade.color = gradient;
            }
            _beam.sharedMaterial = material;
            _sparks.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
            var settings = _sparks.main;
            settings.startColor = color;
        }

        private void LateUpdate()
        {
            if (_beam == null) return;
            var position = transform.position;
            _beam.SetPosition(0, position);
            _beam.SetPosition(1, position + Vector3.up * 0.5f);
            _beam.SetPosition(2, position + Vector3.up * 1.4f);
            var pulse = 0.23f + 0.04f * Mathf.Sin(Time.time * 2f);
            var bottom = _color; bottom.a = pulse;
            var top = _color; top.a = 0f;
            _beam.startColor = bottom;
            _beam.endColor = top;
        }
    }
}
