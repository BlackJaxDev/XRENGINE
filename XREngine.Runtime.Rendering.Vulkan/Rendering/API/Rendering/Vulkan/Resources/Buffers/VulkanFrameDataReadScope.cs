namespace XREngine.Rendering.Vulkan;

/// <summary>Bounded host read access after any required non-coherent invalidation.</summary>
internal ref struct VulkanFrameDataReadScope
{
    private VulkanFrameDataArena? _arena;

    internal VulkanFrameDataReadScope(VulkanFrameDataArena arena, Span<byte> bytes)
    {
        _arena = arena;
        Bytes = bytes;
    }

    internal Span<byte> Bytes { get; }

    public void Dispose()
    {
        VulkanFrameDataArena? arena = _arena;
        if (arena is null)
            return;

        _arena = null;
        arena.EndRead();
    }
}
