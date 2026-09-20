using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WaveByWave.Generation
{
    /// <summary>
    /// Builds one X-shaped mesh directly on the island surface. Unlike a depth-buffer
    /// projector, this geometry cannot spill onto loot, decorations or characters.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class BuriedChestMarker : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] private float halfExtent = 0.5f;
        [SerializeField, Min(0.01f)] private float halfWidth = 0.07f;
        [SerializeField, Range(2, 16)] private int segmentsPerStroke = 6;
        [SerializeField, Min(0.001f)] private float surfaceOffset = 0.025f;

        private Mesh _mesh;

        public void Configure(ProceduralIsland island)
        {
            if (island == null) return;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;

            var vertices = new List<Vector3>((segmentsPerStroke + 1) * 4);
            var normals = new List<Vector3>(vertices.Capacity);
            var uvs = new List<Vector2>(vertices.Capacity);
            var triangles = new List<int>(segmentsPerStroke * 12);
            AddStroke(island, new Vector2(1f, 1f), vertices, normals, uvs, triangles);
            AddStroke(island, new Vector2(1f, -1f), vertices, normals, uvs, triangles);

            _mesh = new Mesh { name = "Buried chest cross surface" };
            _mesh.SetVertices(vertices);
            _mesh.SetNormals(normals);
            _mesh.SetUVs(0, uvs);
            _mesh.SetTriangles(triangles, 0);
            _mesh.RecalculateBounds();
            GetComponent<MeshFilter>().sharedMesh = _mesh;

            var renderer = GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        public bool ContainsStroke(Vector3 worldPoint, float padding = 0.015f)
        {
            var local = transform.InverseTransformPoint(worldPoint);
            var edge = Mathf.Max(Mathf.Abs(local.x), Mathf.Abs(local.z));
            var distanceToStroke = Mathf.Min(Mathf.Abs(local.x - local.z),
                Mathf.Abs(local.x + local.z)) * 0.70710678f;
            return edge <= halfExtent + padding && distanceToStroke <= halfWidth + padding;
        }

        private void AddStroke(ProceduralIsland island, Vector2 direction,
            List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int> triangles)
        {
            var center = transform.localPosition;
            var lateral = new Vector2(-direction.y, direction.x).normalized * halfWidth;
            var start = vertices.Count;
            var segments = Mathf.Clamp(segmentsPerStroke, 2, 16);
            for (var i = 0; i <= segments; i++)
            {
                var t = i / (float)segments * 2f - 1f;
                var along = direction * (halfExtent * t);
                AddSurfaceVertex(island, center, along - lateral, new Vector2(0f, i / (float)segments),
                    vertices, normals, uvs);
                AddSurfaceVertex(island, center, along + lateral, new Vector2(1f, i / (float)segments),
                    vertices, normals, uvs);
            }
            for (var i = 0; i < segments; i++)
            {
                var a = start + i * 2;
                triangles.Add(a); triangles.Add(a + 2); triangles.Add(a + 1);
                triangles.Add(a + 1); triangles.Add(a + 2); triangles.Add(a + 3);
            }
        }

        private void AddSurfaceVertex(ProceduralIsland island, Vector3 center, Vector2 offset, Vector2 uv,
            List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs)
        {
            var x = center.x + offset.x;
            var z = center.z + offset.y;
            if (!island.TrySurface(x, z, out var point, out var normal))
            {
                point = island.transform.TransformPoint(new Vector3(x, center.y, z));
                normal = island.transform.up;
            }
            vertices.Add(transform.InverseTransformPoint(point + normal * surfaceOffset));
            normals.Add(transform.InverseTransformDirection(normal).normalized);
            uvs.Add(uv);
        }

        private void OnDestroy()
        {
            if (_mesh == null) return;
            if (Application.isPlaying) Destroy(_mesh);
            else DestroyImmediate(_mesh);
        }
    }
}
