namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Render's the camera's screen space UI to the current viewport.
    /// </summary>
    public class VPRC_RenderScreenSpaceUI : ViewportRenderCommand
    {
        /// <summary>
        /// The name of the FBO to render the UI to.
        /// If null, the UI will be rendered to the current viewport.
        /// </summary>
        public string? OutputTargetFBOName { get; set; } = null;

        /// <summary>
        /// If true, the command will not render anything if the FBO is not found.
        /// Note that this should be false if you want to render to the current viewport instead of an FBO.
        /// </summary>
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

        protected override void Execute()
        {
            var ui = ActivePipelineInstance.RenderState.ScreenSpaceUserInterface;
            if (ui is null || !ui.IsActive)
                return;

            var fbo = OutputTargetFBOName is null ? null : ActivePipelineInstance.GetFBO<XRFrameBuffer>(OutputTargetFBOName);
            if (FailRenderIfNoOutputFBO && fbo is null)
                return;

            ui.RenderScreenSpace(ActivePipelineInstance.RenderState.RenderingViewport, fbo);
        }
    }
}