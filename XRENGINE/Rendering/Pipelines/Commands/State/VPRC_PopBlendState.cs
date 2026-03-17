namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Pops blend state pushed by <see cref="VPRC_PushBlendState"/>.
    /// Disables alpha blending.
    /// </summary>
    public class VPRC_PopBlendState : ViewportPopStateRenderCommand
    {
        protected override void Execute()
        {
            Engine.Rendering.State.EnableBlend(false);
        }
    }
}
