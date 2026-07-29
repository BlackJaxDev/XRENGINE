namespace XREngine.Rendering;

/// <summary>
/// Shader-visible logical resource reference state.
/// </summary>
[Flags]
public enum EAdvancedResourceReferenceFlags : uint
{
    None = 0,
    Resident = 1u << 0,
    Fallback = 1u << 1,
    StaleGeneration = 1u << 2,
}
