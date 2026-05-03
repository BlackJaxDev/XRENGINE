using System;
using XREngine.Rendering.RenderGraph;
using System.Linq;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Renders an FBO quad to another FBO.
    /// Useful for transforming every pixel of previous FBO.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="destination"></param>
    [RenderPipelineScriptCommand]
    public class VPRC_RenderQuadToFBO : ViewportRenderCommand
    {
        private static readonly bool _diagEnabled =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XRE_DIAG_QUAD_BLIT"));

        public string? SourceQuadFBOName { get; set; }
        public string? DestinationFBOName { get; set; } = null;
        public bool MatchDestinationRenderArea { get; set; }

        public void SetTargets(string sourceQuadFBOName, string? destinationFBOName = null, bool matchDestinationRenderArea = false)
        {
            SourceQuadFBOName = sourceQuadFBOName;
            DestinationFBOName = destinationFBOName;
            MatchDestinationRenderArea = matchDestinationRenderArea;
        }

        protected override void Execute()
        {
            if (SourceQuadFBOName is null)
                return;

            var activeInstance = Engine.Rendering.State.CurrentRenderingPipeline;
            if (activeInstance is null)
            {
                Debug.RenderingWarningEvery(
                    $"QuadBlit.MissingPipeline.{SourceQuadFBOName}.{DestinationFBOName}",
                    TimeSpan.FromSeconds(5),
                    "[QuadBlitDiag] Skipping quad blit from '{0}' to '{1}': no active render pipeline instance.",
                    SourceQuadFBOName,
                    DestinationFBOName ?? "<current>");
                return;
            }

            string destination = DestinationFBOName
                ?? activeInstance.RenderState.OutputFBO?.Name
                ?? RenderGraphResourceNames.OutputRenderTarget;

            int passIndex = ResolvePassIndex($"QuadBlit_{SourceQuadFBOName}_to_{destination}");
            using var passScope = passIndex != int.MinValue
                ? Engine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : default;

            XRQuadFrameBuffer? sourceFBO = activeInstance.GetFBO<XRQuadFrameBuffer>(SourceQuadFBOName);
            if (sourceFBO is null)
            {
                if (_diagEnabled)
                    Debug.RenderingWarning($"[QuadBlitDiag] Source FBO '{SourceQuadFBOName}' not found as XRQuadFrameBuffer.");
                return;
            }

            var destFBO = DestinationFBOName is null ? null : activeInstance.GetFBO<XRFrameBuffer>(DestinationFBOName);
            if (_diagEnabled && DestinationFBOName is not null && destFBO is null)
                Debug.RenderingWarning($"[QuadBlitDiag] Dest FBO '{DestinationFBOName}' not found.");

            if ((string.Equals(SourceQuadFBOName, DefaultRenderPipeline.PostProcessFBOName, StringComparison.Ordinal)
                    && string.Equals(DestinationFBOName, "PostProcessOutputFBO", StringComparison.Ordinal))
                || (string.Equals(SourceQuadFBOName, DefaultRenderPipeline.FxaaFBOName, StringComparison.Ordinal)
                    && string.Equals(DestinationFBOName, DefaultRenderPipeline.FxaaFBOName, StringComparison.Ordinal))
                || (string.Equals(SourceQuadFBOName, DefaultRenderPipeline.TsrUpscaleFBOName, StringComparison.Ordinal)
                    && string.Equals(DestinationFBOName, DefaultRenderPipeline.TsrUpscaleFBOName, StringComparison.Ordinal))
                || string.Equals(SourceQuadFBOName, DefaultRenderPipeline.MsaaLightCombineFBOName, StringComparison.Ordinal))
            {
                /*
                Debug.RenderingEvery(
                    $"QuadBlit.{ActivePipelineInstance.GetHashCode()}.{SourceQuadFBOName}.{DestinationFBOName}",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] QuadBlit Source={0} Dest={1} SourceTargets={2} DestTargets={3} DestType={4} CurrentOutput={5}",
                    SourceQuadFBOName,
                    DestinationFBOName ?? "<current>",
                    sourceFBO.Targets?.Length ?? 0,
                    destFBO?.Targets?.Length ?? 0,
                    destFBO?.GetType().Name ?? "<null>",
                    ActivePipelineInstance.RenderState.OutputFBO?.Name ?? "<backbuffer>");
                */
            }

            if (_diagEnabled)
            {
                bool hasTargets = destFBO?.Targets is { Length: > 0 };
                Debug.Log(ELogCategory.Rendering, $"[QuadBlitDiag] Rendering '{SourceQuadFBOName}' → '{DestinationFBOName ?? "<current>"}' (dest has targets: {hasTargets}, dest type: {destFBO?.GetType().Name ?? "null"})");
            }

            using var renderAreaScope = MatchDestinationRenderArea && destFBO is { Width: > 0, Height: > 0 }
                ? activeInstance.RenderState.PushRenderArea((int)destFBO.Width, (int)destFBO.Height)
                : default;

            sourceFBO.Render(destFBO);
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

            if (SourceQuadFBOName is null)
                return;

            string destination = DestinationFBOName
                ?? context.CurrentRenderTarget?.Name
                ?? RenderGraphResourceNames.OutputRenderTarget;

            var builder = context.GetOrCreateSyntheticPass($"QuadBlit_{SourceQuadFBOName}_to_{destination}");
            builder.WithStage(ERenderGraphPassStage.Graphics);
            builder.SampleTexture(MakeFboColorResource(SourceQuadFBOName));

            ERenderPassLoadOp colorLoad = ERenderPassLoadOp.Load;
            ERenderPassStoreOp colorStore = ERenderPassStoreOp.Store;
            ERenderGraphAccess access = ERenderGraphAccess.ReadWrite;

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
