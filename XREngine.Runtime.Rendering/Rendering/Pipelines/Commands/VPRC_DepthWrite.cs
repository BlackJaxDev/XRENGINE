namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_DepthWrite : ViewportRenderCommand
    {
        public bool Allow { get; set; } = true;

        protected override void Execute()
        {
            Engine.Rendering.State.AllowDepthWrite(Allow);
        }
    }
}
