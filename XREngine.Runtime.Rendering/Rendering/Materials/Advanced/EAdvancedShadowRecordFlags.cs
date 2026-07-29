namespace XREngine.Rendering;

/// <summary>
/// Shadow residency and reuse state.
/// </summary>
[Flags]
public enum EAdvancedShadowRecordFlags : uint
{
    None = 0,
    Resident = 1u << 0,
    StaticCache = 1u << 1,
    StaleFallback = 1u << 2,
    MomentEncoded = 1u << 3,
}
