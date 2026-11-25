namespace XREngine.Rendering.Vulkan;

/// <summary>One exact text SSBO and its selected published descriptor element.</summary>
internal readonly record struct VulkanStaticUiTextBufferAuthority(
    VulkanRecordedBufferIdentity Buffer,
    VulkanDescriptorPayload Payload,
    uint SetIndex,
    uint BindingIndex)
{
    internal bool IsComplete => Buffer.IsBound && Buffer.IsComplete &&
        Payload.DescriptorType == Silk.NET.Vulkan.DescriptorType.StorageBuffer &&
        Payload.PrimaryHandle == Buffer.BufferHandle &&
        Payload.PrimaryGeneration == Buffer.AllocationGeneration;
}
