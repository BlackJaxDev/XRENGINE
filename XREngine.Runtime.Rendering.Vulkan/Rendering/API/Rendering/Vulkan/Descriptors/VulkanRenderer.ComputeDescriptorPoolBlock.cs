using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private sealed class ComputeDescriptorPoolBlock
    {
        public DescriptorPool Pool;
        public uint MaxAllocations;
        public uint AllocatedAllocations;
        public bool UsesUpdateAfterBind;
    }
}
