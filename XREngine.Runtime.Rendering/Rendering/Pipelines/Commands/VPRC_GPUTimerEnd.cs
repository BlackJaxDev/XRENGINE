namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Ends the most recent user-authored GPU timing scope started by <see cref="VPRC_GPUTimerBegin"/>.
    /// </summary>
    [RenderPipelineScriptCommand]
    public sealed class VPRC_GPUTimerEnd : ViewportRenderCommand
    {
        public string? Label { get; set; }

        public override string GpuProfilingName
            => string.IsNullOrWhiteSpace(Label) ? nameof(VPRC_GPUTimerEnd) : $"{nameof(VPRC_GPUTimerEnd)}:{Label}";

        protected override void Execute()
            => RenderPipelineGpuProfiler.Instance.PopUserScope(Label);
    }
}
