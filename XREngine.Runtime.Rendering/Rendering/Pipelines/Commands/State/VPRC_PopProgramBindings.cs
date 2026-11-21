namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    [RenderCommandInfo(ERenderCommandCategory.State)]
    public class VPRC_PopProgramBindings : ViewportPopStateRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("program-bindings");

        protected override void Execute()
            => ActivePipelineInstance.RenderState.PopProgramBindings();
    }
}
