namespace XREngine.Rendering.Vulkan;

/// <summary>Immutable offset/count pair into one frame-slot-owned prepared stream.</summary>
internal readonly record struct VulkanPreparedStreamRange(int Start, int Count)
{
    internal bool IsEmpty => Count == 0;
    internal bool IsValidFor(int length) => Start >= 0 && Count >= 0 && Start <= length - Count;
}
