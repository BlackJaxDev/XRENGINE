namespace XREngine.Rendering;

/// <summary>
/// Immutable diagnostic view of one compact material-table render-pass slot.
/// </summary>
public readonly record struct ZeroReadbackMaterialTablePassDiagnostic(
    int RenderPass,
    EZeroReadbackMaterialTableDiagnosticStage Stage,
    ulong FrameId);
