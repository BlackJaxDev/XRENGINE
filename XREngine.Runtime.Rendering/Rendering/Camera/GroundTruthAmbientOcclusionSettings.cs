using System;

namespace XREngine.Rendering
{
    public sealed class GroundTruthAmbientOcclusionSettings(AmbientOcclusionSettings owner) : AmbientOcclusionModeSettings(owner, nameof(AmbientOcclusionSettings.GroundTruth))
    {
        /// <summary>
        /// Controls the resolution at which the GTAO generation pass runs.
        /// Lower resolutions are significantly faster but lose fine detail.
        /// </summary>
        public enum EResolution
        {
            Full = 1,
            Half = 2,
            Quarter = 4,
        }

        public const int DefaultSliceCount = 5;
        public const int DefaultStepsPerSlice = 10;
        public const bool DefaultDenoiseEnabled = true;
        public const int DefaultDenoiseRadius = 8;
        public const float DefaultDenoiseSharpness = 14.02f;
        public const bool DefaultUseInputNormals = true;
        public const float DefaultFalloffStartRatio = 0.4f;
        public const float DefaultThicknessHeuristic = 1.0f;
        public const bool DefaultMultiBounceEnabled = true;
        public const bool DefaultSpecularOcclusionEnabled = true;
        public const EResolution DefaultResolution = EResolution.Half;
        public const bool DefaultUseNormalWeightedBlur = true;
        public const bool DefaultUseVisibilityBitmask = true;
        public const float DefaultVisibilityBitmaskThickness = 1.5002f;

        private int _sliceCount = DefaultSliceCount;
        private int _stepsPerSlice = DefaultStepsPerSlice;
        private bool _denoiseEnabled = DefaultDenoiseEnabled;
        private int _denoiseRadius = DefaultDenoiseRadius;
        private float _denoiseSharpness = DefaultDenoiseSharpness;
        private bool _useInputNormals = DefaultUseInputNormals;
        private float _falloffStartRatio = DefaultFalloffStartRatio;
        private float _thicknessHeuristic = DefaultThicknessHeuristic;
        private bool _multiBounceEnabled = DefaultMultiBounceEnabled;
        private bool _specularOcclusionEnabled = DefaultSpecularOcclusionEnabled;
        private EResolution _resolution = DefaultResolution;
        private bool _useNormalWeightedBlur = DefaultUseNormalWeightedBlur;
        private bool _useVisibilityBitmask = DefaultUseVisibilityBitmask;
        private float _visibilityBitmaskThickness = DefaultVisibilityBitmaskThickness;

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

        /// <summary>
        /// Resolution at which the AO generation pass runs. Half is recommended for most scenes.
        /// </summary>
        public EResolution Resolution
        {
            get => _resolution;
            set => SetValue(ref _resolution, value, nameof(AmbientOcclusionSettings.GTAOResolution));
        }

        /// <summary>
        /// When true the bilateral blur reads and weights by G-buffer normals (higher quality, more bandwidth).
        /// When false only depth-weighted blur is used (faster, slightly softer edges).
        /// </summary>
        public bool UseNormalWeightedBlur
        {
            get => _useNormalWeightedBlur;
            set => SetValue(ref _useNormalWeightedBlur, value, nameof(AmbientOcclusionSettings.GTAOUseNormalWeightedBlur));
        }

        /// <summary>
        /// Switches the GTAO gather from horizon-angle integration to the visibility-bitmask variant.
        /// </summary>
        public bool UseVisibilityBitmask
        {
            get => _useVisibilityBitmask;
            set => SetValue(ref _useVisibilityBitmask, value, nameof(AmbientOcclusionSettings.GTAOUseVisibilityBitmask));
        }

        /// <summary>
        /// View-space thickness used by the visibility-bitmask gather to approximate back-face coverage.
        /// </summary>
        public float VisibilityBitmaskThickness
        {
            get => _visibilityBitmaskThickness;
            set => SetValue(ref _visibilityBitmaskThickness, value, nameof(AmbientOcclusionSettings.GTAOVisibilityBitmaskThickness));
        }

        public override void ApplyUniforms(XRRenderProgram program)
        {
            program.Uniform("Radius", PositiveOr(Owner.Radius, AmbientOcclusionSettings.DefaultRadius));
            program.Uniform("Bias", PositiveOr(Owner.Bias, AmbientOcclusionSettings.DefaultBias));
            program.Uniform("Power", PositiveOr(Owner.Power, AmbientOcclusionSettings.DefaultPower));
            program.Uniform("SliceCount", PositiveOr(SliceCount, DefaultSliceCount));
            program.Uniform("StepsPerSlice", PositiveOr(StepsPerSlice, DefaultStepsPerSlice));
            program.Uniform("FalloffStartRatio", PositiveOr(FalloffStartRatio, DefaultFalloffStartRatio));
            program.Uniform("ThicknessHeuristic", Math.Clamp(ThicknessHeuristic, 0.0f, 1.0f));
            program.Uniform("DenoiseEnabled", DenoiseEnabled);
            program.Uniform("DenoiseRadius", Math.Clamp(DenoiseRadius, 0, 16));
            program.Uniform("DenoiseSharpness", PositiveOr(DenoiseSharpness, DefaultDenoiseSharpness));
            program.Uniform("UseInputNormals", UseInputNormals);
            program.Uniform("UseVisibilityBitmask", UseVisibilityBitmask);
            program.Uniform("VisibilityBitmaskThickness", PositiveOr(VisibilityBitmaskThickness, DefaultVisibilityBitmaskThickness));
        }
    }
}