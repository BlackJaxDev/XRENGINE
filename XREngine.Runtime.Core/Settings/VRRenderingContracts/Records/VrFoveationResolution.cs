namespace XREngine;

public readonly record struct VrFoveationResolution(
    EVrFoveationMode RequestedMode,
    EVrFoveationMode EffectiveMode,
    EVrFoveationQualityPreset QualityPreset,
    EVrFoveationCapabilityPath CapabilityPath,
    bool IsSupported,
    string? Diagnostic);
