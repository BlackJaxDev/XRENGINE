namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Pops blend state pushed by <see cref="VPRC_PushBlendState"/>.
    /// Disables alpha blending.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_PopBlendState : ViewportPopStateRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("raster-state");

        protected override void Execute()
        {
            RuntimeEngine.Rendering.State.EnableBlend(false);
        }
    }
}
