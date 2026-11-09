using System.Numerics;

namespace XREngine.Rendering
{
    public sealed class PrototypeAmbientOcclusionSettings : AmbientOcclusionModeSettings
    {
        public static readonly Vector4 DefaultScaleFactors = new(0.1f, 0.2f, 0.4f, 0.8f);

        private float _intensity = 1.0f;
        private Vector4 _scaleFactors = DefaultScaleFactors;

        public PrototypeAmbientOcclusionSettings(AmbientOcclusionSettings owner)
            : base(owner, nameof(AmbientOcclusionSettings.Prototype))
        {
        }

        public float Intensity
        {
            get => _intensity;
            set => SetValue(ref _intensity, value, nameof(AmbientOcclusionSettings.Intensity));
        }

        /// <summary>The four sample radii, as fractions of the shared AO radius.</summary>
        public Vector4 ScaleFactors
        {
            get => _scaleFactors;
            set => SetValue(ref _scaleFactors, value);
        }

        public override void ApplyUniforms(XRRenderProgram program)
        {
            program.Uniform("Radius", PositiveOr(Owner.Radius, AmbientOcclusionSettings.DefaultRadius));
            program.Uniform("ScaleFactors", ScaleFactors);
            program.Uniform("Bias", Owner.Bias);
            program.Uniform("Intensity", Intensity);
        }
    }
}