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
        public string? UserInterfaceFBOName { get; set; }

        /// <summary>
        /// If true, the command will not render anything if the FBO is not found.
        /// </summary>
        public bool FailRenderIfNoFBO { get; set; } = false;

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
            var ui = Pipeline.RenderState.UserInterface;
            if (ui is null || !ui.IsActive)
                return;
            
            var fbo = UserInterfaceFBOName is null ? null : Pipeline.GetFBO<XRFrameBuffer>(UserInterfaceFBOName);
            if (FailRenderIfNoFBO && fbo is null)
                return;

            ui.RenderScreenSpace(Pipeline.RenderState.RenderingViewport, fbo);
        }
    }
}