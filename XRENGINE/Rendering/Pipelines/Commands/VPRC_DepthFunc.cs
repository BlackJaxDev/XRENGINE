using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_DepthFunc : ViewportRenderCommand
    {
        public EComparison Comp { get; set; } = EComparison.Lequal;

        protected override void Execute()
        {
            Engine.Rendering.State.DepthFunc(Comp);
        }
    }
}
