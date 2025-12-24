using System;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_RenderQuadFBO : ViewportRenderCommand
    {
        public string? FrameBufferName { get; set; }
        public string? TargetFrameBufferName { get; set; }

        protected override void Execute()
        {
            if (FrameBufferName is null)
                return;

            var inputFBO = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(FrameBufferName);
            if (inputFBO is null)
                return;

            inputFBO.Render(TargetFrameBufferName != null ? ActivePipelineInstance.GetFBO<XRFrameBuffer>(TargetFrameBufferName) : null);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (FrameBufferName is null)
                return;

            string target = TargetFrameBufferName
                ?? context.CurrentRenderTarget?.Name
                ?? RenderGraphResourceNames.OutputRenderTarget;

            var builder = context.GetOrCreateSyntheticPass($"QuadRender_{FrameBufferName}_to_{target}");
            builder.WithStage(RenderGraphPassStage.Graphics);
            builder.SampleTexture(MakeFboColorResource(FrameBufferName));

            RenderPassLoadOp colorLoad = RenderPassLoadOp.Load;
            RenderPassStoreOp colorStore = RenderPassStoreOp.Store;
            RenderGraphAccess access = RenderGraphAccess.ReadWrite;

            if (context.CurrentRenderTarget is { } bound &&
                string.Equals(bound.Name, target, StringComparison.OrdinalIgnoreCase))
            {
                colorLoad = bound.ConsumeColorLoadOp();
                colorStore = bound.GetColorStoreOp();
                access = bound.ColorAccess;
            }

            builder.UseColorAttachment(MakeFboColorResource(target), access, colorLoad, colorStore);
        }
    }
}