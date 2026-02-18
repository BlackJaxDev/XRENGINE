using System;
using XREngine.Rendering.RenderGraph;
using System.Linq;

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

            string target = TargetFrameBufferName
                ?? ActivePipelineInstance.RenderState.OutputFBO?.Name
                ?? RenderGraphResourceNames.OutputRenderTarget;

            int passIndex = ResolvePassIndex($"QuadRender_{FrameBufferName}_to_{target}");
            using var passScope = passIndex != int.MinValue
                ? Engine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : default;

            var inputFBO = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(FrameBufferName);
            if (inputFBO is null)
                return;

            inputFBO.Render(TargetFrameBufferName != null ? ActivePipelineInstance.GetFBO<XRFrameBuffer>(TargetFrameBufferName) : null);
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

            if (FrameBufferName is null)
                return;

            string target = TargetFrameBufferName
                ?? context.CurrentRenderTarget?.Name
                ?? RenderGraphResourceNames.OutputRenderTarget;

            var builder = context.GetOrCreateSyntheticPass($"QuadRender_{FrameBufferName}_to_{target}");
            builder.WithStage(ERenderGraphPassStage.Graphics);
            builder.SampleTexture(MakeFboColorResource(FrameBufferName));

            ERenderPassLoadOp colorLoad = ERenderPassLoadOp.Load;
            ERenderPassStoreOp colorStore = ERenderPassStoreOp.Store;
            ERenderGraphAccess access = ERenderGraphAccess.ReadWrite;

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
