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
    public const string ShadingDebugOutput = ScopePrefix + ".DebugOutput";
}
