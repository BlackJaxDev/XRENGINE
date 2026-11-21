namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    [RenderCommandInfo(ERenderCommandCategory.State)]
    public class VPRC_PopMaterialOverride : ViewportPopStateRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("material-override");

        protected override void Execute()
            => ActivePipelineInstance.RenderState.PopOverrideMaterial();
    }
}
