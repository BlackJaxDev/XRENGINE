namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    [RenderCommandInfo(ERenderCommandCategory.Targets, DoesGpuWork = true)]
    public class VPRC_ClearByBoundFBO : ViewportRenderCommand
    {
        protected override void Execute()
        {
            RuntimeEngine.Rendering.State.ClearByBoundFBO();
        }
    }
}
