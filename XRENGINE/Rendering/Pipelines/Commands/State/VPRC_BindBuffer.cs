namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_BindBuffer : ViewportStateRenderCommand<VPRC_PopBufferBinding>
    {
        public required string BufferName { get; set; }
        public uint BindingLocation { get; set; }

        protected override void Execute()
        {
            ActivePipelineInstance.RenderState.PushBufferBinding(new XRRenderPipelineInstance.RenderingState.ScopedBufferBinding
            {
                BufferName = BufferName,
                BindingLocation = BindingLocation
            });
        }
    }
}