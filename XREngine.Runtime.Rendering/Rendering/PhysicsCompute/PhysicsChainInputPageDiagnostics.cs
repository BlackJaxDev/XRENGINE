namespace XREngine.Rendering.Compute;

/// <summary>Counts input pages that could not be reused or prepared.</summary>
public readonly record struct PhysicsChainInputPageDiagnostics(
    long BusyCount,
    long FailureCount,
    string LastFailure,
    long AcquiredPageCount,
    long BufferAllocationCount,
    long MappedWriteCount,
    int QuarantinedPageCount);
