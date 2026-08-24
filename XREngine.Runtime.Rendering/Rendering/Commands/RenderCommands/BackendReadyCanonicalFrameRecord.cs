namespace XREngine.Rendering.Commands;

/// <summary>
/// Frame-level canonical publication state valid independently of visible draw
/// enumeration.
/// </summary>
public readonly record struct BackendReadyCanonicalFrameRecord(
    ulong FrameId,
    ulong FrameGeneration,
    ulong SourceRevision,
    ulong DependencySignature);
