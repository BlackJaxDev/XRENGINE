namespace XREngine.Rendering.Resources;

/// <summary>
/// Immutable structural settings captured once for a managed render-resource request.
/// The revision is stable while every feature-affecting value remains unchanged.
/// </summary>
public readonly record struct ResourceGenerationSettingsSnapshot(
    bool OutputHDR,
    EAntiAliasingMode AntiAliasingMode,
    uint MsaaSampleCount,
    bool Stereo,
    ulong FeatureMask,
    uint ReservedViewCount,
    uint ReservedEyeIndex,
    RenderPipelineExternalTargetKind ExternalTargetKind,
    ulong Revision);
