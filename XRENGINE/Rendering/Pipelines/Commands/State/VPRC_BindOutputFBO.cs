namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Binds the FBO that is set as the output FBO in the pipeline.
    /// This FBO may be null if the pipeline is not rendering to a framebuffer.
    /// </summary>
    /// <param name="pipeline"></param>
    public class VPRC_BindOutputFBO : ViewportStateRenderCommand<VPRC_UnbindFBO>
    {
        public bool Write { get; set; } = true;
        public bool ClearColor { get; set; } = true;
        public bool ClearDepth { get; set; } = true;
        public bool ClearStencil { get; set; } = true;

        public void SetOptions(bool write = true, bool clearColor = true, bool clearDepth = true, bool clearStencil = true)
        {
            Write = write;
            ClearColor = clearColor;
            ClearDepth = clearDepth;
            ClearStencil = clearStencil;
        }

        protected override void Execute()
        {
            var fbo = ActivePipelineInstance.RenderState.OutputFBO;
            if (fbo is null)
                return;
            
            if (Write)
                fbo.BindForWriting();
            else
                fbo.BindForReading();

            PopCommand.FrameBuffer = fbo;
            PopCommand.Write = Write;

            if (ClearColor || ClearDepth || ClearStencil)
                Engine.Rendering.State.ClearByBoundFBO(ClearColor, ClearDepth, ClearStencil);
        }
    }
}
