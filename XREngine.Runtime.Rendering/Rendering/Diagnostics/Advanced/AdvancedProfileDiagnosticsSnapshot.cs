namespace XREngine.Rendering;

/// <summary>
/// Detached diagnostic capture for one pipeline instance. Stage entries are
/// authoring observations; per-eye entries come from the immutable package
/// retained by that instance and are only comparable when frame, generation,
/// and output identity agree.
/// </summary>
public sealed record AdvancedProfileDiagnosticsSnapshot(
    ulong FrameId,
    int ResourceGeneration,
    ulong OutputId,
    AdvancedProfileStageDiagnostic[] Stages,
    AdvancedProfileEyeDiagnostic[] Eyes);
