using System;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Renders an FBO quad to another FBO.
    /// Useful for transforming every pixel of previous FBO.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="destination"></param>
    public class VPRC_RenderQuadToFBO : ViewportRenderCommand
    {
        public string? SourceQuadFBOName { get; set; }
        public string? DestinationFBOName { get; set; } = null;

        public void SetTargets(string sourceQuadFBOName, string? destinationFBOName = null)
        {
            SourceQuadFBOName = sourceQuadFBOName;
            DestinationFBOName = destinationFBOName;
        }

        protected override void Execute()
        {
            if (SourceQuadFBOName is null)
                return;

            XRQuadFrameBuffer? sourceFBO = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(SourceQuadFBOName);
            if (sourceFBO is null)
                return;

            sourceFBO.Render(DestinationFBOName is null ? null : ActivePipelineInstance.GetFBO<XRFrameBuffer>(DestinationFBOName));
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (SourceQuadFBOName is null)
                return;

            string destination = DestinationFBOName
                ?? context.CurrentRenderTarget?.Name
                ?? RenderGraphResourceNames.OutputRenderTarget;

            var builder = context.GetOrCreateSyntheticPass($"QuadBlit_{SourceQuadFBOName}_to_{destination}");
            builder.WithStage(RenderGraphPassStage.Graphics);
            builder.SampleTexture(MakeFboColorResource(SourceQuadFBOName));

            RenderPassLoadOp colorLoad = RenderPassLoadOp.Load;
            RenderPassStoreOp colorStore = RenderPassStoreOp.Store;
            RenderGraphAccess access = RenderGraphAccess.ReadWrite;

            if (context.CurrentRenderTarget is { } bound &&
                string.Equals(bound.Name, destination, StringComparison.OrdinalIgnoreCase))
            {
                colorLoad = bound.ConsumeColorLoadOp();
                colorStore = bound.GetColorStoreOp();
                access = bound.ColorAccess;
            }

            builder.UseColorAttachment(MakeFboColorResource(destination), access, colorLoad, colorStore);
        }
    }
}
