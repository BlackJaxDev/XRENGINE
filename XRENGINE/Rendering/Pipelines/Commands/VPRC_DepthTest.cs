namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_DepthTest : ViewportRenderCommand
    {
        public bool Enable { get; set; } = true;

        protected override void Execute()
        {
            Engine.Rendering.State.EnableDepthTest(Enable);
        }
    }
}
