
namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_ColorMask : ViewportRenderCommand
    {
        public bool Red { get; set; } = true;
        public bool Green { get; set; } = true;
        public bool Blue { get; set; } = true;
        public bool Alpha { get; set; } = true;

        protected override void Execute()
        {
            Engine.Rendering.State.ColorMask(Red, Green, Blue, Alpha);
        }

        public void Set(bool red, bool green, bool blue, bool alpha)
        {
            Red = red;
            Green = green;
            Blue = blue;
            Alpha = alpha;
        }
    }
}
