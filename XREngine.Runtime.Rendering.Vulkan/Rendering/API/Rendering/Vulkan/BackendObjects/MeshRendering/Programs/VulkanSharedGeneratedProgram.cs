namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One generated mesh program shared by every renderer whose draw produces the same
/// program identity. Per-draw state (descriptor allocations, uniform buffers, vertex
/// input and pipelines) stays on each renderer.
/// </summary>
internal sealed class VulkanSharedGeneratedProgram(
    VulkanSharedGeneratedProgramKey key,
    XRRenderProgram data,
    VkRenderProgram program)
{
    public VulkanSharedGeneratedProgramKey Key { get; } = key;
    public XRRenderProgram Data { get; } = data;
    public VkRenderProgram Program { get; } = program;

    /// <summary>Renderer cache entries that hold this program. Guarded by the registry lock.</summary>
    internal int References;
}
