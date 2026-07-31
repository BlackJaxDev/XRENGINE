namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable operations and coalesced destination ranges for one publication
/// frequency in a compiled auto-uniform material plan.
/// </summary>
internal sealed class VulkanAutoUniformFrequencyPlan(
    EVulkanBindingFrequency frequency,
    VulkanAutoUniformBindingOperation[] operations,
    VulkanAutoUniformDirtyRange[] dirtyRanges)
{
    internal EVulkanBindingFrequency Frequency { get; } = frequency;
    internal VulkanAutoUniformBindingOperation[] Operations { get; } = operations;
    internal VulkanAutoUniformDirtyRange[] DirtyRanges { get; } = dirtyRanges;
}
