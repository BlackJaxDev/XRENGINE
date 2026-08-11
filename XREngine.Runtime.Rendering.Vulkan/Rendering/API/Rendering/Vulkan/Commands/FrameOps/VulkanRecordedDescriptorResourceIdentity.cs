using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>One concrete native resource referenced by a descriptor payload.</summary>
internal readonly record struct VulkanRecordedDescriptorResourceIdentity(
    ObjectType Type,
    ulong Handle,
    ulong Generation,
    ImageLayout Layout)
{
    public bool IsComplete => Handle != 0UL && Generation != 0UL;
}
