using System;
using UnityEngine;

namespace WaveByWave.Generation
{
    [Serializable]
    public sealed class NightLamp
    {
        public Light Light;
        [Min(0f)] public float NightIntensity = 1f;
        [NonSerialized] public bool InitialEnabled;
        [NonSerialized] public float InitialIntensity;
        [NonSerialized] public bool Captured;
    }

    [Serializable]
    public sealed class NightEmission
    {
        public Renderer Renderer;
        [Min(0)] public int MaterialIndex;
        [ColorUsage(true, true)] public Color NightColor = new(1f, 0.62f, 0.24f, 1f);
        [Min(0f)] public float NightIntensity = 2f;

        [NonSerialized] public Material Original;
        [NonSerialized] public Material Runtime;
        [NonSerialized] public Color DayColor;
    }

    // Assign only the lamps/renderers that should respond to night on this map.
    public sealed class DayNightMaterialController : MonoBehaviour
    {
        [SerializeField] private NightLamp[] lamps = Array.Empty<NightLamp>();
        [SerializeField] private NightEmission[] emissiveMaterials = Array.Empty<NightEmission>();

        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        private float _lastBlend = -1f;

        public void ApplyNight(float blend)
        {
            blend = Mathf.Clamp01(blend);
            if (Mathf.Abs(blend - _lastBlend) < 0.005f) return;
            _lastBlend = blend;
            foreach (var lamp in lamps)
            {
                if (lamp?.Light == null) continue;
                if (!lamp.Captured)
                {
                    lamp.InitialEnabled = lamp.Light.enabled;
                    lamp.InitialIntensity = lamp.Light.intensity;
                    lamp.Captured = true;
                }
                lamp.Light.intensity = lamp.NightIntensity * blend;
                lamp.Light.enabled = blend > 0.01f;
            }
            foreach (var binding in emissiveMaterials)
            {
                if (binding?.Renderer == null) continue;
                if (binding.Runtime == null && !CreateRuntimeMaterial(binding)) continue;
                binding.Runtime.SetColor(EmissionColor, Color.Lerp(binding.DayColor,
                    binding.NightColor * binding.NightIntensity, blend));
            }
        }

        private static bool CreateRuntimeMaterial(NightEmission binding)
        {
            var materials = binding.Renderer.sharedMaterials;
            if (binding.MaterialIndex >= materials.Length || binding.MaterialIndex < 0 ||
                materials[binding.MaterialIndex] == null) return false;
            binding.Original = materials[binding.MaterialIndex];
            binding.DayColor = binding.Original.HasProperty(EmissionColor)
                ? binding.Original.GetColor(EmissionColor) : Color.black;
            binding.Runtime = new Material(binding.Original)
            {
                name = binding.Original.name + " (day/night runtime)",
                hideFlags = HideFlags.DontSave
            };
            binding.Runtime.EnableKeyword("_EMISSION");
            materials[binding.MaterialIndex] = binding.Runtime;
            binding.Renderer.sharedMaterials = materials;
            return true;
        }

        public void RestoreMaterials()
        {
            foreach (var lamp in lamps)
            {
                if (lamp?.Light == null || !lamp.Captured) continue;
                lamp.Light.enabled = lamp.InitialEnabled;
                lamp.Light.intensity = lamp.InitialIntensity;
                lamp.Captured = false;
            }
            foreach (var binding in emissiveMaterials)
            {
                if (binding?.Runtime == null) continue;
                if (binding.Renderer != null)
                {
                    var materials = binding.Renderer.sharedMaterials;
                    if (binding.MaterialIndex >= 0 && binding.MaterialIndex < materials.Length &&
                        materials[binding.MaterialIndex] == binding.Runtime)
                    {
                        materials[binding.MaterialIndex] = binding.Original;
                        binding.Renderer.sharedMaterials = materials;
                    }
                }
                if (Application.IsPlaying(gameObject)) Destroy(binding.Runtime);
                else DestroyImmediate(binding.Runtime);
                binding.Runtime = null;
            }
            _lastBlend = -1f;
        }

        private void OnDisable() => RestoreMaterials();
    }
}
