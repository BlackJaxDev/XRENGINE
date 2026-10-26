namespace XREngine.Rendering.Vulkan;

/// <summary>Holds read-only access to one current mapped frame slice.</summary>
internal ref struct VulkanMappedFrameReadScope
{
    private VulkanMappedFrameArena? _arena;
    private readonly VulkanMappedFrameSlice _slice;

    internal VulkanMappedFrameReadScope(VulkanMappedFrameArena arena,
        VulkanMappedFrameSlice slice, ReadOnlySpan<byte> bytes)
    {
        _arena = arena;
        _slice = slice;
        Bytes = bytes;
    }

    internal ReadOnlySpan<byte> Bytes { get; }

    public void Dispose()
    {
        VulkanMappedFrameArena? arena = _arena;
        if (arena is null)
            return;
        _arena = null;
        arena.EndRead(_slice);
    }
}
