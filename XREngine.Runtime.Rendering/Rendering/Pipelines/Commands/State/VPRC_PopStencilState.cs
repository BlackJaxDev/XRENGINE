namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Pops stencil state pushed by <see cref="VPRC_PushStencilState"/>.
    /// Disables stencil testing and resets to default state.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_PopStencilState : ViewportPopStateRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("stencil");

        protected override void Execute()
        {
            RuntimeEngine.Rendering.State.EnableStencilTest(false);
            RuntimeEngine.Rendering.State.StencilMask(0xFF);
            RuntimeEngine.Rendering.State.StencilFunc(
                Rendering.Models.Materials.EComparison.Always, 0, 0xFF);
            RuntimeEngine.Rendering.State.StencilOp(
                Rendering.Models.Materials.EStencilOp.Keep,
                Rendering.Models.Materials.EStencilOp.Keep,
                Rendering.Models.Materials.EStencilOp.Keep);
        }
    }
}
