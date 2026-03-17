namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Inserts a named synchronization point into the authored pipeline and publishes its completion into the variable store.
    /// The current implementation is a named memory barrier marker over existing renderer synchronization primitives.
    /// </summary>
    public sealed class VPRC_Fence : ViewportRenderCommand
    {
        private EMemoryBarrierMask _mask =
            EMemoryBarrierMask.ShaderStorage |
            EMemoryBarrierMask.ShaderImageAccess |
            EMemoryBarrierMask.Command |
            EMemoryBarrierMask.BufferUpdate |
            EMemoryBarrierMask.TextureUpdate;

        public string? FenceName { get; set; }
        public string? CompletedVariableName { get; set; }

        public EMemoryBarrierMask Mask
        {
            get => _mask;
            set => SetField(ref _mask, value);
        }

        protected override void Execute()
        {
            AbstractRenderer.Current?.MemoryBarrier(Mask);

            string? variableName = CompletedVariableName;
            if (string.IsNullOrWhiteSpace(variableName) && !string.IsNullOrWhiteSpace(FenceName))
                variableName = $"Fence.{FenceName}.Completed";

            if (!string.IsNullOrWhiteSpace(variableName))
                ActivePipelineInstance.Variables.Set(variableName!, true);
        }
    }
}