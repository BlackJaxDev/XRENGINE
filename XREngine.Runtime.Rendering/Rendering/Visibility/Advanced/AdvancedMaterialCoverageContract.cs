namespace XREngine.Rendering;

/// <summary>
/// Fixed material-table convention shared by masked visibility and native material shading.
/// </summary>
public static class AdvancedMaterialCoverageContract
{
    public const uint CoverageTextureBinding = 0u;
    public const uint AlphaCutoffConstantWord = 0u;
    public const uint UvScaleXConstantWord = 1u;
    public const uint UvScaleYConstantWord = 2u;
    public const uint UvBiasXConstantWord = 3u;
    public const uint UvBiasYConstantWord = 4u;
    public const float DefaultAlphaCutoff = 0.5f;
}
