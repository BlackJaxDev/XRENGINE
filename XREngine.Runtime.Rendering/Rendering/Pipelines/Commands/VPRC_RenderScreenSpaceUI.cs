using XREngine.Rendering.RenderGraph;
using System;
using System.Linq;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Render's the camera's screen space UI to the current viewport.
    /// </summary>
    [RenderPipelineScriptCommand]
    [RenderCommandInfo(ERenderCommandCategory.Scene, DoesGpuWork = true)]
    public class VPRC_RenderScreenSpaceUI : ViewportRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("screen-ui");

        /// <summary>
        /// The name of the FBO to render the UI to.
        /// If null, the UI will be rendered to the current viewport.
        /// </summary>
        [RenderResourceName(ERenderResourceNameKind.FrameBuffer)]
        public string? OutputTargetFBOName { get; set; } = null;

        /// <summary>
        /// If true, the command will not render anything if the FBO is not found.
        /// Note that this should be false if you want to render to the current viewport instead of an FBO.
        /// </summary>
        [System.ComponentModel.DefaultValue(false)]
        public bool FailRenderIfNoOutputFBO { get; set; } = false;

        //public override bool NeedsCollecVisible => true;

        //public override void CollectVisible()
        //{
        //    var ui = Pipeline.RenderState.UserInterface;
        //    var vp = Pipeline.RenderState.RenderingViewport;
        //    if (ui is null || vp is null)
        //        return;

        //    var fbo = Pipeline.GetFBO<XRQuadFrameBuffer>(UserInterfaceFBOName);
        //    if (fbo is not null)
        //        ui?.CollectRenderedItems(vp);
        //}

        //public override void SwapBuffers()
        //{
        //    var ui = Pipeline.RenderState.UserInterface;
        //    var vp = Pipeline.RenderState.RenderingViewport;
        //    if (ui is null || vp is null)
        //        return;

        //    var fbo = Pipeline.GetFBO<XRQuadFrameBuffer>(UserInterfaceFBOName);
        //    if (fbo is not null)
        //        ui?.SwapBuffers();
        //}

        protected override bool ShouldExecuteThisFrame()
        {
            XRRenderPipelineInstance? instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            if (instance is null)
                return false;

            var ui = instance.RenderState.ScreenSpaceUserInterface;
            if (ui is null || !ui.IsActive)
                return false;

            return OutputTargetFBOName is null ||
                !FailRenderIfNoOutputFBO ||
                instance.GetFBO<XRFrameBuffer>(OutputTargetFBOName) is not null;
        }

        protected override void Execute()
        {
            int passIndex = ResolvePassIndex(nameof(VPRC_RenderScreenSpaceUI));
            using var passScope = passIndex != int.MinValue
                ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : default;

            var ui = ActivePipelineInstance.RenderState.ScreenSpaceUserInterface;

            if (ui is null || !ui.IsActive)
                return;

            var fbo = OutputTargetFBOName is null
                ? ActivePipelineInstance.RenderState.OutputFBO
                : ActivePipelineInstance.GetFBO<XRFrameBuffer>(OutputTargetFBOName);
            if (FailRenderIfNoOutputFBO && fbo is null)
                return;

            using var clipScope = XREngine.Rendering.AbstractRenderer.Current?.PushUiClipSpacePolicy();
            XRTexture2D? previousBackdropBlur = XREngine.Rendering.UI.UIBatchCollector.ActiveBackdropBlurTexture;
            string? previousHostPipelineName = XREngine.Rendering.UI.UIBatchCollector.ActiveBackdropBlurHostPipelineName;
            XREngine.Rendering.UI.UIBatchCollector.ActiveBackdropBlurTexture = ActivePipelineInstance.RenderState.UiBackdropBlurTexture as XRTexture2D;
            XREngine.Rendering.UI.UIBatchCollector.ActiveBackdropBlurHostPipelineName = ActivePipelineInstance.Pipeline?.GetType().Name;
            try
            {
                ui.RenderScreenSpace(ActivePipelineInstance.RenderState.RenderingViewport, fbo);
                ActivePipelineInstance.RenderState.MarkScreenSpaceUiExecuted();
            }
            finally
            {
                XREngine.Rendering.UI.UIBatchCollector.ActiveBackdropBlurTexture = previousBackdropBlur;
                XREngine.Rendering.UI.UIBatchCollector.ActiveBackdropBlurHostPipelineName = previousHostPipelineName;
            }
        }

        private int ResolvePassIndex(string passName)
            => ResolveActiveRenderGraphPassIndex(passName);

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            string target = OutputTargetFBOName
                ?? context.CurrentRenderTarget?.Name
                ?? RenderGraphResourceNames.OutputRenderTarget;

            var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_RenderScreenSpaceUI), ERenderGraphPassStage.Graphics);
            if (context.ResourceLayout is not null && context.HasResource(UiBackdropBlurResources.HalfTextureName))
                builder.SampleTexture(MakeTextureResource(UiBackdropBlurResources.HalfTextureName));
            builder.UseColorAttachment(MakeColorTargetResource(target));
            if (!string.Equals(target, RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase))
            {
                builder.UseDepthAttachment(MakeFboDepthResource(target));
                builder.UseStencilAttachment(MakeFboStencilResource(target));
            }
        }
    }
}
