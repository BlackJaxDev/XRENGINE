namespace XREngine.Rendering;

/// <summary>
/// Descriptor and SSBO binding conventions for material classification compute shaders.
/// </summary>
public static class AdvancedClassificationShaderBindings
{
    public const uint InVisibilityIdentity = 0u;
    public const uint InVisibilityMetadata = 1u;
    public const uint InDepth = 2u;

    public const uint OutActiveTiles = 4u;
    public const uint OutKernelTiles = 5u;
    public const uint OutCounters = 6u;
    public const uint OutDispatchArgs = 7u;
    public const uint OutDebugImage = 8u;
    public const uint KernelCounts = 9u;
}
