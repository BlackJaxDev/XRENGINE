using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Built-in Advanced depth-derived GTAO contract. Backends derive normals from
/// final visibility depth and preserve R8 normalized logical visibility in
/// the output's declared physical storage encoding.
/// </summary>
public sealed class AdvancedDepthGtaoProvider : IAdvancedAmbientOcclusionProvider
{
    public static AdvancedDepthGtaoProvider Instance { get; } = new();

    private AdvancedDepthGtaoProvider() { }

    public string ProviderName => "Built-in depth GTAO";
    public bool IsSupported => true;
    public bool IsHalfResolution => false;
    public bool SupportsStereo => true;
    public EPixelInternalFormat OutputFormat => EPixelInternalFormat.R8;
    public string? UnsupportedReason => null;
}
