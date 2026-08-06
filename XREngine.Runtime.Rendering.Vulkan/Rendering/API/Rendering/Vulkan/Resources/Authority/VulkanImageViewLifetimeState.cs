using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns image-view interning and destruction-admission state.</summary>
internal sealed class VulkanImageViewLifetimeState
{
    internal ConcurrentDictionary<ulong, string> LiveHandles { get; } = new();
    internal ConcurrentDictionary<ulong, ImageViewCreateInfo> DescriptorHeapCreateInfos { get; } = new();
    internal ConcurrentDictionary<ulong, byte> RetiringImageHandles { get; } = new();
    internal object InternGate { get; } = new();
    internal Dictionary<VulkanImageViewStructuralKey, InternedImageViewEntry> InternedViews { get; } = new();
    internal Dictionary<ulong, VulkanImageViewStructuralKey> InternedKeysByHandle { get; } = new();
}

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

internal sealed class InternedImageViewEntry(ImageView view)
{
    internal ImageView View { get; } = view;
    internal int ReferenceCount { get; set; } = 1;
}
