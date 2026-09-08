namespace XREngine.Rendering;

/// <summary>
/// Authoring receipt for a background shader that always emits canonical far depth,
/// never writes fragment depth, and writes only color attachment zero. Recreate the
/// receipt after editing shaders; arbitrary depth-varying geometry is not admitted.
/// </summary>
public sealed record AdvancedBackgroundMaterialProfile(long SourceShaderRevision, bool SupportsStereo, string? UnsupportedReason = null);
