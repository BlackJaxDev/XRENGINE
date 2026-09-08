namespace XREngine.Rendering;

/// <summary>Flags describing the encoded probe payload consumed by native IBL shading.</summary>
[Flags]
public enum EAdvancedProbeRecordFlags : uint
{
    None = 0u,
    Valid = 1u << 0,
    Octahedral = 1u << 1,
    ParallaxCorrected = 1u << 2,
    Normalized = 1u << 3,
}
