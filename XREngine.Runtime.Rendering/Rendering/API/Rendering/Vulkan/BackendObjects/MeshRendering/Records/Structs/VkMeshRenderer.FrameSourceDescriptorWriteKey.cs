using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public partial class VkMeshRenderer
    {
        internal readonly record struct FrameSourceDescriptorWriteKey(
            int DescriptorSlotIndex,
            uint Set,
            uint Binding,
            DescriptorType DescriptorType,
            uint DescriptorCount);
    }
}
