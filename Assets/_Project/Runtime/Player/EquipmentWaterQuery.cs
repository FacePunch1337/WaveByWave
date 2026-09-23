using System;
using System.Collections.Generic;
using StylizedWater3;
using Unity.Mathematics;
using UnityEngine;

namespace WaveByWave.Player
{
    // Reuses CPU wave samples. No Rigidbody, GPU readbacks or per-query native allocations.
    public sealed class EquipmentWaterQuery : IDisposable
    {
        private readonly HeightQuerySystem.Interface _water = new()
        { method = HeightQuerySystem.Interface.Method.CPU, autoFind = true };
        private readonly HeightQuerySystem.Sampler _samples = new();
        private struct WaveMaterialState
        {
            public int Frame, Layers;
            public bool Enabled;
            public float Speed, Frequency, Height;
            public float2 Direction;
        }
        private static readonly Dictionary<Material, WaveMaterialState> MaterialStates = new();
        private readonly Dictionary<WaterObject, CachedSurface> _cachedSurfaces = new();

        public sealed class CachedSurface
        {
            internal WaterObject Water;
            internal WaveProfile Profile;
            internal bool WavesEnabled;
            internal float Speed, Frequency, Height;
            internal float2 Direction;
            internal int Layers;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetMaterialStates() => MaterialStates.Clear();

        private void ComputeSamples(float level)
        {
            var profile = _water.waveProfile;
            if (profile == null) return;
            var material = _water.waterObject.material;
            if (!MaterialStates.TryGetValue(material, out var state) || state.Frame != Time.frameCount)
            {
                state.Frame = Time.frameCount;
                state.Enabled = material.IsKeywordEnabled(ShaderParams.Keywords.Waves);
                if (state.Enabled)
                {
                    var direction = material.GetVector(ShaderParams.Properties._Direction);
                    state.Direction = new float2(direction.x, direction.y);
                    state.Speed = material.GetFloat(ShaderParams.Properties._Speed) *
                        material.GetFloat(ShaderParams.Properties._WaveSpeed);
                    state.Frequency = material.GetFloat(ShaderParams.Properties._WaveFrequency);
                    state.Layers = material.GetInt(ShaderParams.Properties._WaveMaxLayers);
                    state.Height = material.GetFloat(ShaderParams.Properties._WaveHeight);
                }
                MaterialStates[material] = state;
            }
            if (!state.Enabled) return;
            // Let the asset apply its internal floating-origin offset and clock.
            Gerstner.ComputeHeight(profile, _samples, level, state.Speed, state.Frequency,
                state.Direction, new float3(1f, state.Height, 1f), state.Layers);
        }
        public EquipmentWaterQuery(WaveProfile profile)
        { _water.waveProfile = profile; _samples.SetSampleCount(4, true); }

        // The material's LUT selects the actual wave profile. Resolve it once per
        // water body, then keep the material's wave settings for every ship spawned there.
        public CachedSurface CacheSurface(Vector3 point, WaveProfile fallback)
        {
            var water = WaterObject.Find(point, false);
            if (water == null || water.material == null) return null;
            if (_cachedSurfaces.TryGetValue(water, out var cached)) return cached;
            var material = water.material;
            var texture = material.GetTexture(ShaderParams.Properties._WaveProfile);
            var profile = fallback;
            if (texture != null && (profile == null || profile.shaderParametersLUT != texture))
                foreach (var candidate in Resources.FindObjectsOfTypeAll<WaveProfile>())
                    if (candidate != null && candidate.shaderParametersLUT == texture)
                    { profile = candidate; break; }
            if (texture != null && (profile == null || profile.shaderParametersLUT != texture))
                Debug.LogWarning($"[Enemy ships] No loaded WaveProfile matches water material '{material.name}'. Assign its profile through the water scene setup.");
            cached = new CachedSurface { Water = water, Profile = profile,
                WavesEnabled = material.IsKeywordEnabled(ShaderParams.Keywords.Waves) };
            if (cached.WavesEnabled)
            {
                var direction = material.GetVector(ShaderParams.Properties._Direction);
                cached.Direction = new float2(direction.x, direction.y);
                cached.Speed = material.GetFloat(ShaderParams.Properties._Speed) *
                    material.GetFloat(ShaderParams.Properties._WaveSpeed);
                cached.Frequency = material.GetFloat(ShaderParams.Properties._WaveFrequency);
                cached.Layers = material.GetInt(ShaderParams.Properties._WaveMaxLayers);
                cached.Height = material.GetFloat(ShaderParams.Properties._WaveHeight);
            }
            _cachedSurfaces.Add(water, cached);
            return cached;
        }

