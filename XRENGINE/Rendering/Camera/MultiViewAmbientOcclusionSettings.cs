using System;

namespace XREngine.Rendering
{
    public sealed class MultiViewAmbientOcclusionSettings : AmbientOcclusionModeSettings
    {
        private float _secondaryRadius = 1.6f;
        private float _blend = 0.6f;
        private float _spread = 0.5f;
        private float _depthPhi = 4.0f;
        private float _normalPhi = 64.0f;

        public MultiViewAmbientOcclusionSettings(AmbientOcclusionSettings owner)
            : base(owner, nameof(AmbientOcclusionSettings.MultiView))
        {
        }

        public float SecondaryRadius
        {
            get => _secondaryRadius;
            set => SetValue(ref _secondaryRadius, value, nameof(AmbientOcclusionSettings.SecondaryRadius));
        }

        public float Blend
        {
            get => _blend;
            set => SetValue(ref _blend, value, nameof(AmbientOcclusionSettings.MultiViewBlend));
        }

        public float Spread
        {
            get => _spread;
            set => SetValue(ref _spread, value, nameof(AmbientOcclusionSettings.MultiViewSpread));
        }

        public float DepthPhi
        {
            get => _depthPhi;
            set => SetValue(ref _depthPhi, value, nameof(AmbientOcclusionSettings.DepthPhi));
        }

        public float NormalPhi
        {
            get => _normalPhi;
            set => SetValue(ref _normalPhi, value, nameof(AmbientOcclusionSettings.NormalPhi));
        }

        public override void ApplyUniforms(XRRenderProgram program)
        {
            program.Uniform("Radius", PositiveOr(Owner.Radius, 0.9f));
            program.Uniform("SecondaryRadius", PositiveOr(SecondaryRadius, 1.6f));
            program.Uniform("Bias", PositiveOr(Owner.Bias, 0.03f));
            program.Uniform("Power", PositiveOr(Owner.Power, 1.4f));
            program.Uniform("MultiViewBlend", Math.Clamp(Blend, 0.0f, 1.0f));
            program.Uniform("MultiViewSpread", Math.Clamp(Spread, 0.0f, 1.0f));
            program.Uniform("DepthPhi", PositiveOr(DepthPhi, 4.0f));
            program.Uniform("NormalPhi", PositiveOr(NormalPhi, 64.0f));
        }
    }
}