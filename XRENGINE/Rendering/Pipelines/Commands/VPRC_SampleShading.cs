namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Enables or disables per-sample shading (GL_SAMPLE_SHADING).
    /// When enabled with MinValue=1.0, the fragment shader executes once per MSAA sample,
    /// which is required for per-sample deferred lighting of complex pixels.
    /// </summary>
    public class VPRC_SampleShading : ViewportRenderCommand
    {
        public bool Enable { get; set; }
        public float MinValue { get; set; } = 1.0f;

        protected override void Execute()
        {
            if (Enable)
                Engine.Rendering.State.EnableSampleShading(MinValue);
            else
                Engine.Rendering.State.DisableSampleShading();
        }
    }
}
