namespace XREngine.Rendering.Compute;

/// <summary>
/// Delayed aggregate diagnostics for the dispatcher-owned resident GPU arenas.
/// </summary>
public readonly record struct GPUPhysicsChainArenaDiagnostics(
    long CapacityBytes,
    long LiveBytes,
    long DeferredBytes,
    long GrowthCopyBytes,
    long StaticUploadBytes,
    long DynamicUploadBytes,
    int GrowthCount,
    int ResourceGeneration);
