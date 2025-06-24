using XREngine.Data.Core;

namespace XREngine.Rendering
{
    public class BloomSettings : XRBase
    {
        private float _intensity = 1.0f;
        private float _threshold = 1.0f;
        private float _softKnee = 0.5f;
        private float _radius = 1.0f;

        public BloomSettings()
        {

        }

        public float Intensity
        {
            get => _intensity;
            set => SetField(ref _intensity, value);
        }
        public float Threshold
        {
            get => _threshold;
            set => SetField(ref _threshold, value);
        }
        public float SoftKnee
        {
            get => _softKnee;
            set => SetField(ref _softKnee, value);
        }
        public float Radius
        {
            get => _radius;
            set => SetField(ref _radius, value);
        }

        public void SetBrightPassUniforms(XRRenderProgram program)
        {
            program.Uniform("BloomIntensity", Intensity);
            program.Uniform("BloomThreshold", Threshold);
            program.Uniform("SoftKnee", SoftKnee);
            program.Uniform("Luminance", Engine.Rendering.Settings.DefaultLuminance);
        }
        public void SetBlurPassUniforms(XRRenderProgram program)
        {
            program.Uniform("Radius", Radius);
        }
    }
}