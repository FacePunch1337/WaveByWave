using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WaveByWave.Ships
{
    /// <summary>Camera-independent ocean clipping between the two sides of a hull.</summary>
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class ShipOceanCutout : MonoBehaviour
    {
        public const int MaxVolumes = 16;
        public const int ProfileWidth = 256, ProfileHeight = 128;
        [HideInInspector] public Mesh SourceMesh;
        [HideInInspector] public Bounds SourceBounds;
        [HideInInspector] public Texture2D Sections;
        [HideInInspector] public string SourceSignature;
        private MeshRenderer _renderer;
        private MeshFilter _filter;
        private static readonly List<ShipOceanCutout> Active = new();
        private static readonly Matrix4x4[] Matrices = new Matrix4x4[MaxVolumes];
        private static readonly Vector4[] Slices = new Vector4[MaxVolumes];
        private static Texture2DArray _atlas;
        private static bool _dirty = true;
        private static readonly int CountId = Shader.PropertyToID("_WBWCutCount");
        private static readonly int MatricesId = Shader.PropertyToID("_WBWCutMatrices");
        private static readonly int SlicesId = Shader.PropertyToID("_WBWCutSlices");
        private static readonly int AtlasId = Shader.PropertyToID("_WBWCutSections");
#if UNITY_EDITOR
        public static event System.Action<ShipOceanCutout> BakeRequested;
#endif
        public bool Ready => Sections != null && _filter != null && SourceMesh == _filter.sharedMesh;
        public bool MaskEnabled => isActiveAndEnabled && Ready && _renderer != null && _renderer.enabled;
        public Matrix4x4 WorldToNormalized => Matrix4x4.Scale(new Vector3(
            1f / SourceBounds.size.x, 1f / SourceBounds.size.y, 1f / SourceBounds.size.z)) *
            Matrix4x4.Translate(-SourceBounds.min) * transform.worldToLocalMatrix;

        public bool Contains(Vector3 world)
        {
            if (!MaskEnabled) return false;
            var p = WorldToNormalized.MultiplyPoint3x4(world);
            if (p.x < 0 || p.x > 1 || p.y < 0 || p.y > 1 || p.z < 0 || p.z > 1) return false;
            var sides = Sections.GetPixelBilinear(p.z, p.y);
            return p.x >= sides.r && p.x <= sides.g;
        }

        private void OnEnable()
        {
            _renderer = GetComponent<MeshRenderer>(); _filter = GetComponent<MeshFilter>();
            if (!Active.Contains(this)) Active.Add(this);
            if (Active.Count == MaxVolumes + 1)
                Debug.LogWarning($"Ocean cutout supports up to {MaxVolumes} enabled volumes at once.", this);
            _dirty = true;
            RenderPipelineManager.beginCameraRendering -= BeforeCamera;
            RenderPipelineManager.beginCameraRendering += BeforeCamera;
#if UNITY_EDITOR
            if (!Application.isPlaying) BakeRequested?.Invoke(this);
#endif
        }
        private void OnDisable()
        {
            Active.Remove(this); _dirty = true;
            Shader.SetGlobalInt(CountId, 0);
            if (Active.Count != 0) return;
            RenderPipelineManager.beginCameraRendering -= BeforeCamera;
            ReleaseAtlas();
        }
        public void ProfileChanged() => _dirty = true;
#if UNITY_EDITOR
        private void Update()
        {
            if (!Application.isPlaying && !Ready) BakeRequested?.Invoke(this);
        }
        private void OnValidate() { _dirty = true; }
#endif
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetGlobals()
        {
            // Also handles entering play mode with domain/scene reload disabled.
            Shader.SetGlobalInt(CountId, 0); _dirty = true;
            RenderPipelineManager.beginCameraRendering -= BeforeCamera;
            RenderPipelineManager.beginCameraRendering += BeforeCamera;
        }
        private static void ReleaseAtlas()
        {
            if (_atlas == null) return;
            if (Application.isPlaying) Destroy(_atlas); else DestroyImmediate(_atlas);
            _atlas = null;
        }
        private static void BeforeCamera(ScriptableRenderContext context, Camera camera)
        {
            Upload(camera);
        }
        // Public for isolated render checks. Normal rendering calls this once per camera.
        public static void Upload(Camera camera)
        {
            if (_dirty)
            {
                ReleaseAtlas();
                if (Active.Count > 0)
                {
                    _atlas = new Texture2DArray(ProfileWidth, ProfileHeight, MaxVolumes, TextureFormat.RGFloat, false, true)
                    { name = "Ship ocean cut sections", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                        hideFlags = HideFlags.HideAndDontSave };
                    for (var i = 0; i < Active.Count && i < MaxVolumes; i++)
                        if (Active[i] != null && Active[i].Ready) _atlas.SetPixels(Active[i].Sections.GetPixels(), i);
                    _atlas.Apply(false, false);
                    Shader.SetGlobalTexture(AtlasId, _atlas);
                }
                _dirty = false;
            }
            var count = 0;
            for (var i = 0; i < Active.Count && i < MaxVolumes; i++)
            {
                var cut = Active[i];
                if (cut == null || !cut.MaskEnabled || (camera.cullingMask & (1 << cut.gameObject.layer)) == 0) continue;
                if (camera.scene.IsValid() && cut.gameObject.scene != camera.scene) continue;
#if UNITY_EDITOR
                if (!camera.scene.IsValid())
                {
                    if (camera.cameraType == CameraType.SceneView)
                    {
                        if (!UnityEditor.SceneManagement.StageUtility.GetCurrentStageHandle().Contains(cut.gameObject)) continue;
                    }
                    else if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(cut.gameObject.scene)) continue;
                }
#endif
                Matrices[count] = cut.WorldToNormalized;
                Slices[count++] = new Vector4(i, 0, 0, 0);
            }
            Shader.SetGlobalInt(CountId, count);
            if (count == 0) return;
            Shader.SetGlobalMatrixArray(MatricesId, Matrices);
            Shader.SetGlobalVectorArray(SlicesId, Slices);
        }
    }
}
