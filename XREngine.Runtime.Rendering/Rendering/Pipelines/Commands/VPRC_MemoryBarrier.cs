namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_MemoryBarrier : ViewportRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("memory-barriers");

        private EMemoryBarrierMask _mask = EMemoryBarrierMask.All;
        [System.ComponentModel.DefaultValue(EMemoryBarrierMask.All)]
        public EMemoryBarrierMask Mask
        {
            get => _mask;
            set => SetField(ref _mask, value);
        }

        protected override void Execute()
            => AbstractRenderer.Current?.MemoryBarrier(Mask);
    }
}
