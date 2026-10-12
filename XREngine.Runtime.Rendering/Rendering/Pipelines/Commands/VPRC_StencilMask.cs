namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_StencilMask : ViewportRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("stencil");

        public uint Mask { get; set; }

        public void Set(uint mask)
            => Mask = mask;

        protected override void Execute()
            => RuntimeEngine.Rendering.State.StencilMask(Mask);
    }
}
