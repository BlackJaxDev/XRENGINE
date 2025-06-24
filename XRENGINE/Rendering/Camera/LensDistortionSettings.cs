
using XREngine.Data.Core;

namespace XREngine.Rendering
{
    public class LensDistortionSettings : XRBase
    {
        private float _intensity = 0.0f; // Default intensity is 0, meaning no distortion.
        public float Intensity
        {
            get => _intensity;
            set => SetField(ref _intensity, value);
        }

        public void SetUniforms(XRRenderProgram program)
        {
            program.Uniform("LensDistortionIntensity", Intensity);
        }
    }
}