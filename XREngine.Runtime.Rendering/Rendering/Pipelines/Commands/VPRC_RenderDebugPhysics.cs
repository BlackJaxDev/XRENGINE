using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Scene.Physics.DebugVisualization;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_RenderDebugPhysics : ViewportRenderCommand
    {
        public string? RenderGraphPassName { get; set; }
        public PhysicsDebugDepthMode DepthMode { get; set; } = PhysicsDebugDepthMode.DepthTested;

        protected override void Execute()
        {
            if (RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass)
                return;

            using (RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(ResolveRenderGraphPassIndex()))
            using (ActivePipelineInstance.RenderState.PushRenderingCamera(ActivePipelineInstance.RenderState.SceneCamera))
                ActivePipelineInstance.RenderState.WindowViewport?.World?.DebugRenderPhysics(DepthMode);
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

            return DepthMode == PhysicsDebugDepthMode.DepthTested
                ? (int)EDefaultRenderPass.OpaqueForward
                : (int)EDefaultRenderPass.OnTopForward;
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
