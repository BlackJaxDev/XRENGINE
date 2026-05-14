namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_DepthTest : ViewportRenderCommand
    {
        public bool Enable { get; set; } = true;

        protected override void Execute()
        {
            RuntimeEngine.Rendering.State.EnableDepthTest(Enable);
        }
    }
}
