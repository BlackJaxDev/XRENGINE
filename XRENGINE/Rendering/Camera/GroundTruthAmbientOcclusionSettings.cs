using System;

namespace XREngine.Rendering
{
    public sealed class GroundTruthAmbientOcclusionSettings : AmbientOcclusionModeSettings
    {
        private int _sliceCount = 3;
        private int _stepsPerSlice = 6;
        private bool _denoiseEnabled = true;
        private int _denoiseRadius = 4;
        private float _denoiseSharpness = 4.0f;
        private bool _useInputNormals = true;
        private float _falloffStartRatio = 0.4f;
        private float _thicknessHeuristic = 1.0f;
        private bool _multiBounceEnabled = true;
        private bool _specularOcclusionEnabled = true;

        public GroundTruthAmbientOcclusionSettings(AmbientOcclusionSettings owner)
            : base(owner, nameof(AmbientOcclusionSettings.GroundTruth))
        {
        }

        public int SliceCount
        {
            get => _sliceCount;
            set => SetValue(ref _sliceCount, value, nameof(AmbientOcclusionSettings.GTAOSliceCount));
        }

        public int StepsPerSlice
        {
            get => _stepsPerSlice;
            set => SetValue(ref _stepsPerSlice, value, nameof(AmbientOcclusionSettings.GTAOStepsPerSlice));
        }

        public bool DenoiseEnabled
        {
            get => _denoiseEnabled;
            set => SetValue(ref _denoiseEnabled, value, nameof(AmbientOcclusionSettings.GTAODenoiseEnabled));
        }

        public int DenoiseRadius
        {
            get => _denoiseRadius;
            set => SetValue(ref _denoiseRadius, value, nameof(AmbientOcclusionSettings.GTAODenoiseRadius));
        }

        public float DenoiseSharpness
        {
            get => _denoiseSharpness;
            set => SetValue(ref _denoiseSharpness, value, nameof(AmbientOcclusionSettings.GTAODenoiseSharpness));
        }

        public bool UseInputNormals
        {
            get => _useInputNormals;
            set => SetValue(ref _useInputNormals, value, nameof(AmbientOcclusionSettings.GTAOUseInputNormals));
        }

        public float FalloffStartRatio
        {
            get => _falloffStartRatio;
            set => SetValue(ref _falloffStartRatio, value, nameof(AmbientOcclusionSettings.GTAOFalloffStartRatio));
        }

        public float ThicknessHeuristic
        {
            get => _thicknessHeuristic;
            set => SetValue(ref _thicknessHeuristic, value, nameof(AmbientOcclusionSettings.GTAOThicknessHeuristic));
        }

        public bool MultiBounceEnabled
        {
            get => _multiBounceEnabled;
            set => SetValue(ref _multiBounceEnabled, value, nameof(AmbientOcclusionSettings.GTAOMultiBounceEnabled));
        }

        public bool SpecularOcclusionEnabled
        {
            get => _specularOcclusionEnabled;
            set => SetValue(ref _specularOcclusionEnabled, value, nameof(AmbientOcclusionSettings.GTAOSpecularOcclusionEnabled));
        }

        public override void ApplyUniforms(XRRenderProgram program)
        {
            program.Uniform("Radius", PositiveOr(Owner.Radius, 2.0f));
            program.Uniform("Bias", PositiveOr(Owner.Bias, 0.05f));
            program.Uniform("Power", PositiveOr(Owner.Power, 1.0f));
            program.Uniform("SliceCount", PositiveOr(SliceCount, 3));
            program.Uniform("StepsPerSlice", PositiveOr(StepsPerSlice, 6));
            program.Uniform("FalloffStartRatio", PositiveOr(FalloffStartRatio, 0.4f));
            program.Uniform("ThicknessHeuristic", Math.Clamp(ThicknessHeuristic, 0.0f, 1.0f));
            program.Uniform("DenoiseEnabled", DenoiseEnabled);
            program.Uniform("DenoiseRadius", Math.Clamp(DenoiseRadius, 0, 16));
            program.Uniform("DenoiseSharpness", PositiveOr(DenoiseSharpness, 4.0f));
            program.Uniform("UseInputNormals", UseInputNormals);
        }
    }
}