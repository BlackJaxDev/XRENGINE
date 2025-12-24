namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_Clear : ViewportRenderCommand
    {
        public bool Color { get; set; }
        public bool Depth { get; set; }
        public bool Stencil { get; set; }

        public void Set(bool color, bool depth, bool stencil)
        {
            Color = color;
            Depth = depth;
            Stencil = stencil;
        }

        protected override void Execute()
        {
            Engine.Rendering.State.Clear(Color, Depth, Stencil);
        }
    }
}
