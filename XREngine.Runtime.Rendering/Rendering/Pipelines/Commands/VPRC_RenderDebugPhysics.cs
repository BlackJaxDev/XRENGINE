using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_RenderDebugPhysics : ViewportRenderCommand
    {
        public string? RenderGraphPassName { get; set; }

        protected override void Execute()
        {
            if (RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass)
                return;

            using (RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(ResolveRenderGraphPassIndex()))
            using (ActivePipelineInstance.RenderState.PushRenderingCamera(ActivePipelineInstance.RenderState.SceneCamera))
                ActivePipelineInstance.RenderState.WindowViewport?.World?.DebugRenderPhysics();
        }

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
