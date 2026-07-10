namespace XREngine;

public readonly record struct VrViewRenderModeResolution(
    EVrViewRenderMode RequestedMode,
    EVrViewRenderMode EffectiveMode,
    EVrViewRenderImplementationPath EffectiveImplementationPath,
    EVrTemporalHistoryPolicy TemporalHistoryPolicy,
    bool IsSupported,
    string? Diagnostic);
