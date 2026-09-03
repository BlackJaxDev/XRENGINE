namespace XREngine.Rendering;

/// <summary>
/// Stable shader asset paths for ARP 06 GPU Material Work Classification.
/// </summary>
public static class AdvancedClassificationShaderLibrary
{
    public const string Root = "Advanced/Classification";
    public const string InterfaceInclude = Root + "/ClassificationInterface.glslinc";
    public const string ResetCountersCompute = Root + "/ResetClassificationCounters.comp";
    public const string ClassifyTilesCompute = Root + "/ClassifyTiles.comp";
    public const string BuildIndirectCompute = Root + "/BuildClassificationIndirect.comp";
}
