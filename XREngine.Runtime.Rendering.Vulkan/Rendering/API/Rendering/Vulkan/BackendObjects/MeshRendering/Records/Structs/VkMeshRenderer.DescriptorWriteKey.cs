using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public partial class VkMeshRenderer
    {
        internal readonly record struct DescriptorWriteKey(
            int DescriptorSlotIndex,
            ulong DescriptorSetHandle,
            uint Set,
            uint Binding,
            DescriptorType DescriptorType,
            uint DescriptorCount);
    }
}
