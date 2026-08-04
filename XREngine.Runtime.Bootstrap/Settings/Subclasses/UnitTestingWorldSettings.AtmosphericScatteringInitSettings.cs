namespace XREngine.Runtime.Bootstrap;

public partial class UnitTestingWorldSettings
{
    public class AtmosphericScatteringInitSettings
    {
        public TranslationXYZ Translation { get; set; } = new() { X = 0.0f, Y = 0.0f, Z = 0.0f };
        public float GroundRadius { get; set; } = 6371000.0f;
        public float AtmosphereHeight { get; set; } = 100000.0f;
        public float GroundLevelOffset { get; set; } = 0.0f;
        public float SunIntensity { get; set; } = 20.0f;
        public ColorRgb SunColor { get; set; } = new() { R = 1.0f, G = 1.0f, B = 1.0f };
        public float RayleighScaleHeight { get; set; } = 8000.0f;
        public float MieScaleHeight { get; set; } = 1200.0f;
        public ColorRgb RayleighScattering { get; set; } = new() { R = 5.802e-6f, G = 13.558e-6f, B = 33.1e-6f };
        public ColorRgb MieScattering { get; set; } = new() { R = 3.996e-6f, G = 3.996e-6f, B = 3.996e-6f };
        public float MieAnisotropy { get; set; } = 0.76f;
        public float ExposureScale { get; set; } = 1.0f;
        public float GroundAlbedo { get; set; } = 0.30f;
        public float MaxDistance { get; set; } = 200000.0f;
        public int ViewSamples { get; set; } = 8;
        public int OpticalDepthSamples { get; set; } = 0;
        public float JitterStrength { get; set; } = 0.5f;
        public bool TemporalEnabled { get; set; } = true;
    }
}
