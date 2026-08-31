using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies one live descriptor-set publication without confusing a recycled
/// native handle for the descriptor generation that observed a retired resource.
/// </summary>
internal readonly record struct VulkanDescriptorSetGenerationReference(
    DescriptorSet Set,
    ulong Generation);
