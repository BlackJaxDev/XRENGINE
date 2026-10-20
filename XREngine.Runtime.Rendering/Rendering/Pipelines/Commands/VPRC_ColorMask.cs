
namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_ColorMask : ViewportRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("raster-state");

        [System.ComponentModel.DefaultValue(true)]
        public bool Red { get; set; } = true;
        [System.ComponentModel.DefaultValue(true)]
        public bool Green { get; set; } = true;
        [System.ComponentModel.DefaultValue(true)]
        public bool Blue { get; set; } = true;
        [System.ComponentModel.DefaultValue(true)]
        public bool Alpha { get; set; } = true;

        protected override void Execute()
        {
            RuntimeEngine.Rendering.State.ColorMask(Red, Green, Blue, Alpha);
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
