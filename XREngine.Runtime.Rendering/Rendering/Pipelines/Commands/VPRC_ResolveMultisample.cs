using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    [RenderCommandInfo(ERenderCommandCategory.Transfer, DoesGpuWork = true)]
    public class VPRC_ResolveMultisample : ViewportRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation(ResolveDepth || ResolveStencil ? "depth-resolve" : "color-resolve");

        private readonly XRFrameBuffer _sourceFbo = new();
        private readonly XRFrameBuffer _destinationFbo = new();

        [RenderResourceName(ERenderResourceNameKind.Texture)]
        public string? SourceTextureName { get; set; }
        [RenderResourceName(ERenderResourceNameKind.Texture)]
        public string? DestinationTextureName { get; set; }
        public int SourceMipLevel { get; set; }
        public int SourceLayerIndex { get; set; }
        public int DestinationMipLevel { get; set; }
        public int DestinationLayerIndex { get; set; }
        [System.ComponentModel.DefaultValue(EReadBufferMode.ColorAttachment0)]
        public EReadBufferMode ReadBuffer { get; set; } = EReadBufferMode.ColorAttachment0;
        [System.ComponentModel.DefaultValue(true)]
        public bool ResolveColor { get; set; } = true;
        public bool ResolveDepth { get; set; }
        public bool ResolveStencil { get; set; }

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
                ResolveColor,
                ResolveDepth,
                ResolveStencil,
                false);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (SourceTextureName is null || DestinationTextureName is null)
                return;

            context.GetOrCreateSyntheticPass($"ResolveMultisample_{SourceTextureName}_to_{DestinationTextureName}")
                .WithStage(ERenderGraphPassStage.Transfer)
                .SampleTexture(SourceTextureName)
                .UseColorAttachment(DestinationTextureName);
        }
    }
}
