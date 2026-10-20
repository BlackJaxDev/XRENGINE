namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_PopMaterialOverride : ViewportPopStateRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("material-override");

        protected override void Execute()
            => ActivePipelineInstance.RenderState.PopOverrideMaterial();
    }
}
