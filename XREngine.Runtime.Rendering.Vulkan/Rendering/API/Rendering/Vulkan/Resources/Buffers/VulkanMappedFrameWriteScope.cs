namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded exclusive host-write access to one mapped frame slice.
/// </summary>
internal ref struct VulkanMappedFrameWriteScope
{
    private VulkanMappedFrameArena? _arena;
    private readonly VulkanMappedFrameSlice _slice;

    internal VulkanMappedFrameWriteScope(
        VulkanMappedFrameArena arena,
        VulkanMappedFrameSlice slice,
        Span<byte> bytes)
    {
        _arena = arena;
        _slice = slice;
        Bytes = bytes;
    }

    internal Span<byte> Bytes { get; }

    public void Dispose()
    {
        VulkanMappedFrameArena? arena = _arena;
        if (arena is null)
            return;

        _arena = null;
        arena.EndWrite(_slice);
    }
}
