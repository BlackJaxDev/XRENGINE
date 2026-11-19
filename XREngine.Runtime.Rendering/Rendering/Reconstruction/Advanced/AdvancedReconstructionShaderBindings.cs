namespace XREngine.Rendering;

/// <summary>
/// Backend-specific reconstruction bindings. <see cref="Shaders.AdvancedShaderAccessLibrary"/>
/// writes these values into the shader preamble, so this class is the one source of the numbers.
/// <para>
/// OpenGL has one SSBO binding namespace for all programs. Bindings 30 to 34 are reserved for the
/// reconstruction tables, and no other OpenGL path uses them: mesh deformation uses 39 to 45
/// (<see cref="MeshDeformationBindingLayout"/>), and the forward lighting shaders use 35 to 38.
/// Because no other draw changes these bindings, the tables stay bound from visibility preparation
/// to native shading. Vulkan aliases the immutable visibility set-1 geometry tables.
/// </para>
/// </summary>
public static class AdvancedReconstructionShaderBindings
{
    public const uint StaticVertices = 30u;
    public const uint PreSkinnedCurrentVertices = 31u;
    public const uint PreSkinnedPreviousVertices = 32u;
    public const uint Indices = 33u;
    public const uint Counters = 34u;

    public const uint VulkanStaticVertices = 33u;
    public const uint VulkanPreSkinnedCurrentVertices = 34u;
    public const uint VulkanPreSkinnedPreviousVertices = 35u;
    public const uint VulkanPreparedDrawDeformations = 46u;
    public const uint VulkanIndices = 47u;
    public const uint VulkanCounters = 48u;
}
