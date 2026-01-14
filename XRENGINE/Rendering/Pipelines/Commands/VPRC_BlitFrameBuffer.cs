using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using System;
using System.Linq;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Performs a framebuffer blit/resolve between two cached FBOs.
    /// </summary>
    public class VPRC_BlitFrameBuffer : ViewportRenderCommand
    {
        public string? SourceFBOName { get; set; }
        public string? DestinationFBOName { get; set; }
        public EReadBufferMode ReadBuffer { get; set; } = EReadBufferMode.ColorAttachment0;
        public bool BlitColor { get; set; } = true;
        public bool BlitDepth { get; set; } = false;
        public bool BlitStencil { get; set; } = false;
        public bool LinearFilter { get; set; } = false;

        public VPRC_BlitFrameBuffer SetOptions(
            string sourceName,
            string destinationName,
            EReadBufferMode readBuffer,
            bool blitColor,
            bool blitDepth,
            bool blitStencil,
            bool linearFilter)
        {
            SourceFBOName = sourceName;
            DestinationFBOName = destinationName;
            ReadBuffer = readBuffer;
            BlitColor = blitColor;
            BlitDepth = blitDepth;
            BlitStencil = blitStencil;
            LinearFilter = linearFilter;
            return this;
        }

        protected override void Execute()
        {
            if (SourceFBOName is null || DestinationFBOName is null)
                return;

            int passIndex = ResolvePassIndex($"Blit_{SourceFBOName}_to_{DestinationFBOName}");
            using var passScope = passIndex != int.MinValue
                ? Engine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : default;

            var source = ActivePipelineInstance.GetFBO<XRFrameBuffer>(SourceFBOName);
            var destination = ActivePipelineInstance.GetFBO<XRFrameBuffer>(DestinationFBOName);
            if (source is null || destination is null)
                return;

            AbstractRenderer.Current?.BlitFBOToFBO(
                source,
                destination,
                ReadBuffer,
                BlitColor,
                BlitDepth,
                BlitStencil,
                LinearFilter);
        }

        private int ResolvePassIndex(string passName)
        {
            var metadata = ParentPipeline?.PassMetadata;
            if (metadata is null)
                return int.MinValue;

            var match = metadata.FirstOrDefault(m => string.Equals(m.Name, passName, StringComparison.OrdinalIgnoreCase));
            return match?.PassIndex ?? int.MinValue;
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (SourceFBOName is null || DestinationFBOName is null)
                return;

            var builder = context.GetOrCreateSyntheticPass($"Blit_{SourceFBOName}_to_{DestinationFBOName}")
                .WithStage(RenderGraphPassStage.Transfer);

            builder.SampleTexture(MakeFboColorResource(SourceFBOName));
            builder.UseColorAttachment(MakeFboColorResource(DestinationFBOName));
        }
    }
}
