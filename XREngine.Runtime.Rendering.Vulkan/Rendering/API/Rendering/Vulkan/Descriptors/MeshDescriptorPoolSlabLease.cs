using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed class MeshDescriptorPoolSlabLease
{
    internal readonly MeshDescriptorPoolSlab Slab;

    internal MeshDescriptorPoolSlabLease(MeshDescriptorPoolSlab slab)
        => Slab = slab;

    internal DescriptorPool Pool => Slab.Pool;
    internal bool Released { get; set; }
}
