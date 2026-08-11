using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanImageViewStructuralKey(
    ulong ImageHandle,
    ulong ImageGeneration,
    ImageViewCreateFlags Flags,
    ImageViewType ViewType,
    Format Format,
    ComponentSwizzle R,
    ComponentSwizzle G,
    ComponentSwizzle B,
    ComponentSwizzle A,
    ImageAspectFlags AspectMask,
    uint BaseMipLevel,
    uint LevelCount,
    uint BaseArrayLayer,
    uint LayerCount);
