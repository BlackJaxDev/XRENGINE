namespace XREngine.Rendering
{
    public sealed class PrototypeAmbientOcclusionSettings : AmbientOcclusionModeSettings
    {
        private float _intensity = 1.0f;

        public PrototypeAmbientOcclusionSettings(AmbientOcclusionSettings owner)
            : base(owner, nameof(AmbientOcclusionSettings.Prototype))
        {
        }

        public float Intensity
        {
            get => _intensity;
            set => SetValue(ref _intensity, value, nameof(AmbientOcclusionSettings.Intensity));
        }

        public override void ApplyUniforms(XRRenderProgram program)
        {
            program.Uniform("Bias", Owner.Bias);
            program.Uniform("Intensity", Intensity);
        }
    }
}