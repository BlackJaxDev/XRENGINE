namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_ClearByBoundFBO : ViewportRenderCommand
    {
        protected override void Execute()
        {
            Engine.Rendering.State.ClearByBoundFBO();
        }
    }
}
