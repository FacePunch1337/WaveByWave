using UnityEngine;
using UnityEngine.UI;

namespace WaveByWave.Player
{
    // A small ring and cannonball silhouette; no texture or extra UI assets.
    public sealed class CannonReloadRing : MaskableGraphic
    {
        private const int Segments = 64;
        private float _progress;
        public float Progress
        {
            get => _progress;
            set
            {
                if (Mathf.Approximately(_progress, value)) return;
                _progress = Mathf.Clamp01(value);
                SetVerticesDirty();
            }
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            var rect = GetPixelAdjustedRect();
            var radius = Mathf.Min(rect.width, rect.height) * 0.48f;
            AddRing(mesh, rect.center, radius, radius * 0.82f, 1f, new Color(0.035f, 0.055f, 0.075f, 0.85f));
            AddRing(mesh, rect.center, radius, radius * 0.82f, _progress, new Color(1f, 0.67f, 0.25f));
            AddRing(mesh, rect.center, radius * 0.47f, 0f, 1f, new Color(0.15f, 0.18f, 0.22f, 0.95f));
            AddRing(mesh, rect.center + Vector2.one * radius * 0.15f, radius * 0.13f, 0f, 1f,
                new Color(0.7f, 0.74f, 0.8f, 0.9f));
        }

        private static void AddRing(VertexHelper mesh, Vector2 center, float outer, float inner, float fill, Color tint)
        {
            var count = Mathf.CeilToInt(Segments * fill);
            for (var i = 0; i < count; i++)
            {
                var a = Mathf.PI * 0.5f - i * (Mathf.PI * 2f / Segments);
                var b = Mathf.PI * 0.5f - Mathf.Min((i + 1f) / Segments, fill) * Mathf.PI * 2f;
                var start = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                var end = new Vector2(Mathf.Cos(b), Mathf.Sin(b));
                var index = mesh.currentVertCount;
                mesh.AddVert(center + start * outer, tint, Vector2.zero);
                mesh.AddVert(center + end * outer, tint, Vector2.zero);
                mesh.AddVert(center + end * inner, tint, Vector2.zero);
                mesh.AddVert(center + start * inner, tint, Vector2.zero);
                mesh.AddTriangle(index, index + 1, index + 2);
                mesh.AddTriangle(index, index + 2, index + 3);
            }
        }
    }
}
