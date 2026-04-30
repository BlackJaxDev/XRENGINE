
using System;
using XREngine.Data;
using XREngine.Data.Colors;

namespace XREngine.Rendering
{
    public class VignetteSettings : PostProcessSettings
    {
        public const string VignetteUniformName = "Vignette";

        private bool _enabled;
        private ColorF3 _color = new();
        private float _intensity = 0.35f;
        private float _power = 2.0f;

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public ColorF3 Color
        {
            get => _color;
            set => SetField(ref _color, value);
        }

        public float Intensity
        {
            get => _intensity;
            set => SetField(ref _intensity, Math.Clamp(value, 0.0f, 1.0f));
        }

        public float Power
        {
            get => _power;
            set => SetField(ref _power, MathF.Max(0.01f, value));
        }

        public override void SetUniforms(XRRenderProgram program)
        {
            program.Uniform($"{VignetteUniformName}.{nameof(Color)}", Color);
            program.Uniform($"{VignetteUniformName}.{nameof(Intensity)}", Enabled ? Intensity : 0.0f);
            program.Uniform($"{VignetteUniformName}.{nameof(Power)}", Power);
        }
    }
}