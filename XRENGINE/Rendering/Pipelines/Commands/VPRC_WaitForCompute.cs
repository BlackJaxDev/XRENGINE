namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Convenience barrier for authored pipelines that need compute-written resources visible to subsequent graphics work.
    /// </summary>
    public sealed class VPRC_WaitForCompute : ViewportRenderCommand
    {
        private EMemoryBarrierMask _mask =
            EMemoryBarrierMask.ShaderStorage |
            EMemoryBarrierMask.ShaderImageAccess |
            EMemoryBarrierMask.TextureFetch |
            EMemoryBarrierMask.VertexAttribArray |
            EMemoryBarrierMask.ElementArray |
            EMemoryBarrierMask.Uniform |
            EMemoryBarrierMask.Command;

        public EMemoryBarrierMask Mask
        {
            get => _mask;
            set => SetField(ref _mask, value);
        }

        protected override void Execute()
            => AbstractRenderer.Current?.MemoryBarrier(Mask);
    }
}