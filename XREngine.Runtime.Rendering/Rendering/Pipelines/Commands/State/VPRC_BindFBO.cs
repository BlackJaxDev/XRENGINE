namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_BindFBO : ViewportStateRenderCommand<VPRC_UnbindFBO>
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
        {
            requirements.RequireOperation("framebuffer");
            if (FrameBuffer is XRMaterialFrameBuffer { Material: { } material }) requirements.RequireMaterial(material);
        }

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
