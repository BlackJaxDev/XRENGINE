namespace XREngine.Runtime.Bootstrap;

public partial class UnitTestingWorldSettings
{
    public class VolumetricFogVolumeInitSettings
    {
        public TranslationXYZ Translation { get; set; } = new() { X = 0.0f, Y = 6.0f, Z = 0.0f };
        public TranslationXYZ HalfExtents { get; set; } = new() { X = 24.0f, Y = 8.0f, Z = 24.0f };
        public ColorRgb ScatteringColor { get; set; } = new() { R = 0.86f, G = 0.9f, B = 1.0f };
        public float Density { get; set; } = 0.025f;
        public float NoiseScale { get; set; } = 0.08f;
        public TranslationXYZ NoiseVelocity { get; set; } = new() { X = 0.0f, Y = 0.03f, Z = 0.01f };
        public float NoiseThreshold { get; set; } = 0.35f;
        public float NoiseAmount { get; set; } = 0.5f;
        public float EdgeFade { get; set; } = 0.0f;
        public float Anisotropy { get; set; } = 0.2f;
        public float LightContribution { get; set; } = 1.0f;
        public int Priority { get; set; } = 0;
        public float Intensity { get; set; } = 1.0f;
        public float MaxDistance { get; set; } = 150.0f;
        public float StepSize { get; set; } = 1.0f;
        public float JitterStrength { get; set; } = 0.5f;
    }
}
