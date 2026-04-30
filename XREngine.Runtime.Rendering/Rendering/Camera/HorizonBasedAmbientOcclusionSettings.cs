namespace XREngine.Rendering
{
    public sealed class HorizonBasedAmbientOcclusionSettings : AmbientOcclusionModeSettings
    {
        private int _directionCount = 8;
        private int _stepsPerDirection = 4;
        private float _tangentBias = 0.1f;

        public HorizonBasedAmbientOcclusionSettings(AmbientOcclusionSettings owner)
            : base(owner, nameof(AmbientOcclusionSettings.HorizonBased))
        {
        }

        public int DirectionCount
        {
            get => _directionCount;
            set => SetValue(ref _directionCount, value, nameof(AmbientOcclusionSettings.HBAODirectionCount));
        }

        public int StepsPerDirection
        {
            get => _stepsPerDirection;
            set => SetValue(ref _stepsPerDirection, value, nameof(AmbientOcclusionSettings.HBAOStepsPerDirection));
        }

        public float TangentBias
        {
            get => _tangentBias;
            set => SetValue(ref _tangentBias, value, nameof(AmbientOcclusionSettings.HBAOTangentBias));
        }

        public override void ApplyUniforms(XRRenderProgram program)
        {
            program.Uniform("Radius", PositiveOr(Owner.Radius, 0.9f));
            program.Uniform("Bias", PositiveOr(Owner.Bias, 0.03f));
            program.Uniform("Power", PositiveOr(Owner.Power, 1.4f));
            program.Uniform("DirectionCount", PositiveOr(DirectionCount, 8));
            program.Uniform("StepsPerDirection", PositiveOr(StepsPerDirection, 4));
            program.Uniform("TangentBias", TangentBias >= 0.0f ? TangentBias : 0.1f);
        }
    }
}