using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace GaussianSplatting
{
    /// <summary>
    /// URP Renderer Feature that properly integrates Gaussian Splat rendering
    /// with correct matrix setup. Supports both legacy and RenderGraph paths.
    /// </summary>
    public class GaussianSplatRenderFeature : ScriptableRendererFeature
    {
        private GaussianSplatRenderPass _renderPass;

        public override void Create()
        {
            _renderPass = new GaussianSplatRenderPass();
            _renderPass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(_renderPass);
        }

        protected override void Dispose(bool disposing)
        {
            _renderPass?.Dispose();
        }
    }

    public class GaussianSplatRenderPass : ScriptableRenderPass
    {
        private const string ProfilerTag = "Gaussian Splat Render";
        private ProfilingSampler _profilingSampler = new ProfilingSampler(ProfilerTag);

        public GaussianSplatRenderPass()
        {
            renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        }

        // Legacy path (Compatibility Mode / older URP)
        [System.Obsolete]
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var camera = renderingData.cameraData.camera;
            ExecutePass(context, camera);
        }

        // RenderGraph path (URP 14+ / Unity 2023+)
        private class PassData
        {
            public Camera camera;
            public GaussianSplatRenderer[] renderers;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var cameraData = frameData.Get<UniversalCameraData>();
            var resourceData = frameData.Get<UniversalResourceData>();
            var camera = cameraData.camera;

            // Skip preview cameras
            if (camera.cameraType == CameraType.Preview)
                return;

            // Find all active GaussianSplatRenderer components
            var renderers = Object.FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.None);
            if (renderers == null || renderers.Length == 0)
            {
                return;
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(ProfilerTag, out var passData, _profilingSampler))
            {
                passData.camera = camera;
                passData.renderers = renderers;

                // Bind to the active camera targets so URP sets correct per-camera constants
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                {
                    foreach (var renderer in data.renderers)
                    {
                        if (renderer != null && renderer.isActiveAndEnabled)
                        {
                            renderer.RenderWithRasterCommandBuffer(ctx.cmd, data.camera);
                        }
                    }
                });
            }
        }

        private void ExecutePass(ScriptableRenderContext context, Camera camera)
        {
            // Skip preview cameras
            if (camera.cameraType == CameraType.Preview)
                return;

            // Find all active GaussianSplatRenderer components
            var renderers = Object.FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.None);
            if (renderers == null || renderers.Length == 0)
            {
                return;
            }

            CommandBuffer cmd = CommandBufferPool.Get(ProfilerTag);
            using (new ProfilingScope(cmd, _profilingSampler))
            {
                foreach (var renderer in renderers)
                {
                    if (renderer.isActiveAndEnabled)
                    {
                        renderer.RenderWithCommandBuffer(cmd, camera);
                    }
                }
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public void Dispose()
        {
        }
    }
}
