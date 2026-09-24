using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace WaveByWave.Generation
{
    // One fullscreen pass, no fog mesh, particles, per-frame allocations or DOTS job.
    // It is not enqueued outside an active night wave, so daytime has zero pass cost.
    public sealed class NightBattlefieldFogFeature : ScriptableRendererFeature
    {
        [SerializeField] private Shader fogShader;
        private Material _material;
        private Texture2D _noise;
        private FogPass _pass;
        private static readonly int CenterRadius = Shader.PropertyToID("_BattlefieldCenterRadius");
        private static readonly int NearColor = Shader.PropertyToID("_BattlefieldFogNearColor");
        private static readonly int FarColor = Shader.PropertyToID("_BattlefieldFogFarColor");
        private static readonly int Shape = Shader.PropertyToID("_BattlefieldFogShape");
        private static readonly int Noise = Shader.PropertyToID("_BattlefieldFogNoise");

        public Shader FogShader { get => fogShader; set => fogShader = value; }

        public override void Create()
        {
            Release();
            if (fogShader == null) return;
            _material = CoreUtils.CreateEngineMaterial(fogShader);
            _noise = CreateNoise();
            _material.SetTexture("_FogNoiseTex", _noise);
            _pass = new FogPass(_material);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null || renderingData.cameraData.cameraType != CameraType.Game ||
                renderingData.cameraData.renderType != CameraRenderType.Base ||
                !NightWaveController.TryGetBattlefield(out var zone, out var settings)) return;
            _material.SetVector(CenterRadius, zone);
            _material.SetColor(NearColor, settings.FogNearColor);
            _material.SetColor(FarColor, settings.FogFarColor);
            _material.SetVector(Shape, new Vector4(settings.FogDensity, settings.FogEdgeWidth,
                settings.FogHeight, settings.FogViewDistance));
            _material.SetVector(Noise, new Vector4(settings.FogNoiseScale, settings.FogWindSpeed,
                settings.FogNoiseStrength, settings.FogSampleCount));
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing) => Release();

        private void Release()
        {
            _pass?.Dispose();
            _pass = null;
            CoreUtils.Destroy(_material);
            CoreUtils.Destroy(_noise);
            _material = null;
            _noise = null;
        }

        private static Texture2D CreateNoise()
        {
            const int size = 128;
            var random = new System.Random(15873);
            var large = new float[8 * 8];
            var detail = new float[24 * 24];
            for (var i = 0; i < large.Length; i++) large[i] = (float)random.NextDouble();
            for (var i = 0; i < detail.Length; i++) detail[i] = (float)random.NextDouble();
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
            {
                var u = x / (float)size;
                var v = y / (float)size;
                var a = (byte)(TileNoise(large, 8, u, v) * 255f);
                var b = (byte)(TileNoise(detail, 24, u, v) * 255f);
                pixels[y * size + x] = new Color32(a, b, 0, 255);
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            {
                name = "Night battlefield seamless fog noise",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static float TileNoise(float[] grid, int period, float u, float v)
        {
            var x = u * period;
            var y = v * period;
            var x0 = Mathf.FloorToInt(x) % period;
            var y0 = Mathf.FloorToInt(y) % period;
            var x1 = (x0 + 1) % period;
            var y1 = (y0 + 1) % period;
            var fx = Mathf.SmoothStep(0f, 1f, x - Mathf.Floor(x));
            var fy = Mathf.SmoothStep(0f, 1f, y - Mathf.Floor(y));
            return Mathf.Lerp(Mathf.Lerp(grid[y0 * period + x0], grid[y0 * period + x1], fx),
                Mathf.Lerp(grid[y1 * period + x0], grid[y1 * period + x1], fx), fy);
        }

        private sealed class FogPass : ScriptableRenderPass
        {
            private readonly Material _material;
            private RTHandle _legacyCopy;
            public FogPass(Material material)
            {
                _material = material;
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
                profilingSampler = new ProfilingSampler("Night battlefield fog");
                ConfigureInput(ScriptableRenderPassInput.Depth);
                requiresIntermediateTexture = true;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer) return;
                var source = resources.activeColorTexture;
                var descriptor = renderGraph.GetTextureDesc(source);
                descriptor.name = "Night battlefield fog color";
                descriptor.clearBuffer = false;
                var destination = renderGraph.CreateTexture(descriptor);
                renderGraph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(
                    source, destination, _material, 0), "Night battlefield fog");
                resources.cameraColor = destination;
            }

#pragma warning disable CS0618, CS0672
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                var descriptor = renderingData.cameraData.cameraTargetDescriptor;
                descriptor.depthBufferBits = 0;
                descriptor.msaaSamples = 1;
                RenderingUtils.ReAllocateHandleIfNeeded(ref _legacyCopy, descriptor,
                    FilterMode.Bilinear, TextureWrapMode.Clamp, name: "Night battlefield fog copy");
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var color = renderingData.cameraData.renderer.cameraColorTargetHandle;
                var cmd = CommandBufferPool.Get("Night battlefield fog");
                Blitter.BlitCameraTexture(cmd, color, _legacyCopy);
                Blitter.BlitCameraTexture(cmd, _legacyCopy, color, _material, 0);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
#pragma warning restore CS0618, CS0672

            public void Dispose() => _legacyCopy?.Release();
        }
    }
}
