namespace XREngine;

[Flags]
public enum ERenderOutputQualityRequirement : uint
{
    None = 0,
    GpuAccelerated = 1u << 0,
    NativeResolution = 1u << 1,
    Foveation = 1u << 2,
    Multiview = 1u << 3,
    TemporalHistory = 1u << 4,
    IndependentScene = 1u << 5,
}
