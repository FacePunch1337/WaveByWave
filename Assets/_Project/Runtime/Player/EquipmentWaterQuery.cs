using System;
using StylizedWater3;
using UnityEngine;

namespace WaveByWave.Player
{
    // Reuses CPU wave samples. No Rigidbody, GPU readbacks or per-query native allocations.
    internal sealed class EquipmentWaterQuery : IDisposable
    {
        private readonly HeightQuerySystem.Interface _water = new()
        { method = HeightQuerySystem.Interface.Method.CPU, autoFind = true };
        private readonly HeightQuerySystem.Sampler _samples = new();
        public EquipmentWaterQuery(WaveProfile profile)
        { _water.waveProfile = profile; _samples.SetSampleCount(2, true); }
        public bool TryHeight(Vector3 point, out float height)
        {
            height = 0f;
            if (_water.GetWaterObject(point) == null || _water.waterObject.material == null) return false;
            _samples.positions[0] = _samples.positions[1] = point;
            height = _water.GetWaterLevel();
            _samples.heightValues[0] = _samples.heightValues[1] = height;
            if (_water.waveProfile != null) Gerstner.ComputeHeight(_samples, _water);
            height = _samples.heightValues[0];
            return true;
        }
        public bool Crossing(Vector3 from, Vector3 to, out Vector3 point, out float fraction)
        {
            point = default; fraction = 1f;
            if (_water.GetWaterObject((from + to) * 0.5f) == null || _water.waterObject.material == null) return false;
            _samples.positions[0] = from; _samples.positions[1] = to;
            var level = _water.GetWaterLevel();
            _samples.heightValues[0] = _samples.heightValues[1] = level;
            if (_water.waveProfile != null) Gerstner.ComputeHeight(_samples, _water);
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
