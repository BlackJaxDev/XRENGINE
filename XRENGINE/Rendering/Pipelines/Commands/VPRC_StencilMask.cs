namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_StencilMask : ViewportRenderCommand
    {
        public uint Mask { get; set; }

        public void Set(uint mask)
            => Mask = mask;

        protected override void Execute()
            => Engine.Rendering.State.StencilMask(Mask);
    }
}
