using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Retains native and arena ownership when an accepted synchronous submission cannot prove
/// completion in its initiating call. The completion-maintenance pass retries the fence.
/// </summary>
internal readonly record struct VulkanRetiredSynchronousSubmission(
    CommandBuffer commandBuffer,
    CommandPool commandPool,
    Fence fence,
    VulkanFrameDataArena? arena,
    in VulkanFrameDataSlice slice,
    bool removeOneTimeOwner,
    bool completeSynchronousLifetime,
    int frameSlotLifetime,
    string owner)
{
    internal CommandBuffer CommandBuffer { get; } = commandBuffer;
    internal CommandPool CommandPool { get; } = commandPool;
    internal Fence Fence { get; } = fence;
    internal VulkanFrameDataArena? Arena { get; } = arena;
    internal VulkanFrameDataSlice Slice { get; } = slice;
    internal bool RemoveOneTimeOwner { get; } = removeOneTimeOwner;
    internal bool CompleteSynchronousLifetime { get; } = completeSynchronousLifetime;
    internal int FrameSlotLifetime { get; } = frameSlotLifetime;
    internal string Owner { get; } = owner;
    internal bool IsValid => CommandBuffer.Handle != 0;
}
