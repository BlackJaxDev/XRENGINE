namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_PopProgramBindings : ViewportPopStateRenderCommand
    {
        protected override void Execute()
            => ActivePipelineInstance.RenderState.PopProgramBindings();
    }
}
