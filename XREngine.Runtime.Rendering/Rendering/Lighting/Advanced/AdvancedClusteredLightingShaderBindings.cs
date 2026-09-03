namespace XREngine.Rendering;

/// <summary>
/// Descriptor and SSBO binding conventions for clustered lighting shaders.
/// </summary>
public static class AdvancedClusteredLightingShaderBindings
{
    public const uint FroxelGrid = 10u;
    public const uint LightIndexList = 11u;
    public const uint LightingCounters = 12u;
}
