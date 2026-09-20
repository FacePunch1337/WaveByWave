using System;
using StylizedWater3;
using UnityEngine;

namespace WaveByWave.Player
{
    // Reuses CPU wave samples. No Rigidbody, GPU readbacks or per-query native allocations.
    public sealed class EquipmentWaterQuery : IDisposable
    {
        private readonly HeightQuerySystem.Interface _water = new()
        { method = HeightQuerySystem.Interface.Method.CPU, autoFind = true };
        private readonly HeightQuerySystem.Sampler _samples = new();
        public EquipmentWaterQuery(WaveProfile profile)
        { _water.waveProfile = profile; _samples.SetSampleCount(4, true); }
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
            if (_water.waveProfile != null) Gerstner.ComputeHeight(_samples, _water);
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
