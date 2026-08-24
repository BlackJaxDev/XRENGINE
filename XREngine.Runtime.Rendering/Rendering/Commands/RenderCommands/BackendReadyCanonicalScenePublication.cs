namespace XREngine.Rendering.Commands;

/// <summary>
/// Immutable identity of a resident scene publication owned by the shared GPU
/// scene database. A package never allocates, retires, or remaps these records.
/// </summary>
public readonly record struct BackendReadyCanonicalScenePublication(
    ulong DatabaseEpoch,
    ulong Sequence,
    ulong FrameGeneration,
    ulong TopologyGeneration,
    ulong ContentGeneration,
    ulong LookupGeneration);
