namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_MemoryBarrier : ViewportRenderCommand
    {
        private EMemoryBarrierMask _mask = EMemoryBarrierMask.All;
        public EMemoryBarrierMask Mask
        {
            get => _mask;
            set => SetField(ref _mask, value);
        }

        protected override void Execute()
            => AbstractRenderer.Current?.MemoryBarrier(Mask);
    }
}
