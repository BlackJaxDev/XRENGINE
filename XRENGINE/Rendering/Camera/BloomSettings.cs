using System;
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
            float threshold = MathF.Max(0.0f, Threshold);
            float softKnee = MathF.Min(1.0f, MathF.Max(0.0f, SoftKnee));

            program.Uniform("BloomIntensity", MathF.Max(0.0f, Intensity));
            program.Uniform("BloomThreshold", threshold);
            program.Uniform("SoftKnee", softKnee);
            program.Uniform("Luminance", Engine.Rendering.Settings.DefaultLuminance);
        }
        public void SetBlurPassUniforms(XRRenderProgram program)
        {
            float threshold = MathF.Max(0.0f, Threshold);
            float softKnee = MathF.Min(1.0f, MathF.Max(0.0f, SoftKnee));
            float radius = MathF.Max(0.0f, Radius);
            bool useThreshold = threshold > 0.0f;

            program.Uniform("Radius", radius);
            program.Uniform("BloomThreshold", threshold);
            program.Uniform("BloomSoftKnee", MathF.Max(softKnee * threshold, 1e-4f));
            program.Uniform("UseThreshold", useThreshold);
        }
    }
}