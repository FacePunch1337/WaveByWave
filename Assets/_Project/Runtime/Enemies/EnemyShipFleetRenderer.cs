using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace WaveByWave.Enemies
{
    // Reads the authored prefab once. Only private material copies and draw matrices are
    // created; the prefab, its materials and its hierarchy are never modified.
    internal sealed class EnemyShipFleetRenderer : IDisposable
    {
        private struct Part
        {
            public Mesh Mesh;
            public int Submesh;
            public Matrix4x4 Local;
            public RenderParams Parameters;
        }

        private readonly List<Part> _parts = new();
        private readonly Dictionary<Material, Material> _materials = new();
        private readonly List<Matrix4x4> _roots = new(1024);
        // Arbitrary authored scales require both objectToWorld and worldToObject.
        // Unity's default instancing buffer holds 511 such pairs per draw.
        private readonly Matrix4x4[] _matrices = new Matrix4x4[511];
        private readonly Plane[] _planes = new Plane[6];
        private Bounds _bounds;
        private Bounds _localBounds;
        private bool _hasCamera;
        public bool Available { get; private set; }

        public EnemyShipFleetRenderer(GameObject prefab)
        {
            if (!SystemInfo.supportsInstancing || prefab.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
                return;
            var rootInverse = prefab.transform.worldToLocalMatrix;
            var excludedLods = new HashSet<Renderer>();
            foreach (var group in prefab.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = group.GetLODs();
                for (var i = 1; i < lods.Length; i++)
                    foreach (var renderer in lods[i].renderers) excludedLods.Add(renderer);
            }
            var hasBounds = false;
            foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!renderer.enabled || excludedLods.Contains(renderer) ||
                    !renderer.TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh == null) continue;
                var active = true;
                for (var node = renderer.transform; node != null; node = node.parent)
                {
                    if (!node.gameObject.activeSelf) { active = false; break; }
                    if (node == prefab.transform) break;
                }
                if (!active) continue;
                var local = rootInverse * renderer.transform.localToWorldMatrix;
                var meshBounds = filter.sharedMesh.bounds;
                for (var corner = 0; corner < 8; corner++)
                {
                    var point = local.MultiplyPoint3x4(meshBounds.center + Vector3.Scale(meshBounds.extents,
                        new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1,
                            (corner & 4) == 0 ? -1 : 1)));
                    if (!hasBounds) { _localBounds = new Bounds(point, Vector3.zero); hasBounds = true; }
                    else _localBounds.Encapsulate(point);
                }
                var sources = renderer.sharedMaterials;
                for (var submesh = 0; submesh < filter.sharedMesh.subMeshCount && submesh < sources.Length; submesh++)
                {
                    var source = sources[submesh];
                    if (source == null) continue;
                    if (!_materials.TryGetValue(source, out var material))
                    {
                        material = new Material(source) { enableInstancing = true,
                            name = source.name + " (fleet instances)", hideFlags = HideFlags.HideAndDontSave };
                        _materials.Add(source, material);
                    }
                    _parts.Add(new Part { Mesh = filter.sharedMesh, Submesh = submesh, Local = local,
                        Parameters = new RenderParams(material) { layer = renderer.gameObject.layer,
                            renderingLayerMask = renderer.renderingLayerMask,
                            shadowCastingMode = ShadowCastingMode.Off, receiveShadows = renderer.receiveShadows } });
                }
            }
            Available = _parts.Count > 0;
        }

        public void Begin(Camera camera)
        {
            _roots.Clear();
            _hasCamera = camera != null;
            if (_hasCamera) GeometryUtility.CalculateFrustumPlanes(camera, _planes);
        }

        public void Add(Matrix4x4 matrix)
        {
            var size = _localBounds.extents.magnitude * 2f * Mathf.Max(matrix.lossyScale.x,
                Mathf.Max(matrix.lossyScale.y, matrix.lossyScale.z));
            var bounds = new Bounds(matrix.MultiplyPoint3x4(_localBounds.center), Vector3.one * size);
            if (_hasCamera && !GeometryUtility.TestPlanesAABB(_planes, bounds)) return;
            if (_roots.Count == 0) _bounds = bounds;
            else _bounds.Encapsulate(bounds);
            _roots.Add(matrix);
        }

        public void Draw()
        {
            if (_roots.Count == 0) return;
            foreach (var part in _parts)
            {
                var parameters = part.Parameters;
                parameters.worldBounds = _bounds;
                for (var start = 0; start < _roots.Count; start += _matrices.Length)
                {
                    var count = Mathf.Min(_matrices.Length, _roots.Count - start);
                    for (var i = 0; i < count; i++) _matrices[i] = _roots[start + i] * part.Local;
                    Graphics.RenderMeshInstanced(parameters, part.Mesh, part.Submesh, _matrices, count);
                }
            }
        }

        public void Dispose()
        {
            foreach (var material in _materials.Values) if (material != null) Object.Destroy(material);
            _materials.Clear();
            _parts.Clear();
            _roots.Clear();
        }
    }
}
