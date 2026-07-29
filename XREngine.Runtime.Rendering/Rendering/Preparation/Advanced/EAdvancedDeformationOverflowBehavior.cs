namespace XREngine.Rendering;

/// <summary>
/// Explicit behavior when an aggregate job cannot be admitted as a whole.
/// </summary>
public enum EAdvancedDeformationOverflowBehavior : uint
{
    KeepPreviousAndInvalidateVelocity = 0u,
    CpuDirectDiagnostic = 1u,
    DiagnosticBoundsProxy = 2u,
}
