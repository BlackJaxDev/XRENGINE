using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed class ComputeDescriptorPoolBlock
{
    public DescriptorPool Pool;
    public uint MaxAllocations;
    public uint AllocatedAllocations;
    public bool UsesUpdateAfterBind;
}
