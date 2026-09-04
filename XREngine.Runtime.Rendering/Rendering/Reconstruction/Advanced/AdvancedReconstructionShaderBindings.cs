namespace XREngine.Rendering;

/// <summary>
/// Backend-specific reconstruction bindings. OpenGL retains its established
/// local range; Vulkan aliases the immutable visibility set-1 geometry tables.
/// </summary>
public static class AdvancedReconstructionShaderBindings
{
    public const uint StaticVertices = 37u;
    public const uint PreSkinnedCurrentVertices = 38u;
    public const uint PreSkinnedPreviousVertices = 39u;
    public const uint Indices = 40u;
    public const uint Counters = 41u;

    public const uint VulkanStaticVertices = 33u;
    public const uint VulkanPreSkinnedCurrentVertices = 34u;
    public const uint VulkanPreSkinnedPreviousVertices = 35u;
    public const uint VulkanPreparedDrawDeformations = 46u;
    public const uint VulkanIndices = 47u;
    public const uint VulkanCounters = 48u;
}