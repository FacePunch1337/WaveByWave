using System;
using System.Collections.Generic;
using UnityEngine;

namespace WaveByWave.Ships
{
    [Serializable]
    public struct HullHoleSite
    {
        public Vector3 Position, Normal;
        public Vector2 UV, UVPerMetre;
    }

    [ExecuteAlways, RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class ShipHullHoles : MonoBehaviour
    {
        public const int MaximumHoles = 24;
        [Tooltip("Allowed rectangles in UV0 (x, y, width, height). Hole centres and texture borders stay inside these regions.")]
        public Rect[] AllowedUVRegions = { new(0f, 0f, 1f, 1f) };
        [Tooltip("Additional hull-local height restriction, useful for atlases with overlapping UV islands.")]
        public Vector2 AllowedHeight = new(-100f, 100f);
        [Range(0.05f, 1f)] public float HoleRadius = 0.3f;
        [Range(0.001f, 0.15f)] public float MaximumUVRadius = 0.035f;
        [Range(0f, 1f)] public float MaximumNormalY = 0.7f;
        public bool ShowAllowedRegions = true;
        public Color RegionColor = new(0.1f, 1f, 0.5f, 0.4f);
        [HideInInspector] public HullHoleSite[] Sites = Array.Empty<HullHoleSite>();
        private MeshRenderer _renderer;
        private MaterialPropertyBlock _block;
        private readonly Vector4[] _holes = new Vector4[MaximumHoles];
        private readonly Vector4[] _positions = new Vector4[MaximumHoles];
        private readonly Vector4[] _normals = new Vector4[MaximumHoles];
        private readonly Vector4[] _regions = new Vector4[8];
        private readonly List<ParticleSystem> _sprays = new();
        private int _shownHoles;

        public bool IsAllowed(Vector2 uv, Vector2 radius, float height, Vector3 normal)
        {
            if (height < AllowedHeight.x || height > AllowedHeight.y || Mathf.Abs(normal.y) > MaximumNormalY) return false;
            foreach (var area in AllowedUVRegions ?? Array.Empty<Rect>())
                if (uv.x - radius.x >= area.xMin && uv.x + radius.x <= area.xMax &&
                    uv.y - radius.y >= area.yMin && uv.y + radius.y <= area.yMax) return true;
            return false;
        }

        public Vector2 RadiusUV(HullHoleSite site) => Vector2.Min(
            Vector2.one * MaximumUVRadius, site.UVPerMetre * HoleRadius);

        public bool TryChooseSite(Vector3 worldImpact, Func<Vector3, bool> occupied, out HullHoleSite chosen)
        {
            chosen = default;
            var best = float.PositiveInfinity;
            var found = false;
            var point = transform.InverseTransformPoint(worldImpact);
            // Sites are sampled once in the editor. Only hits search this bounded array.
            foreach (var site in Sites)
            {
                if (!IsAllowed(site.UV, RadiusUV(site), site.Position.y, site.Normal) || occupied(site.Position)) continue;
                var score = (site.Position - point).sqrMagnitude * UnityEngine.Random.Range(0.65f, 1.4f);
                if (score >= best) continue;
                chosen = site; best = score; found = true;
            }
            return found;
        }

        public void Present(ShipFlooding ship)
        {
            EnsureRenderer();
            if (_renderer == null) return;
            _shownHoles = Mathf.Min(ship.HoleCount, MaximumHoles);
            for (var i = 0; i < _shownHoles; i++)
            {
                var hole = ship.GetHole(i);
                _holes[i] = new Vector4(hole.UV.x, hole.UV.y, hole.RadiusUV.x, hole.RadiusUV.y);
                _positions[i] = new Vector4(hole.Position.x, hole.Position.y, hole.Position.z, HoleRadius * 1.6f);
                _normals[i] = hole.Normal;
            }
            _block.SetInt("_HoleCount", _shownHoles);
            _block.SetVectorArray("_Holes", _holes);
            _block.SetVectorArray("_HolePositions", _positions);
            _block.SetVectorArray("_HoleNormals", _normals);
            _renderer.SetPropertyBlock(_block);
            while (_sprays.Count > _shownHoles)
            { var last = _sprays[^1]; if (last != null) Destroy(last.gameObject); _sprays.RemoveAt(_sprays.Count - 1); }
            while (_sprays.Count < _shownHoles)
                _sprays.Add(ShipWaterEffects.CreateLeak(transform, ship.SprayMaterial));
            for (var i = 0; i < _sprays.Count; i++)
            {
                var hole = ship.GetHole(i);
                var spray = _sprays[i];
                spray.transform.localPosition = hole.Position - hole.Normal * 0.08f;
                spray.transform.localRotation = Quaternion.LookRotation(-hole.Normal, Vector3.up);
                var emission = spray.emission;
                emission.rateOverTime = ship.IsSinking ? 0f : Mathf.Min(45f, 10f + hole.Leak * 4f);
            }
        }

        private void EnsureRenderer()
        {
            if (_renderer == null) _renderer = GetComponent<MeshRenderer>();
            _block ??= new MaterialPropertyBlock();
            _renderer.GetPropertyBlock(_block);
        }

        private void Update()
        {
            if (Application.isPlaying) return;
            WriteRegions();
        }

        private void WriteRegions()
        {
            EnsureRenderer();
            var count = Mathf.Min(8, AllowedUVRegions?.Length ?? 0);
            for (var i = 0; i < count; i++)
            { var r = AllowedUVRegions[i]; _regions[i] = new Vector4(r.xMin, r.yMin, r.xMax, r.yMax); }
            _block.SetInt("_RegionCount", count);
            _block.SetVectorArray("_Regions", _regions);
            _block.SetVector("_AllowedHeight", new Vector4(AllowedHeight.x, AllowedHeight.y, MaximumNormalY, 0));
            _block.SetColor("_RegionColor", RegionColor);
            _block.SetFloat("_ShowRegions", !Application.isPlaying && ShowAllowedRegions ? 1f : 0f);
            _renderer.SetPropertyBlock(_block);
        }

        private void OnEnable()
        {
            EnsureRenderer();
            _block.SetInt("_HoleCount", 0);
            _renderer.SetPropertyBlock(_block);
            WriteRegions();
        }
    }
}
