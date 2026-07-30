namespace XREngine.Rendering;

/// <summary>
/// Immutable resource-profile bits owned by document 05.
/// </summary>
[Flags]
public enum AdvancedReconstructionResourceFeature : ulong
{
    None = 0UL,
    Core = 1UL << 51,
    DebugOutput = 1UL << 52,
    DerivativeDiagnostics = 1UL << 53,
    GpuValidation = 1UL << 54,
    ReferenceOutput = 1UL << 55,
}
