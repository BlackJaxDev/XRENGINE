

using System.ComponentModel;
using System.Numerics;

namespace XREngine.Rendering
{
    public class MotionBlurSettings : PostProcessSettings
    {
        private bool _enabled;
        private float _shutterScale = 0.75f;
        private int _maxSamples = 12;
        private float _maxBlurPixels = 12.0f;
        private float _velocityThreshold = 0.002f;
        private float _depthRejectThreshold = 0.002f;
        private float _sampleFalloff = 2.0f;

        [Category("Motion Blur"), Description("Enable full-screen velocity-based motion blur.")]
        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        [Category("Motion Blur"), Description("Scales the length of the blur streak relative to encoded velocity.")]
        public float ShutterScale
        {
            get => _shutterScale;
            set => SetField(ref _shutterScale, Math.Clamp(value, 0.0f, 2.0f));
        }

        [Category("Motion Blur"), Description("Maximum number of samples taken per pixel when integrating blur.")]
        public int MaxSamples
        {
            get => _maxSamples;
            set => SetField(ref _maxSamples, Math.Clamp(value, 4, 64));
        }

        [Category("Motion Blur"), Description("Maximum blur radius in pixels before clamping motion length.")]
        public float MaxBlurPixels
        {
            get => _maxBlurPixels;
            set => SetField(ref _maxBlurPixels, Math.Clamp(value, 1.0f, 64.0f));
        }

        [Category("Motion Blur"), Description("Velocity magnitude (in NDC units) required before blur activates.")]
        public float VelocityThreshold
        {
            get => _velocityThreshold;
            set => SetField(ref _velocityThreshold, Math.Clamp(value, 0.0f, 0.5f));
        }

        [Category("Motion Blur"), Description("Depth difference threshold to reject samples that cross discontinuities.")]
        public float DepthRejectThreshold
        {
            get => _depthRejectThreshold;
            set => SetField(ref _depthRejectThreshold, Math.Clamp(value, 0.0f, 0.05f));
        }

        [Category("Motion Blur"), Description("Controls exponential falloff applied to farther samples.")]
        public float SampleFalloff
        {
            get => _sampleFalloff;
            set => SetField(ref _sampleFalloff, Math.Clamp(value, 0.1f, 8.0f));
        }

        public override void SetUniforms(XRRenderProgram program)
            => SetUniforms(program, Vector2.Zero);

        public void SetUniforms(XRRenderProgram program, Vector2 texelSize)
        {
            program.Uniform("TexelSize", texelSize);
            program.Uniform("ShutterScale", _shutterScale);
            program.Uniform("VelocityThreshold", _velocityThreshold);
            program.Uniform("DepthRejectThreshold", _depthRejectThreshold);
            program.Uniform("MaxBlurPixels", _maxBlurPixels);
            program.Uniform("SampleFalloff", _sampleFalloff);
            program.Uniform("MaxSamples", _maxSamples);
        }
    }
}