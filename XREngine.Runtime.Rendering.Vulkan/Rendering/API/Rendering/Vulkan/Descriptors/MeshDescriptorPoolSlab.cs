using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed class MeshDescriptorPoolSlab
{
    public required MeshDescriptorPoolSlabKey Key { get; init; }
    public required DescriptorPool Pool { get; init; }
    public int IssuedAllocationCount;
    public int LiveAllocationCount;
}
