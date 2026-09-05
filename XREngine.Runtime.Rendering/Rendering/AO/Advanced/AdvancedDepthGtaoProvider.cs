using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Built-in Vulkan Advanced-pipeline GTAO implementation. It derives its
/// normal from final visibility depth and writes the frozen R8 AO target.
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
