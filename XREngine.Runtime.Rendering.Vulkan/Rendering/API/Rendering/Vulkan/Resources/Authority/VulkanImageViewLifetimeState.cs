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
