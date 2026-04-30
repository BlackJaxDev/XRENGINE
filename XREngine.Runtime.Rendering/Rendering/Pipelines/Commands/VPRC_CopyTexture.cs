using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_CopyTexture : ViewportRenderCommand
    {
        private readonly XRFrameBuffer _sourceFbo = new();
        private readonly XRFrameBuffer _destinationFbo = new();

        public string? SourceTextureName { get; set; }
        public string? DestinationTextureName { get; set; }
        public int SourceMipLevel { get; set; }
        public int SourceLayerIndex { get; set; }
        public int DestinationMipLevel { get; set; }
        public int DestinationLayerIndex { get; set; }
        public EReadBufferMode ReadBuffer { get; set; } = EReadBufferMode.ColorAttachment0;
        public bool CopyColor { get; set; } = true;
        public bool CopyDepth { get; set; }
        public bool CopyStencil { get; set; }
        public bool LinearFilter { get; set; }

        protected override void Execute()
        {
            if (SourceTextureName is null || DestinationTextureName is null)
                return;

            var instance = ActivePipelineInstance;
            if (!instance.TryGetTexture(SourceTextureName, out XRTexture? sourceTexture) ||
                !instance.TryGetTexture(DestinationTextureName, out XRTexture? destinationTexture) ||
                sourceTexture is not IFrameBufferAttachement sourceAttachment ||
                destinationTexture is not IFrameBufferAttachement destinationAttachment)
            {
                return;
            }

            _sourceFbo.SetRenderTargets([(sourceAttachment, EFrameBufferAttachment.ColorAttachment0, SourceMipLevel, SourceLayerIndex)]);
            _destinationFbo.SetRenderTargets([(destinationAttachment, EFrameBufferAttachment.ColorAttachment0, DestinationMipLevel, DestinationLayerIndex)]);

            AbstractRenderer.Current?.BlitFBOToFBO(
                _sourceFbo,
                _destinationFbo,
                ReadBuffer,
                CopyColor,
                CopyDepth,
                CopyStencil,
                LinearFilter);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (SourceTextureName is null || DestinationTextureName is null)
                return;

            context.GetOrCreateSyntheticPass($"CopyTexture_{SourceTextureName}_to_{DestinationTextureName}")
                .WithStage(ERenderGraphPassStage.Transfer)
                .SampleTexture(SourceTextureName)
                .UseColorAttachment(DestinationTextureName);
        }
    }
}
