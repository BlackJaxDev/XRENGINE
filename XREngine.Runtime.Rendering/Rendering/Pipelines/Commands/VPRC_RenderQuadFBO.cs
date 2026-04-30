using System;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_RenderQuadFBO : ViewportRenderCommand
    {
        public string? FrameBufferName { get; set; }
        public string? TargetFrameBufferName { get; set; }
        public bool RenderToSourceFrameBuffer { get; set; }

        public VPRC_RenderQuadFBO SetOptions(
            string frameBufferName,
            string? targetFrameBufferName = null,
            bool renderToSourceFrameBuffer = false)
        {
            FrameBufferName = frameBufferName;
            TargetFrameBufferName = targetFrameBufferName;
            RenderToSourceFrameBuffer = renderToSourceFrameBuffer;
            return this;
        }

        protected override void Execute()
        {
            if (FrameBufferName is null)
                return;

            string target = TargetFrameBufferName
                ?? (RenderToSourceFrameBuffer ? FrameBufferName : null)
                ?? ActivePipelineInstance.RenderState.OutputFBO?.Name
                ?? RenderGraphResourceNames.OutputRenderTarget;

            int passIndex = ResolvePassIndex($"QuadRender_{FrameBufferName}_to_{target}");
            using var passScope = passIndex != int.MinValue
                ? Engine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : default;

            var inputFBO = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(FrameBufferName);
            if (inputFBO is null)
                return;

            XRFrameBuffer? targetFBO = TargetFrameBufferName is not null
                ? ActivePipelineInstance.GetFBO<XRFrameBuffer>(TargetFrameBufferName)
                : RenderToSourceFrameBuffer ? inputFBO : null;

            inputFBO.Render(targetFBO);
        }

        private int ResolvePassIndex(string passName)
        {
            var metadata = ParentPipeline?.PassMetadata;
            if (metadata is null)
                return int.MinValue;

            foreach (var match in metadata)
            {
                if (string.Equals(match.Name, passName, StringComparison.OrdinalIgnoreCase))
                    return match.PassIndex;
            }

            return int.MinValue;
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (FrameBufferName is null)
                return;

            string target = TargetFrameBufferName
                ?? (RenderToSourceFrameBuffer ? FrameBufferName : null)
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
