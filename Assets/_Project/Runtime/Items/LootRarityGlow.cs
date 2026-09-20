using UnityEngine;

namespace WaveByWave.Items
{
    [DefaultExecutionOrder(3500)]
    public sealed class LootRarityGlow : MonoBehaviour
    {
        [SerializeField] private LineRenderer beam;
        [SerializeField] private ParticleSystem sparks;
        private Color _color;
        public void Initialize(Color color)
        {
            _color = color;
            if (beam == null) beam = GetComponentInChildren<LineRenderer>(true);
            if (sparks == null) sparks = GetComponentInChildren<ParticleSystem>(true);
            if (beam == null || sparks == null)
            {
                Debug.LogError("Loot rarity effect prefab requires a LineRenderer and ParticleSystem.", this);
                enabled = false;
                return;
            }
            var settings = sparks.main;
            settings.startColor = color;
        }

        private void LateUpdate()
        {
            if (beam == null) return;
            var position = transform.position;
            beam.SetPosition(0, position);
            beam.SetPosition(1, position + Vector3.up * 0.5f);
            beam.SetPosition(2, position + Vector3.up * 1.4f);
            var pulse = 0.23f + 0.04f * Mathf.Sin(Time.time * 2f);
            var bottom = _color; bottom.a = pulse;
            var top = _color; top.a = 0f;
            beam.startColor = bottom;
            beam.endColor = top;
        }
    }
}
