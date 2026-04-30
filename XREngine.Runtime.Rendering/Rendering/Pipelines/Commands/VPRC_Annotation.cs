namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Inserts a named GPU profiling scope into the command stream.
    /// Useful for marking logical phases inside user-authored pipelines.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_Annotation : ViewportRenderCommand
    {
        public string Label { get; set; } = "Annotation";

        public override string GpuProfilingName
            => string.IsNullOrWhiteSpace(Label) ? base.GpuProfilingName : Label;

        protected override void Execute()
        {
        }
    }
}
