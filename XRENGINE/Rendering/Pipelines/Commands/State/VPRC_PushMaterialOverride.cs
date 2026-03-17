namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_PushMaterialOverride : ViewportStateRenderCommand<VPRC_PopMaterialOverride>
    {
        public required XRMaterial Material { get; set; }

        protected override void Execute()
            => ActivePipelineInstance.RenderState.PushOverrideMaterial(Material);
    }
}