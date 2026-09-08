namespace XREngine.Rendering;

/// <summary>
/// Immutable observation published at an executable Advanced command boundary.
/// Enqueue acceptance is authoring evidence only and does not certify GPU completion.
/// </summary>
public readonly record struct AdvancedProfileStageDiagnostic(
    bool Observed,
    ulong FrameId,
    EAdvancedRenderStage Stage,
    EAdvancedVisibilityStageBackendPhase Phase,
    EAdvancedProfileStageDiagnosticState State,
    int ResourceGeneration,
    ulong OutputId,
    string? Reason);
