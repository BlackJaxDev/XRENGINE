using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Resolves an MSAA GBuffer FBO to a non-MSAA GBuffer FBO by blitting each color attachment
    /// and depth-stencil individually. Required because glBlitFramebuffer only operates on
    /// one color read/draw buffer pair at a time.
    /// </summary>
    public class VPRC_ResolveMsaaGBuffer : ViewportRenderCommand
    {
        public string? SourceMsaaFBOName { get; set; }
        public string? DestinationFBOName { get; set; }

        /// <summary>
        /// Number of color attachments to resolve (CA0 through CA(N-1)).
        /// </summary>
        public int ColorAttachmentCount { get; set; } = 4;

        public bool ResolveDepthStencil { get; set; } = true;

        public VPRC_ResolveMsaaGBuffer SetOptions(
            string sourceMsaaFBO,
            string destinationFBO,
            int colorAttachmentCount = 4,
            bool resolveDepthStencil = true)
        {
            SourceMsaaFBOName = sourceMsaaFBO;
            DestinationFBOName = destinationFBO;
            ColorAttachmentCount = colorAttachmentCount;
            ResolveDepthStencil = resolveDepthStencil;
            return this;
        }

        private static readonly EReadBufferMode[] ColorAttachments =
        [
            EReadBufferMode.ColorAttachment0,
            EReadBufferMode.ColorAttachment1,
            EReadBufferMode.ColorAttachment2,
            EReadBufferMode.ColorAttachment3,
        ];

        protected override void Execute()
        {
            if (SourceMsaaFBOName is null || DestinationFBOName is null)
                return;

            var source = ActivePipelineInstance.GetFBO<XRFrameBuffer>(SourceMsaaFBOName);
            var destination = ActivePipelineInstance.GetFBO<XRFrameBuffer>(DestinationFBOName);
            if (source is null || destination is null)
                return;

            var renderer = AbstractRenderer.Current;
            if (renderer is null)
                return;

            // Resolve each color attachment individually
            int count = Math.Min(ColorAttachmentCount, ColorAttachments.Length);
            for (int i = 0; i < count; i++)
            {
                renderer.BlitFBOToFBOSingleAttachment(
                    source, destination,
                    ColorAttachments[i], ColorAttachments[i],
                    colorBit: true, depthBit: false, stencilBit: false,
                    linearFilter: false);
            }

            // Resolve depth-stencil (not affected by read/draw buffer selection)
            if (ResolveDepthStencil)
            {
                renderer.BlitFBOToFBO(
                    source, destination,
                    EReadBufferMode.None,
                    colorBit: false, depthBit: true, stencilBit: true,
                    linearFilter: false);
            }
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (SourceMsaaFBOName is null || DestinationFBOName is null)
                return;

            var builder = context.GetOrCreateSyntheticPass($"ResolveMsaaGBuffer_{SourceMsaaFBOName}_to_{DestinationFBOName}")
                .WithStage(ERenderGraphPassStage.Transfer);

            builder.SampleTexture(MakeFboColorResource(SourceMsaaFBOName));
            builder.UseColorAttachment(MakeFboColorResource(DestinationFBOName));
        }
    }
}
