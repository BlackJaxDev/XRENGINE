namespace XREngine.Rendering.Vulkan;

/// <summary>Exclusive bounded host write access to a canonical frame-data slice.</summary>
internal ref struct VulkanFrameDataWriteScope
{
    private VulkanFrameDataArena? _arena;
    private readonly VulkanFrameDataSlice _slice;

    internal VulkanFrameDataWriteScope(VulkanFrameDataArena arena, VulkanFrameDataSlice slice, Span<byte> bytes)
    {
        _arena = arena;
        _slice = slice;
        Bytes = bytes;
    }

    internal Span<byte> Bytes { get; }

    public void Dispose()
    {
        VulkanFrameDataArena? arena = _arena;
        if (arena is null)
            return;

        _arena = null;
        arena.EndWrite(_slice);
    }
}
