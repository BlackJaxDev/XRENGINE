namespace XREngine.Rendering;

/// <summary>
/// Logical texture properties independent of descriptor encoding.
/// </summary>
[Flags]
public enum EAdvancedTextureRecordFlags : uint
{
    None = 0,
    Resident = 1u << 0,
    Srgb = 1u << 1,
    Storage = 1u << 2,
    Depth = 1u << 3,
    ShadowCompare = 1u << 4,
}
