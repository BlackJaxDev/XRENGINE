using System;

namespace XREngine.Rendering
{
    public sealed class TonemappingSettings : PostProcessSettings
    {
        public const float DefaultMobiusTransition = 0.6f;
        public const float MinMobiusTransition = 0.01f;
        public const float MaxMobiusTransition = 4.0f;

        private ETonemappingType _tonemapping = ETonemappingType.Mobius;
        private float _mobiusTransition = DefaultMobiusTransition;

        public ETonemappingType Tonemapping
        {
            get => _tonemapping;
            set => SetField(ref _tonemapping, value);
        }

        public float MobiusTransition
        {
            get => _mobiusTransition;
            set => SetField(ref _mobiusTransition, Math.Clamp(value, MinMobiusTransition, MaxMobiusTransition));
        }

        public override void SetUniforms(XRRenderProgram program)
        {
            program.Uniform("TonemapType", (int)Tonemapping);
            program.Uniform("MobiusTransition", MobiusTransition);
        }
    }
}