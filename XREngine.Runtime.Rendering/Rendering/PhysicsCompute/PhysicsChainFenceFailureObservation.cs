namespace XREngine.Rendering.Compute;

/// <summary>Identifies one failed input or output page fence observation.</summary>
public readonly record struct PhysicsChainFenceFailureObservation(
    bool IsInputPage,
    int PageIndex,
    ulong PageOrdinalOrEpoch,
    ulong ObservationFrame,
    string Stage,
    long FenceRentalId,
    GpuFenceFailureDiagnostic? FenceFailure);
