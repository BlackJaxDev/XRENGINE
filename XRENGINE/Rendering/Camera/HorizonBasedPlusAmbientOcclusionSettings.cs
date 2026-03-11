using System;

namespace XREngine.Rendering
{
    public sealed class HorizonBasedPlusAmbientOcclusionSettings : AmbientOcclusionModeSettings
    {
        private float _detailAO = 0.0f;
        private bool _blurEnabled = true;
        private int _blurRadius = 8;
        private float _blurSharpness = 4.0f;
        private bool _useInputNormals = true;
        private float _metersToViewSpaceUnits = 1.0f;

        public HorizonBasedPlusAmbientOcclusionSettings(AmbientOcclusionSettings owner)
            : base(owner, nameof(AmbientOcclusionSettings.HorizonBasedPlus))
        {
        }

        public float DetailAO
        {
            get => _detailAO;
            set => SetValue(ref _detailAO, value, nameof(AmbientOcclusionSettings.HBAODetailAO));
        }

        public bool BlurEnabled
        {
            get => _blurEnabled;
            set => SetValue(ref _blurEnabled, value, nameof(AmbientOcclusionSettings.HBAOBlurEnabled));
        }

        public int BlurRadius
        {
            get => _blurRadius;
            set => SetValue(ref _blurRadius, value, nameof(AmbientOcclusionSettings.HBAOBlurRadius));
        }

        public float BlurSharpness
        {
            get => _blurSharpness;
            set => SetValue(ref _blurSharpness, value, nameof(AmbientOcclusionSettings.HBAOBlurSharpness));
        }

        public bool UseInputNormals
        {
            get => _useInputNormals;
            set => SetValue(ref _useInputNormals, value, nameof(AmbientOcclusionSettings.HBAOUseInputNormals));
        }

        public float MetersToViewSpaceUnits
        {
            get => _metersToViewSpaceUnits;
            set => SetValue(ref _metersToViewSpaceUnits, value, nameof(AmbientOcclusionSettings.HBAOMetersToViewSpaceUnits));
        }

        public override void ApplyUniforms(XRRenderProgram program)
        {
            program.Uniform("Radius", PositiveOr(Owner.Radius, 2.0f));
            program.Uniform("Bias", PositiveOr(Owner.Bias, 0.1f));
            program.Uniform("Power", PositiveOr(Owner.Power, 2.0f));
            program.Uniform("DetailAO", Math.Clamp(DetailAO, 0.0f, 5.0f));
            program.Uniform("BlurEnabled", BlurEnabled);
            program.Uniform("BlurRadius", Math.Clamp(BlurRadius, 0, 16));
            program.Uniform("BlurSharpness", PositiveOr(BlurSharpness, 4.0f));
            program.Uniform("UseInputNormals", UseInputNormals);
            program.Uniform("MetersToViewSpaceUnits", PositiveOr(MetersToViewSpaceUnits, 1.0f));
        }
    }
}