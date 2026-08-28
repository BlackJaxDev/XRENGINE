namespace XREngine.Rendering;

/// <summary>Renderer-neutral sampler details not represented by the coarse filter enum.</summary>
[Flags]
public enum EAdvancedSamplerRecordFlags : uint
{
    None = 0,
    UsesMipmaps = 1u << 0,
    LinearMipmapInterpolation = 1u << 1,
    NearestMinification = 1u << 2,
    NearestMagnification = 1u << 3,
    ComparisonEnabled = 1u << 4,
    AnisotropyEnabled = 1u << 5,
}
