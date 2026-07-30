namespace XREngine.Rendering;

/// <summary>
/// Stable shader asset identities for visibility raster, coverage, meshlet, and diagnostics.
/// </summary>
public static class AdvancedVisibilityShaderLibrary
{
    public const string Root = "Advanced/Visibility";
    public const string Vertex = Root + "/VisibilityRaster.vert";
    public const string OpaqueFragment = Root + "/VisibilityRaster.frag";
    public const string MaskedFragment = Root + "/VisibilityRasterMasked.frag";
    public const string Mesh = Root + "/VisibilityRaster.mesh";
    public const string DebugCompute = Root + "/VisibilityDebug.comp";
    public const string ClearCompute = Root + "/ClearVisibility.comp";
    public const string ResetCountersCompute =
        Root + "/ResetVisibilityCounters.comp";
    public const string ResetRangesCompute =
        Root + "/ResetVisibilityRanges.comp";
    public const string ValidateCompute =
        Root + "/ValidateVisibility.comp";
    public const string DepthPyramidCompute =
        "Advanced/Preparation/BuildDepthPyramid.comp";
    public const string EarlyVisibilityCompute =
        "Advanced/Preparation/EarlyVisibility.comp";
    public const string LateVisibilityCompute =
        "Advanced/Preparation/LateVisibility.comp";
    public const string BuildIndirectCompute =
        "Advanced/Preparation/BuildVisibilityIndirect.comp";
}
