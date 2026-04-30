namespace XREngine.Rendering
{
    public sealed class ScreenSpaceAmbientOcclusionSettings : AmbientOcclusionModeSettings
    {
        private float _resolutionScale = 1.0f;
        private float _distance = 1.0f;
        private float _color = 0.0f;
        private int _iterations = 1;
        private float _rings = 4.0f;
        private float _lumaPhi = 4.0f;

        public ScreenSpaceAmbientOcclusionSettings(AmbientOcclusionSettings owner)
            : base(owner, nameof(AmbientOcclusionSettings.ScreenSpace))
        {
        }

        public float ResolutionScale
        {
            get => _resolutionScale;
            set => SetValue(ref _resolutionScale, value, nameof(AmbientOcclusionSettings.ResolutionScale));
        }

        public float Distance
        {
            get => _distance;
            set => SetValue(ref _distance, value, nameof(AmbientOcclusionSettings.Distance));
        }

        public float Color
        {
            get => _color;
            set => SetValue(ref _color, value, nameof(AmbientOcclusionSettings.Color));
        }

        public int Iterations
        {
            get => _iterations;
            set => SetValue(ref _iterations, value, nameof(AmbientOcclusionSettings.Iterations));
        }

        public float Rings
        {
            get => _rings;
            set => SetValue(ref _rings, value, nameof(AmbientOcclusionSettings.Rings));
        }

        public float LumaPhi
        {
            get => _lumaPhi;
            set => SetValue(ref _lumaPhi, value, nameof(AmbientOcclusionSettings.LumaPhi));
        }

        public override void ApplyUniforms(XRRenderProgram program)
        {
            program.Uniform("Radius", Owner.Radius);
            program.Uniform("Power", Owner.Power);
        }
    }
}