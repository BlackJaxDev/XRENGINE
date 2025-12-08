

using System.Numerics;
using XREngine.Data;
using XREngine.Data.Colors;

namespace XREngine.Rendering
{
    public class VignetteSettings : PostProcessSettings
    {
        public const string VignetteUniformName = "Vignette";

        public ColorF3 Color { get; set; } = new ColorF3();
        public float Intensity { get; set; } = 0.0f;
        public float Power { get; set; } = 0.0f;

        public override void SetUniforms(XRRenderProgram program)
        {
            program.Uniform($"{VignetteUniformName}.{nameof(Color)}", Color);
            program.Uniform($"{VignetteUniformName}.{nameof(Intensity)}", Intensity);
            program.Uniform($"{VignetteUniformName}.{nameof(Power)}", Power);
        }
    }
}