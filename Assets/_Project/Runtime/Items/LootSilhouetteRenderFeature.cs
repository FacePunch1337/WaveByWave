using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace WaveByWave.Items
{
    // A shared geometry mask, not an AABB/depth heuristic. Only render entities use
    // this layer; gameplay colliders and prefab materials are left unchanged.
    public sealed class LootSilhouetteRenderFeature : ScriptableRendererFeature
    {
        public const int ItemLayer = 9; // LootItemVisual in TagManager.asset.
        private static readonly int MaskId = Shader.PropertyToID("_LootItemSilhouetteTexture");
        [SerializeField] private Material silhouetteMaterial;
        private SilhouettePass _pass;

        public override void Create()
        {
            _pass?.Dispose();
            _pass = new SilhouettePass(silhouetteMaterial);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (silhouetteMaterial != null) renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing) => _pass?.Dispose();

        private sealed class SilhouettePass : ScriptableRenderPass
        {
            private static readonly ShaderTagId[] Tags =
            {
                new("UniversalForward"), new("UniversalForwardOnly"), new("SRPDefaultUnlit")
            };
            private readonly Material _material;
            private RTHandle _legacyMask;

            public SilhouettePass(Material material)
            {
                _material = material;
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
                profilingSampler = new ProfilingSampler("Loot visible silhouettes");
            }

            private static FilteringSettings Filter => new(RenderQueueRange.opaque, 1 << ItemLayer);

            private DrawingSettings SetOverride(DrawingSettings settings)
            {
                for (var i = 1; i < Tags.Length; i++) settings.SetShaderPassName(i, Tags[i]);
                settings.overrideMaterial = _material;
                settings.overrideMaterialPassIndex = 0;
                settings.perObjectData = PerObjectData.None;
                return settings;
            }

            private sealed class PassData { public RendererListHandle Items; }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                var camera = frameData.Get<UniversalCameraData>();
                var rendering = frameData.Get<UniversalRenderingData>();
                var lights = frameData.Get<UniversalLightData>();

                // Match the camera depth dimensions, XR slices and MSAA. Resolve to a
                // single-channel mask when sampled, preserving antialiased silhouettes.
                var descriptor = renderGraph.GetTextureDesc(resources.activeDepthTexture);
                descriptor.name = "Loot item silhouettes";
                descriptor.format = GraphicsFormat.R8_UNorm;
                descriptor.bindTextureMS = false;
                descriptor.isShadowMap = false;
                descriptor.filterMode = FilterMode.Point;
                descriptor.memoryless = RenderTextureMemoryless.None;
                descriptor.clearBuffer = true;
                descriptor.clearColor = Color.clear;
                var mask = renderGraph.CreateTexture(descriptor);

                var drawing = SetOverride(RenderingUtils.CreateDrawingSettings(Tags[0], rendering,
                    camera, lights, camera.defaultOpaqueSortFlags));
                using var builder = renderGraph.AddRasterRenderPass<PassData>(
                    "Loot visible silhouettes", out var data, profilingSampler);
                data.Items = renderGraph.CreateRendererList(new RendererListParams(rendering.cullResults,
                    drawing, Filter));
                builder.UseRendererList(data.Items);
                builder.SetRenderAttachment(mask, 0, AccessFlags.Write);
                // Read-only scene depth rejects occluded triangles without touching
                // camera depth/stencil (in particular Stylized Water's stencil mask).
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
                builder.SetGlobalTextureAfterPass(mask, MaskId);
                builder.AllowPassCulling(false); // Also clear when the last item disappears.
                builder.SetRenderFunc((PassData pass, RasterGraphContext context) =>
                    context.cmd.DrawRendererList(pass.Items));
            }

            // Compatibility-mode fallback for projects which disable Render Graph.
#pragma warning disable CS0618, CS0672
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                var descriptor = renderingData.cameraData.cameraTargetDescriptor;
                descriptor.depthBufferBits = 0;
                descriptor.graphicsFormat = GraphicsFormat.R8_UNorm;
                descriptor.bindMS = false;
                descriptor.memoryless = RenderTextureMemoryless.None;
                RenderingUtils.ReAllocateHandleIfNeeded(ref _legacyMask, descriptor, FilterMode.Point,
                    TextureWrapMode.Clamp, name: "Loot item silhouettes");
                ConfigureTarget(_legacyMask, renderingData.cameraData.renderer.cameraDepthTargetHandle);
                ConfigureClear(ClearFlag.Color, Color.clear);
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var drawing = SetOverride(CreateDrawingSettings(Tags[0], ref renderingData,
                    renderingData.cameraData.defaultOpaqueSortFlags));
                var listParameters = new RendererListParams(renderingData.cullResults, drawing, Filter);
                var list = context.CreateRendererList(ref listParameters);
                var cmd = CommandBufferPool.Get("Loot visible silhouettes");
                cmd.DrawRendererList(list);
                cmd.SetGlobalTexture(MaskId, _legacyMask.nameID);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
#pragma warning restore CS0618, CS0672

            public void Dispose() => _legacyMask?.Release();
        }
    }
}
