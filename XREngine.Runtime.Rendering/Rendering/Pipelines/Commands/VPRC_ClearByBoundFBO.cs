namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_ClearByBoundFBO : ViewportRenderCommand
    {
        protected override void Execute()
        {
            RuntimeEngine.Rendering.State.ClearByBoundFBO();
        }
    }
}
