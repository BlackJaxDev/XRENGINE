namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_BindBuffer : ViewportStateRenderCommand<VPRC_PopBufferBinding>
    {
        public string BufferName { get; set; } = string.Empty;
        public uint BindingLocation { get; set; }

        protected override void Execute()
        {
            ActivePipelineInstance.RenderState.PushBufferBindingState(
                new XRRenderPipelineInstance.RenderingState.ScopedBufferBinding(
                    BufferName,
                    BindingLocation));
        }
    }
}
