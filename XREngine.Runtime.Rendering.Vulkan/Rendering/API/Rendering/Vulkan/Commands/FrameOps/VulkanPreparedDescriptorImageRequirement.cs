using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact sampled-image state needed by a prepared mesh secondary. The record
/// is resolved on the render thread before workers begin native encoding.
/// </summary>
internal readonly record struct VulkanPreparedDescriptorImageRequirement(
    ulong ImageHandle,
    ulong ResourceGeneration,
    uint MipLevel,
    uint ArrayLayer,
    ImageAspectFlags AspectMask,
    ImageLayout Layout);

/// <summary>Frozen descriptor payload generation captured for one prepared draw.</summary>
internal readonly record struct VulkanPreparedDescriptorImagePayload(
    ulong DescriptorSetHandle,
    ulong ImagePayloadGeneration);
