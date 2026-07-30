namespace XREngine.Rendering;

/// <summary>
/// Ordered synchronization boundaries for reconstruction consumers and diagnostics.
/// </summary>
public enum EAdvancedReconstructionSynchronizationBoundary
{
    FinalVisibilityToReconstruction = 0,
    ReconstructionDiagnosticsToCapture,
}
