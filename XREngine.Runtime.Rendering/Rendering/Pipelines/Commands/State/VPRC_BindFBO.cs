namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_BindFBO : ViewportStateRenderCommand<VPRC_UnbindFBO>
    {
        public required XRFrameBuffer FrameBuffer { get; set; }

        protected override void Execute()
        {
            FrameBuffer.BindForWriting();
            PopCommand.FrameBuffer = FrameBuffer;
            PopCommand.Write = true;
            PopCommand.RenderTargetScope = ActivePipelineInstance.RenderState.PushRenderTargetBinding(
                FrameBuffer.Name ?? $"FBO[{FrameBuffer.GetHashCode()}]",
                FrameBuffer,
                write: true);
        }
    }
}
