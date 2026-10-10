namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_PopShaderGlobals : ViewportPopStateRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("program-bindings");

        protected override void Execute()
            => ActivePipelineInstance.RenderState.PopShaderGlobals();
    }
}
