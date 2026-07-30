namespace XREngine.Rendering;

/// <summary>
/// One ordered visibility operation and the synchronization boundary that must
/// execute immediately before it when crossing GPU domains.
/// </summary>
public readonly record struct AdvancedVisibilitySequenceOperationDescriptor(
    EAdvancedVisibilitySequenceOperation Operation,
    EAdvancedVisibilitySynchronizationBoundary? BoundaryBefore,
    EAdvancedVisibilityRasterOrigin? RasterOrigin,
    bool PreservesExistingVisibility,
    string DebugLabel);
