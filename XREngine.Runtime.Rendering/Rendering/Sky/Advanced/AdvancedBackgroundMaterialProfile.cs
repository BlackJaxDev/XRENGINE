namespace XREngine.Rendering;

/// <summary>
/// Authoring receipt for a background shader that always emits canonical far depth,
/// never writes fragment depth, writes only color attachment zero, and can explicitly
/// make the native HDR alpha channel fully covered. Recreate the receipt after editing
/// shaders; arbitrary depth-varying geometry is not admitted.
/// </summary>
public sealed record AdvancedBackgroundMaterialProfile(
    long SourceShaderRevision,
    bool SupportsStereo,
    bool WritesOpaqueAlpha = false,
    string? UnsupportedReason = null);
