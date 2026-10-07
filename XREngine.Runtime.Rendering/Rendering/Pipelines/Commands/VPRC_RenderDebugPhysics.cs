using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.RenderGraph;
using XREngine.Scene.Physics.DebugVisualization;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_RenderDebugPhysics : ViewportRenderCommand
    {
        public string? RenderGraphPassName { get; set; }
        public PhysicsDebugDepthMode DepthMode { get; set; } = PhysicsDebugDepthMode.DepthTested;

        /// <summary>Draws the world's physics debug frame when enabled.</summary>
        public bool RenderWorldPhysics { get; set; } = true;

        protected override void Execute()
        {
            if (RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass)
                return;

            using (RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(ResolveRenderGraphPassIndex()))
            using (ActivePipelineInstance.RenderState.PushRenderingCamera(ActivePipelineInstance.RenderState.SceneCamera))
            {
                IRuntimeRenderWorld? world = ActivePipelineInstance.RenderState.WindowViewport?.World;
                if (RenderWorldPhysics)
                    world?.DebugRenderPhysics(DepthMode);
                if (DepthMode == PhysicsDebugDepthMode.DepthTested && world is not null)
                    GPUPhysicsChainDispatcher.Instance.RenderSelectedGpuDebug(world.WorldContext);
            }
        }

        private int ResolveRenderGraphPassIndex()
        {
            if (!string.IsNullOrWhiteSpace(RenderGraphPassName) &&
                ParentPipeline?.TryGetRenderPassIndex(RenderGraphPassName, out int passIndex) == true)
            {
                return passIndex;
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
                .KeepSecondaryDynamic(ERenderPassSecondaryCachePolicy.DynamicDebug)
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
