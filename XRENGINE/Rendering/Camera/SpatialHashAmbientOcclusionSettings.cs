using System;

namespace XREngine.Rendering
{
    public sealed class SpatialHashAmbientOcclusionSettings : AmbientOcclusionModeSettings
    {
        private float _samplesPerPixel = 3.0f;
        private float _distanceIntensity = 1.0f;
        private float _thickness = 0.5f;
        private int _samples = 64;
        private float _cellSize = 0.07f;
        private float _maxDistance = 1.5f;
        private int _steps = 8;
        private float _jitterScale = 0.35f;
        private bool _temporalReuseEnabled = true;
        private float _temporalBlendFactor = 0.9f;
        private float _temporalClamp = 0.2f;
        private float _temporalDepthRejectThreshold = 0.01f;
        private float _temporalMotionRejectionScale = 0.2f;

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
            get => _maxDistance;
            set => SetValue(ref _maxDistance, value, nameof(AmbientOcclusionSettings.SpatialHashMaxDistance));
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
            program.Uniform("Radius", PositiveOr(Owner.Radius, 0.9f));
            program.Uniform("Power", PositiveOr(Owner.Power, 1.2f));
            program.Uniform("KernelSize", PositiveOr(Samples, 64));
            program.Uniform("RayStepCount", PositiveOr(Steps, 6));
            program.Uniform("Bias", PositiveOr(Owner.Bias, 0.03f));
            program.Uniform("CellSize", PositiveOr(CellSize, 0.75f));
            program.Uniform("MaxRayDistance", PositiveOr(MaxDistance, 1.5f));
            program.Uniform("Thickness", PositiveOr(Thickness, 0.1f));
            program.Uniform("DistanceFade", PositiveOr(DistanceIntensity, 1.0f));
            program.Uniform("TemporalReuseEnabled", TemporalReuseEnabled);
            program.Uniform("TemporalBlendFactor", Math.Clamp(TemporalBlendFactor, 0.0f, 0.99f));
            program.Uniform("TemporalClamp", PositiveOr(TemporalClamp, 0.2f));
            program.Uniform("TemporalDepthRejectThreshold", PositiveOr(TemporalDepthRejectThreshold, 0.01f));
            program.Uniform("TemporalMotionRejectionScale", PositiveOr(TemporalMotionRejectionScale, 0.2f));
        }
    }
}