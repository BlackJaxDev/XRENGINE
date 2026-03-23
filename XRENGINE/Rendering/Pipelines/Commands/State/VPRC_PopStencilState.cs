namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Pops stencil state pushed by <see cref="VPRC_PushStencilState"/>.
    /// Disables stencil testing and resets to default state.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_PopStencilState : ViewportPopStateRenderCommand
    {
        protected override void Execute()
        {
            Engine.Rendering.State.EnableStencilTest(false);
            Engine.Rendering.State.StencilMask(0xFF);
            Engine.Rendering.State.StencilFunc(
                Rendering.Models.Materials.EComparison.Always, 0, 0xFF);
            Engine.Rendering.State.StencilOp(
                Rendering.Models.Materials.EStencilOp.Keep,
                Rendering.Models.Materials.EStencilOp.Keep,
                Rendering.Models.Materials.EStencilOp.Keep);
        }
    }
}
