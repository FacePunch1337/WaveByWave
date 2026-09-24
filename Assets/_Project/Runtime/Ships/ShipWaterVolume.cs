using UnityEngine;
using UnityEngine.Rendering;

namespace WaveByWave.Ships
{
    // The generated surface uses a real Stylized Water 3 material, clipped to
    // the hull sections. No WaterObject or collider is created for buoyancy.
    [ExecuteAlways]
    public sealed class ShipWaterVolume : MonoBehaviour
    {
        public MeshRenderer OceanCutout;
        public MeshRenderer InteriorWater;
        [Tooltip("Real Stylized Water 3 material, including its Wave Profile and underwater settings.")]
        public Material StylizedWaterMaterial;
        public Bounds LocalBounds = new(Vector3.zero, new Vector3(5f, 2.5f, 15f));
        [Tooltip("XZ outline in this object's coordinates. Empty uses Local Bounds. Configure after replacing the volume mesh.")]
        public Vector2[] Footprint = System.Array.Empty<Vector2>();
        [Range(0f, 1f)] public float EditorFillPreview = 0.35f;
        private MaterialPropertyBlock _properties;
        private ShipOceanCutout _cutout;
        private MeshRenderer _surfaceRenderer;
        private Mesh _surfaceMesh;
        private GameObject _surfaceObject;
        private Material _runtimeMaterial;
        private Material _runtimeSource;
        public Material RuntimeWaterMaterial => EnsureWaterMaterial();
        public float Level(float fill) => Mathf.Lerp(LocalBounds.min.y, LocalBounds.max.y, Mathf.Clamp01(fill));

        public bool ContainsColumn(Vector3 world)
        {
            var p = transform.InverseTransformPoint(world);
            return ContainsXZ(new Vector2(p.x, p.z));
        }

        public bool MasksOceanAt(Vector3 world)
        {
            if (OceanCutout == null || !OceanCutout.enabled) return false;
            if (_cutout == null || _cutout.gameObject != OceanCutout.gameObject)
                _cutout = OceanCutout.GetComponent<ShipOceanCutout>();
            if (_cutout != null) return _cutout.Contains(world);
            if (!ContainsColumn(world)) return false;
            var p = OceanCutout.transform.InverseTransformPoint(world);
            var mesh = OceanCutout != null ? OceanCutout.GetComponent<MeshFilter>()?.sharedMesh : null;
            var bounds = mesh != null ? mesh.bounds : LocalBounds;
            return p.y >= bounds.min.y - 0.05f && p.y <= bounds.max.y + 0.75f;
        }

