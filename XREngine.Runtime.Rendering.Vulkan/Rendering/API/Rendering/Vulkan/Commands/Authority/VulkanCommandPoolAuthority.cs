using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns primary and per-thread native command-pool identities.</summary>
internal sealed class VulkanCommandPoolAuthority
{
    internal object Gate { get; } = new();
    internal Dictionary<int, CommandPool> GraphicsByThread { get; } = new();
    internal Dictionary<int, CommandPool> TransferByThread { get; } = new();
    internal CommandPool PrimaryGraphics { get; set; }
    internal CommandPool PrimaryTransfer { get; set; }
}
