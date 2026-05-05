using System;

namespace XREngine.Rendering
{
    public sealed class SpatialHashAmbientOcclusionSettings : AmbientOcclusionModeSettings
    {
        public const float DefaultSamplesPerPixel = 3.06f;
        public const float DefaultDistanceIntensity = 1.0f;
        public const float DefaultThickness = 1.917f;
        public const int DefaultSamples = 64;
        public const float DefaultCellSize = 0.141f;
        public const int DefaultSteps = 6;
        public const float DefaultJitterScale = 1.0f;
        public const bool DefaultTemporalReuseEnabled = true;
        public const float DefaultTemporalBlendFactor = 0.789f;
        public const float DefaultTemporalClamp = 0.499f;
        public const float DefaultTemporalDepthRejectThreshold = 0.01f;
        public const float DefaultTemporalMotionRejectionScale = 0.2f;

        private float _samplesPerPixel = DefaultSamplesPerPixel;
        private float _distanceIntensity = DefaultDistanceIntensity;
        private float _thickness = DefaultThickness;
        private int _samples = DefaultSamples;
        private float _cellSize = DefaultCellSize;
        private int _steps = DefaultSteps;
        private float _jitterScale = DefaultJitterScale;
        private bool _temporalReuseEnabled = DefaultTemporalReuseEnabled;
        private float _temporalBlendFactor = DefaultTemporalBlendFactor;
        private float _temporalClamp = DefaultTemporalClamp;
        private float _temporalDepthRejectThreshold = DefaultTemporalDepthRejectThreshold;
        private float _temporalMotionRejectionScale = DefaultTemporalMotionRejectionScale;

        public SpatialHashAmbientOcclusionSettings(AmbientOcclusionSettings owner)
            : base(owner, nameof(AmbientOcclusionSettings.SpatialHash))
        {
        }

        public float SamplesPerPixel
        {
            get => _samplesPerPixel;
            set => SetValue(ref _samplesPerPixel, value, nameof(AmbientOcclusionSettings.SamplesPerPixel));
        }

        public float DistanceIntensity
        {
            get => _distanceIntensity;
            set => SetValue(ref _distanceIntensity, value, nameof(AmbientOcclusionSettings.DistanceIntensity));
        }

        public float Thickness
        {
            get => _thickness;
            set => SetValue(ref _thickness, value, nameof(AmbientOcclusionSettings.Thickness));
        }

        public int Samples
        {
            get => _samples;
            set => SetValue(ref _samples, value, nameof(AmbientOcclusionSettings.Samples));
        }

        public float CellSize
        {
            get => _cellSize;
            set => SetValue(ref _cellSize, value, nameof(AmbientOcclusionSettings.SpatialHashCellSize));
        }

        public float MaxDistance
        {
            get => Owner.Radius;
            set => Owner.Radius = value;
        }

        public int Steps
        {
            get => _steps;
            set => SetValue(ref _steps, value, nameof(AmbientOcclusionSettings.SpatialHashSteps));
        }

        public float JitterScale
        {
            get => _jitterScale;
            set => SetValue(ref _jitterScale, value, nameof(AmbientOcclusionSettings.SpatialHashJitterScale));
        }

        public bool TemporalReuseEnabled
        {
            get => _temporalReuseEnabled;
            set => SetValue(ref _temporalReuseEnabled, value, nameof(AmbientOcclusionSettings.SpatialHashTemporalReuseEnabled));
        }

        public float TemporalBlendFactor
        {
            get => _temporalBlendFactor;
            set => SetValue(ref _temporalBlendFactor, value, nameof(AmbientOcclusionSettings.SpatialHashTemporalBlendFactor));
        }

        public float TemporalClamp
        {
            get => _temporalClamp;
            set => SetValue(ref _temporalClamp, value, nameof(AmbientOcclusionSettings.SpatialHashTemporalClamp));
        }

        public float TemporalDepthRejectThreshold
        {
            get => _temporalDepthRejectThreshold;
            set => SetValue(ref _temporalDepthRejectThreshold, value, nameof(AmbientOcclusionSettings.SpatialHashTemporalDepthRejectThreshold));
        }

        public float TemporalMotionRejectionScale
        {
            get => _temporalMotionRejectionScale;
            set => SetValue(ref _temporalMotionRejectionScale, value, nameof(AmbientOcclusionSettings.SpatialHashTemporalMotionRejectionScale));
        }

        public override void ApplyUniforms(XRRenderProgram program)
        {
            program.Uniform("Radius", PositiveOr(Owner.Radius, AmbientOcclusionSettings.SpatialHashDefaultRadius));
            program.Uniform("Power", PositiveOr(Owner.Power, AmbientOcclusionSettings.SpatialHashDefaultPower));
            program.Uniform("KernelSize", PositiveOr(Samples, DefaultSamples));
            program.Uniform("RayStepCount", PositiveOr(Steps, DefaultSteps));
            program.Uniform("Bias", PositiveOr(Owner.Bias, AmbientOcclusionSettings.SpatialHashDefaultBias));
            program.Uniform("CellSize", PositiveOr(CellSize, DefaultCellSize));
            program.Uniform("MaxRayDistance", PositiveOr(Owner.Radius, AmbientOcclusionSettings.SpatialHashDefaultRadius));
            program.Uniform("Thickness", PositiveOr(Thickness, DefaultThickness));
            program.Uniform("DistanceFade", PositiveOr(DistanceIntensity, DefaultDistanceIntensity));
            program.Uniform("TemporalReuseEnabled", TemporalReuseEnabled);
            program.Uniform("TemporalBlendFactor", Math.Clamp(TemporalBlendFactor, 0.0f, 0.99f));
            program.Uniform("TemporalClamp", PositiveOr(TemporalClamp, DefaultTemporalClamp));
            program.Uniform("TemporalDepthRejectThreshold", PositiveOr(TemporalDepthRejectThreshold, DefaultTemporalDepthRejectThreshold));
            program.Uniform("TemporalMotionRejectionScale", PositiveOr(TemporalMotionRejectionScale, DefaultTemporalMotionRejectionScale));
        }
    }
}
