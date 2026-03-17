namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Begins a user-authored GPU timing scope that groups subsequent commands in the render-pipeline GPU profiler.
    /// </summary>
    public sealed class VPRC_GPUTimerBegin : ViewportRenderCommand
    {
        public string Label { get; set; } = "Timer";

        public override string GpuProfilingName
            => string.IsNullOrWhiteSpace(Label) ? nameof(VPRC_GPUTimerBegin) : $"{nameof(VPRC_GPUTimerBegin)}:{Label}";

        protected override void Execute()
        {
            if (string.IsNullOrWhiteSpace(Label))
                return;

            RenderPipelineGpuProfiler.Instance.PushUserScope(Label);
        }
    }
}