namespace XREngine.Rendering;

/// <summary>
/// Diagnostic visualization modes for GPU material work classification.
/// </summary>
public enum EAdvancedClassificationDebugView : uint
{
    Disabled = 0u,
    ActiveTiles = 1u,
    KernelCountPerTile = 2u,
    PrimaryKernelId = 3u,
    PixelDensityHeatmap = 4u,
    OverflowMask = 5u,
}