        public bool TrySurface(Vector3 point, Vector2 size, CachedSurface source,
            out float height, out Vector3 normal)
            => TrySurface(point, size, Quaternion.identity, source, out height, out normal);

        public bool TrySurface(Vector3 point, Vector2 size, Quaternion yaw, CachedSurface source,
            out float height, out Vector3 normal)
        {
            height = 0f;
            normal = Vector3.up;
            if (source == null || source.Water == null) return false;
            var halfX = Mathf.Max(0.05f, size.x * 0.5f);
            var halfZ = Mathf.Max(0.05f, size.y * 0.5f);
            _samples.positions[0] = point + yaw * (Vector3.left * halfX);
            _samples.positions[1] = point + yaw * (Vector3.right * halfX);
            _samples.positions[2] = point + yaw * (Vector3.back * halfZ);
            _samples.positions[3] = point + yaw * (Vector3.forward * halfZ);
            var level = source.Water.transform.position.y;
            for (var i = 0; i < 4; i++) _samples.heightValues[i] = level;
            if (source.WavesEnabled && source.Profile != null)
                Gerstner.ComputeHeight(source.Profile, _samples, level, source.Speed,
                    source.Frequency, source.Direction, new float3(1f, source.Height, 1f), source.Layers);
            height = (_samples.heightValues[0] + _samples.heightValues[1] +
                _samples.heightValues[2] + _samples.heightValues[3]) * 0.25f;
            normal = yaw * HeightQuerySystem.DeriveNormal(_samples.heightValues[0], _samples.heightValues[1],
                _samples.heightValues[2], _samples.heightValues[3], 0.35f);
            return true;
        }
        public bool TryWaterLevel(Vector3 point, out float level)
        {
            level = 0f;
            if (_water.GetWaterObject(point) == null || _water.waterObject.material == null) return false;
            level = _water.GetWaterLevel(); return true;
        }
        public bool TryHeight(Vector3 point, out float height)
        {
            height = 0f;
            if (_water.GetWaterObject(point) == null || _water.waterObject.material == null) return false;
            _samples.positions[0] = _samples.positions[1] = point;
            height = _water.GetWaterLevel();
            _samples.heightValues[0] = _samples.heightValues[1] = height;
            ComputeSamples(height);
            height = _samples.heightValues[0];
            return true;
        }

        public bool TrySurface(Vector3 point, Vector2 size, out float height, out Vector3 normal)
        {
            height = 0f;
            normal = Vector3.up;
            if (_water.GetWaterObject(point) == null || _water.waterObject.material == null) return false;
            var halfX = Mathf.Max(0.05f, size.x * 0.5f);
            var halfZ = Mathf.Max(0.05f, size.y * 0.5f);
            _samples.positions[0] = point + Vector3.left * halfX;
            _samples.positions[1] = point + Vector3.right * halfX;
            _samples.positions[2] = point + Vector3.back * halfZ;
            _samples.positions[3] = point + Vector3.forward * halfZ;
            var level = _water.GetWaterLevel();
            for (var i = 0; i < 4; i++) _samples.heightValues[i] = level;
            ComputeSamples(level);
            height = (_samples.heightValues[0] + _samples.heightValues[1] +
                _samples.heightValues[2] + _samples.heightValues[3]) * 0.25f;
            normal = HeightQuerySystem.DeriveNormal(_samples.heightValues[0], _samples.heightValues[1],
                _samples.heightValues[2], _samples.heightValues[3], 0.35f);
            return true;
        }
        public bool Crossing(Vector3 from, Vector3 to, out Vector3 point, out float fraction)
        {
            point = default; fraction = 1f;
            if (_water.GetWaterObject((from + to) * 0.5f) == null || _water.waterObject.material == null) return false;
            _samples.positions[0] = from; _samples.positions[1] = to;
            var level = _water.GetWaterLevel();
            _samples.heightValues[0] = _samples.heightValues[1] = level;
            ComputeSamples(level);
            var a = _samples.heightValues[0]; var b = _samples.heightValues[1];
            var above = from.y - a;
            var below = to.y - b;
            if (above > 0f && below > 0f) return false;
            fraction = above <= 0f ? 0f : Mathf.Clamp01(above / Mathf.Max(0.0001f, above - below));
            point = Vector3.Lerp(from, to, fraction);
            point.y = Mathf.Lerp(a, b, fraction);
            return true;
        }
        public void Dispose() { _cachedSurfaces.Clear(); _samples.Dispose(); }
    }
}
