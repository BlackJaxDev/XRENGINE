using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan.RenderGraph;

internal readonly record struct VulkanBufferAllocationRequest(BufferResourceDescriptor Descriptor)
{
    public string Name => Descriptor.Name;
    public RenderResourceLifetime Lifetime => Descriptor.Lifetime;
    public ulong SizeInBytes => Descriptor.SizeInBytes;
    public EBufferTarget Target => Descriptor.Target;
    public EBufferUsage Usage => Descriptor.Usage;
    public bool SupportsAliasing => Descriptor.SupportsAliasing;
    public VulkanBufferAliasKey AliasKey => new(Descriptor.SizeInBytes, Descriptor.Target, Descriptor.Usage);
}
