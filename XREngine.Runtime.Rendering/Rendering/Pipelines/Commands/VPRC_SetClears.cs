using XREngine.Data.Colors;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_SetClears : ViewportRenderCommand
    {
        public ColorF4? DefaultColor { get; set; }
        public float? DefaultDepth { get; set; }
        public int? DefaultStencil { get; set; }

        public void Set(ColorF4? color, float? depth, int? stencil)
        {
            DefaultColor = color;
            DefaultDepth = depth;
            DefaultStencil = stencil;
        }

        protected override void Execute()
        {
            if (DefaultColor is not null)
                RuntimeEngine.Rendering.State.ClearColor(DefaultColor.Value);

            if (DefaultDepth is not null)
                RuntimeEngine.Rendering.State.ClearDepth(DefaultDepth.Value);

            if (DefaultStencil is not null)
                RuntimeEngine.Rendering.State.ClearStencil(DefaultStencil.Value);
        }
    }
}
