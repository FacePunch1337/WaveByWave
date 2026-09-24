using System;
using System.Collections.Generic;
using StylizedWater3;
using Unity.Mathematics;
using UnityEngine;
using WaveByWave.Ships;

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
            public bool Enabled, Initialized;
            public float Speed, Frequency, Height;
            public float2 Direction;
            public Texture ProfileTexture;
            public WaveProfile MatchedProfile;
            public int RetryProfileAtFrame;
        }
        private static readonly Dictionary<Material, WaveMaterialState> MaterialStates = new();
        private static readonly HashSet<Material> MissingProfileWarnings = new();

        public sealed class CachedSurface
        {
            internal WaterObject Water;
            internal WaveProfile Profile;
            internal bool WavesEnabled;
            internal float Speed, Frequency, Height;
            internal float2 Direction;
            internal int Layers;
            public WaveProfile ActiveProfile => Profile;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetMaterialStates()
        {
            MaterialStates.Clear();
            MissingProfileWarnings.Clear();
        }

        private static WaveMaterialState GetMaterialState(Material material)
        {
            MaterialStates.TryGetValue(material, out var state);
            if (state.Initialized && state.Frame == Time.frameCount) return state;
            state.Frame = Time.frameCount;
            state.Initialized = true;
            var texture = material.GetTexture(ShaderParams.Properties._WaveProfile);
            if (texture != state.ProfileTexture ||
                texture != null && state.MatchedProfile == null && Time.frameCount >= state.RetryProfileAtFrame)
            {
                state.ProfileTexture = texture;
                state.MatchedProfile = null;
                state.RetryProfileAtFrame = Time.frameCount + 300;
                if (texture != null)
                {
                    foreach (var candidate in Resources.FindObjectsOfTypeAll<WaveProfile>())
                        if (candidate != null && candidate.shaderParametersLUT == texture)
                        { state.MatchedProfile = candidate; break; }
                }
            }
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
            return state;
        }

        private static WaveProfile ProfileFor(WaveMaterialState state, WaveProfile fallback)
        {
            if (state.ProfileTexture == null) return fallback;
            if (fallback != null && fallback.shaderParametersLUT == state.ProfileTexture) return fallback;
            return state.MatchedProfile != null ? state.MatchedProfile : fallback;
        }

        private void ComputeSamples(float level)
        {
            var state = GetMaterialState(_water.waterObject.material);
            var profile = ProfileFor(state, _water.waveProfile);
            if (!state.Enabled || profile == null) return;
            // Let the asset apply its internal floating-origin offset and clock.
            Gerstner.ComputeHeight(profile, _samples, level, state.Speed, state.Frequency,
                state.Direction, new float3(1f, state.Height, 1f), state.Layers);
        }
        public EquipmentWaterQuery(WaveProfile profile)
        { _water.waveProfile = profile; _samples.SetSampleCount(4, true); }

        // Capture the water material at spawn. Each ship keeps its own snapshot, so
        // a later spawn can use new settings without changing ships already afloat.
        public CachedSurface CacheSurface(Vector3 point, WaveProfile fallback)
        {
            var water = WaterObject.Find(point, false);
            if (water == null || water.material == null) return null;
            var state = GetMaterialState(water.material);
            if (state.ProfileTexture != null && state.MatchedProfile == null &&
                (fallback == null || fallback.shaderParametersLUT != state.ProfileTexture) &&
                MissingProfileWarnings.Add(water.material))
                Debug.LogWarning($"No loaded WaveProfile matches water material '{water.material.name}'. " +
                    "Ships spawned now will use their fallback profile.");
            return new CachedSurface
            {
                Water = water,
                Profile = ProfileFor(state, fallback),
                WavesEnabled = state.Enabled,
                Direction = state.Direction,
                Speed = state.Speed,
                Frequency = state.Frequency,
                Layers = state.Layers,
                Height = state.Height
            };
        }

        public bool TrySurface(Vector3 point, Vector2 size, CachedSurface source,
            out float height, out Vector3 normal)
            => TrySurface(point, size, Quaternion.identity, source, out height, out normal);

        public bool TrySurface(Vector3 point, Vector2 size, Quaternion yaw, CachedSurface source,
            out float height, out Vector3 normal)
            => TrySurface(point, size, yaw, 0.35f, source, out height, out normal);

        public bool TrySurface(Vector3 point, Vector2 size, Quaternion yaw, float rollAmount,
            CachedSurface source, out float height, out Vector3 normal)
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
                _samples.heightValues[2], _samples.heightValues[3], rollAmount);
            return true;
        }
        public bool TryWaterLevel(Vector3 point, out float level)
        {
            level = 0f;
            var ship = ShipFlooding.CompartmentAt(point);
            if (ship != null)
            { level = ship.WaterVolume.HeightAt(point, ship.Fill); return ship.WaterLitres > 0.001f; }
            if (_water.GetWaterObject(point) == null || _water.waterObject.material == null) return false;
            level = _water.GetWaterLevel(); return true;
        }
        public bool TryHeight(Vector3 point, out float height)
        {
            height = 0f;
            var ship = ShipFlooding.CompartmentAt(point);
            if (ship != null)
            { height = ship.WaterVolume.HeightAt(point, ship.Fill); return ship.WaterLitres > 0.001f; }
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
            var ship = ShipFlooding.CompartmentAt(point);
            if (ship != null)
                return false; // Internal floodwater can be scooped, but never drives swimming/buoyancy.
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
        public void Dispose() => _samples.Dispose();
    }
}
