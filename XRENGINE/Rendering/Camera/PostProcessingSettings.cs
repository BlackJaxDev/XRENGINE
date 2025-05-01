namespace XREngine.Rendering
{
    public class PostProcessingSettings
    {
        public PostProcessingSettings()
        {
            Bloom = new BloomSettings();
            DepthOfField = new DepthOfFieldSettings();
            AmbientOcclusion = new AmbientOcclusionSettings();
            MotionBlur = new MotionBlurSettings();
            ColorGrading = new ColorGradingSettings();
            Vignette = new VignetteSettings();
            LensDistortion = new LensDistortionSettings();
            ChromaticAberration = new ChromaticAberrationSettings();
            Grain = new GrainSettings();
            Dithering = new DitheringSettings();
            RayTracing = new RayTracingSettings();
            Shadows = new ShadowSettings();
        }

        public ShadowSettings Shadows { get; set; }
        public BloomSettings Bloom { get; set; }
        public DepthOfFieldSettings DepthOfField { get; set; }
        public AmbientOcclusionSettings AmbientOcclusion { get; set; }
        public MotionBlurSettings MotionBlur { get; set; }
        public ColorGradingSettings ColorGrading { get; set; }
        public VignetteSettings Vignette { get; set; }
        public LensDistortionSettings LensDistortion { get; set; }
        public ChromaticAberrationSettings ChromaticAberration { get; set; }
        public GrainSettings Grain { get; set; }
        public DitheringSettings Dithering { get; set; }
        public RayTracingSettings RayTracing { get; set; }

        public void SetUniforms(XRRenderProgram program)
        {
            Vignette.SetUniforms(program);
            ColorGrading.SetUniforms(program);
        }
    }
}