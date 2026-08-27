namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Temporary lease used to transfer the prior wrapper allocation into the
/// immutable descriptor slot that still references it.
/// </summary>
internal readonly record struct VulkanBindlessMaterialTextureSlotTransfer(
    uint DescriptorIndex,
    uint SlotGeneration)
{
    internal bool IsValid => DescriptorIndex != 0U && SlotGeneration != 0U;
}