        public bool ContainsXZ(Vector2 point)
        {
            if (point.x < LocalBounds.min.x || point.x > LocalBounds.max.x ||
                point.y < LocalBounds.min.z || point.y > LocalBounds.max.z) return false;
            if (Footprint == null || Footprint.Length < 3) return true;
            var inside = false;
            for (int i = 0, j = Footprint.Length - 1; i < Footprint.Length; j = i++)
            {
                var a = Footprint[i]; var b = Footprint[j];
                if ((a.y > point.y) != (b.y > point.y) &&
                    point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        public float HeightAt(Vector3 world, float fill)
        {
            var p = transform.InverseTransformPoint(world);
            p.y = Level(fill);
            return transform.TransformPoint(p).y;
        }

        public bool RaySurface(Vector3 origin, Vector3 direction, float distance, float fill, out Vector3 point)
        {
            point = default;
            if (fill <= 0f) return false;
            var o = transform.InverseTransformPoint(origin);
            var d = transform.InverseTransformVector(direction);
            if (Mathf.Abs(d.y) < 0.0001f) return false;
            var t = (Level(fill) - o.y) / d.y;
            if (t < 0f || t > distance) return false;
            point = origin + direction * t;
            return ContainsColumn(point);
        }

        private void OnEnable()
        {
            if (InteriorWater != null) InteriorWater.forceRenderingOff = true;
            RenderPipelineManager.beginCameraRendering -= BeforeCamera;
            RenderPipelineManager.beginCameraRendering += BeforeCamera;
        }

        private void BeforeCamera(ScriptableRenderContext _, Camera camera)
        {
            if (_surfaceRenderer != null && _surfaceRenderer.enabled) ApplyCutoutProperties();
        }

        public void Present(float fill, bool sinking)
        {
            // Keep the real ocean clipped out of the hull even during the
            // sinking animation. Letting it back in covers the interior walls
            // from a submerged camera before the return to port.
            if (OceanCutout != null) OceanCutout.enabled = true;
            if (InteriorWater != null) InteriorWater.forceRenderingOff = true;
            EnsureSurface();
            if (_surfaceRenderer == null) return;
            var material = EnsureWaterMaterial();
            if (_cutout == null && OceanCutout != null) _cutout = OceanCutout.GetComponent<ShipOceanCutout>();
            var cutReady = _cutout != null && _cutout.Ready;
            _surfaceRenderer.enabled = fill > 0.00001f && material != null && cutReady;
            _surfaceRenderer.sharedMaterial = material;
            _surfaceObject.transform.localPosition = Vector3.up * Level(fill);
            if (cutReady) ApplyCutoutProperties();
        }

        private void ApplyCutoutProperties()
        {
            if (_surfaceRenderer == null || _cutout == null || !_cutout.Ready) return;
            _properties ??= new MaterialPropertyBlock();
            _surfaceRenderer.GetPropertyBlock(_properties);
            _properties.SetFloat("_WBWInteriorWater", 1f);
            _properties.SetTexture("_WBWInteriorCutSections", _cutout.Sections);
            _properties.SetMatrix("_WBWInteriorWorldToNormalized", _cutout.WorldToNormalized);
            _surfaceRenderer.SetPropertyBlock(_properties);
        }

        private Material EnsureWaterMaterial()
        {
            if (StylizedWaterMaterial == null) return null;
            if (_runtimeMaterial != null && _runtimeSource == StylizedWaterMaterial) return _runtimeMaterial;
            ReleaseMaterial();
            _runtimeSource = StylizedWaterMaterial;
            _runtimeMaterial = new Material(StylizedWaterMaterial)
            { name = StylizedWaterMaterial.name + " (ship interior)", hideFlags = HideFlags.HideAndDontSave };
            if (_runtimeMaterial.HasProperty("_Cull"))
                _runtimeMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _runtimeMaterial.SetShaderPassEnabled("WaterHeight", false);
            return _runtimeMaterial;
        }

        private void ReleaseMaterial()
        {
            if (_runtimeMaterial != null)
            { if (Application.isPlaying) Destroy(_runtimeMaterial); else DestroyImmediate(_runtimeMaterial); }
            _runtimeMaterial = null;
            _runtimeSource = null;
        }

        private void EnsureSurface()
        {
            if (_surfaceRenderer != null) return;
            _surfaceObject = new GameObject("Interior water surface", typeof(MeshFilter), typeof(MeshRenderer));
            _surfaceObject.hideFlags = HideFlags.HideAndDontSave;
            _surfaceObject.transform.SetParent(transform, false);
            var filter = _surfaceObject.GetComponent<MeshFilter>();
            _surfaceMesh = BuildSurfaceMesh(LocalBounds);
            _surfaceMesh.hideFlags = HideFlags.HideAndDontSave;
            filter.sharedMesh = _surfaceMesh;
            _surfaceRenderer = _surfaceObject.GetComponent<MeshRenderer>();
            _surfaceRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _surfaceRenderer.receiveShadows = false;
            _surfaceRenderer.sharedMaterial = EnsureWaterMaterial();
        }

        // A static grid, deformed by the shader; no per-frame mesh rebuilding.
        public static Mesh BuildSurfaceMesh(Bounds bounds)
        {
            const int across = 24, along = 96;
            var vertices = new Vector3[(across + 1) * (along + 1)];
            var uv = new Vector2[vertices.Length];
            var tangents = new Vector4[vertices.Length];
            var colors = new Color[vertices.Length];
            var triangles = new int[across * along * 6];
            for (var z = 0; z <= along; z++) for (var x = 0; x <= across; x++)
            {
                var i = z * (across + 1) + x;
                vertices[i] = new Vector3(
                    Mathf.Lerp(bounds.min.x, bounds.max.x, x / (float)across), 0f,
                    Mathf.Lerp(bounds.min.z, bounds.max.z, z / (float)along));
                uv[i] = new Vector2(x / (float)across, z / (float)along);
                tangents[i] = new Vector4(1f, 0f, 0f, 1f);
                colors[i] = Color.white;
            }
            var t = 0;
            for (var z = 0; z < along; z++) for (var x = 0; x < across; x++)
            {
                var i = z * (across + 1) + x;
                triangles[t++] = i; triangles[t++] = i + across + 1; triangles[t++] = i + 1;
                triangles[t++] = i + 1; triangles[t++] = i + across + 1; triangles[t++] = i + across + 2;
            }
            var mesh = new Mesh { name = "Interior water grid", vertices = vertices,
                uv = uv, tangents = tangents, colors = colors, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.bounds = new Bounds(new Vector3(bounds.center.x, 0f, bounds.center.z),
                new Vector3(bounds.size.x, Mathf.Max(2f, bounds.size.y), bounds.size.z));
            return mesh;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= BeforeCamera;
            if (InteriorWater != null) InteriorWater.forceRenderingOff = true;
            if (_surfaceObject != null)
            {
                if (Application.isPlaying) Destroy(_surfaceObject); else DestroyImmediate(_surfaceObject);
            }
            if (_surfaceMesh != null)
            {
                if (Application.isPlaying) Destroy(_surfaceMesh); else DestroyImmediate(_surfaceMesh);
            }
            _surfaceObject = null; _surfaceMesh = null; _surfaceRenderer = null;
            ReleaseMaterial();
        }

        private void Update()
        { if (!Application.isPlaying) Present(EditorFillPreview, false); }

        private void OnDrawGizmosSelected()
        {
            var old = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.05f, 0.8f, 1f, 0.6f);
            Gizmos.DrawWireCube(LocalBounds.center, LocalBounds.size);
            Gizmos.matrix = old;
        }
    }
}
