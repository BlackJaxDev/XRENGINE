namespace XREngine.Rendering;

/// <summary>
/// Stable resource names for ARP 07 native opaque shading and lighting attachments.
/// </summary>
public static class AdvancedShadingResourceNames
{
    public const string ScopePrefix = "AdvancedShading";

    public const string OpaqueHdr = ScopePrefix + ".OpaqueHdr";
    public const string DenseVelocity = ScopePrefix + ".DenseVelocity";
    public const string ReactiveMask = ScopePrefix + ".ReactiveMask";
    public const string ShadingDiagnostics = ScopePrefix + ".ShadingDiagnostics";
    /// <summary>
    /// 1x1 R32_UINT storage image that fills the shading diagnostics binding when an output does
    /// not declare <see cref="ShadingDiagnostics"/>. The shading push flag stops every store to it.
    /// </summary>
    public const string ShadingDiagnosticsPlaceholder = ScopePrefix + ".ShadingDiagnosticsPlaceholder";
    public const string SampleRadianceReactive = ScopePrefix + ".SampleRadianceReactive";
    public const string ShadingDebugOutput = ScopePrefix + ".DebugOutput";
}
