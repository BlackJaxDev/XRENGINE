using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Scene;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_RenderDebugShapes : ViewportRenderCommand
    {
        public string? RenderGraphPassName { get; set; }

        protected override void Execute()
        {
            if (RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass)
                return;

            try
            {
                XRRenderPipelineInstance instance = ActivePipelineInstance;
                XRCamera? camera = instance.RenderState.SceneCamera
                    ?? instance.RenderState.RenderingCamera
                    ?? instance.LastSceneCamera
                    ?? instance.LastRenderingCamera;

                using (RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(ResolveRenderGraphPassIndex()))
                using (instance.RenderState.PushRenderingCamera(camera))
                {
                    GpuBvhDebugLineRenderer.RenderQueued(
                        instance.RenderState,
                        GpuBvhDebugOverlayLayer.Base);
                    RenderEnabledSpatialTreeDebug(instance, camera);
                    RuntimeEngine.Rendering.Debug.RenderShapes();
                    GpuBvhDebugLineRenderer.RenderQueued(
                        instance.RenderState,
                        GpuBvhDebugOverlayLayer.Highlight);
                }
            }
            finally
            {
                ResetStencilState();
            }
        }

        private static void ResetStencilState()
        {
            RuntimeEngine.Rendering.State.EnableStencilTest(false);
            RuntimeEngine.Rendering.State.StencilMask(0xFF);
            RuntimeEngine.Rendering.State.StencilFunc(EComparison.Always, 0, 0xFF);
            RuntimeEngine.Rendering.State.StencilOp(EStencilOp.Keep, EStencilOp.Keep, EStencilOp.Keep);
        }

        private static void RenderEnabledSpatialTreeDebug(XRRenderPipelineInstance instance, XRCamera? camera)
        {
            if (instance.RenderState.Scene is VisualScene3D scene3d && Is3DSpatialTreePreviewEnabled(instance))
            {
                scene3d.DebugRenderSpatialTreeNodes(camera);
                return;
            }

            if (instance.RenderState.Scene is VisualScene2D scene2d && Is2DSpatialTreePreviewEnabled(instance))
                scene2d.DebugRenderSpatialTreeNodes(camera);
        }

        private static bool Is3DSpatialTreePreviewEnabled(XRRenderPipelineInstance instance)
            => RuntimeRenderingHostServices.Current.Preview3DWorldOctree
            || ResolveWorld(instance)?.PreviewOctrees == true;

        private static bool Is2DSpatialTreePreviewEnabled(XRRenderPipelineInstance instance)
            => RuntimeRenderingHostServices.Current.Preview2DWorldQuadtree
            || ResolveWorld(instance)?.PreviewQuadtrees == true;

        private static IRuntimeRenderWorld? ResolveWorld(XRRenderPipelineInstance instance)
            => instance.RenderState.WindowViewport?.World ?? RuntimeEngine.Rendering.State.RenderingWorld;

        private int ResolveRenderGraphPassIndex()
        {
            if (!string.IsNullOrWhiteSpace(RenderGraphPassName) &&
                ParentPipeline?.PassMetadata is { } metadata)
            {
                foreach (RenderPassMetadata pass in metadata)
                {
                    if (string.Equals(pass.Name, RenderGraphPassName, StringComparison.OrdinalIgnoreCase))
                        return pass.PassIndex;
                }
            }

            return (int)EDefaultRenderPass.OnTopForward;
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (string.IsNullOrWhiteSpace(RenderGraphPassName))
                return;

            var builder = context.GetOrCreateSyntheticPass(RenderGraphPassName, ERenderGraphPassStage.Graphics)
                .UseEngineDescriptors()
                .UseMaterialDescriptors();

            if (context.CurrentRenderTarget is { } target)
            {
                builder.UseColorAttachment(
                    MakeFboColorResource(target.Name),
                    target.ColorAccess,
                    ERenderPassLoadOp.Load,
                    target.GetColorStoreOp());
                UseRenderTargetDepthStencilAttachments(
                    builder,
                    target,
                    ERenderPassLoadOp.Load,
                    ERenderPassLoadOp.Load);
            }
        }
    }
}
