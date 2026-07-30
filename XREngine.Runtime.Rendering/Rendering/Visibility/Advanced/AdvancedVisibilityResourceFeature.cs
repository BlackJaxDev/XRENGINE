namespace XREngine.Rendering;

/// <summary>
/// Immutable resource-profile bits for optional document-04 diagnostics.
/// </summary>
[Flags]
public enum AdvancedVisibilityResourceFeature : ulong
{
    None = 0UL,
    Core = 1UL << 48,
    DebugOutput = 1UL << 49,
    GpuValidation = 1UL << 50,
}
