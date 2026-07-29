namespace XREngine.Rendering;

/// <summary>
/// Light participation and residency state.
/// </summary>
[Flags]
public enum EAdvancedLightRecordFlags : uint
{
    None = 0,
    Enabled = 1u << 0,
    CastsShadow = 1u << 1,
    Static = 1u << 2,
    Volumetric = 1u << 3,
}
