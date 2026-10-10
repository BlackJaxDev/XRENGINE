namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_PushMaterialOverride : ViewportStateRenderCommand<VPRC_PopMaterialOverride>
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
        {
            requirements.RequireOperation("material-override");
            requirements.RequireMaterial(Material);
        }

        public required XRMaterial Material { get; set; }

        protected override void Execute()
            => ActivePipelineInstance.RenderState.PushOverrideMaterialState(Material);
    }
}
