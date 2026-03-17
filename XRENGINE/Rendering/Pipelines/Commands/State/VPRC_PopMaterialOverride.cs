namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_PopMaterialOverride : ViewportPopStateRenderCommand
    {
        protected override void Execute()
            => ActivePipelineInstance.RenderState.PopOverrideMaterial();
    }
}