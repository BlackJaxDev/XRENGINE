namespace XREngine.Rendering.Commands;

/// <summary>
/// Immutable view of one append-only geometry arena captured at publication seal.
/// It retains the exact backing array, byte extent, and generation so a later arena
/// growth or reset cannot turn an old publication reference into a new resource.
/// </summary>
public readonly struct AdvancedImmutableByteArenaPublicationSnapshot
{
    private readonly byte[]? _data;

    internal AdvancedImmutableByteArenaPublicationSnapshot(
        byte[] data,
        AdvancedGpuHandle bufferHandle,
        uint byteCount,
        AdvancedGpuDirtyRange dirtyByteRange)
    {
        _data = data;
        BufferHandle = bufferHandle;
        ByteCount = byteCount;
        DirtyByteRange = dirtyByteRange;
    }

    public AdvancedGpuHandle BufferHandle { get; }

    public uint ByteCount { get; }

    /// <summary>
    /// Exact byte interval appended or structurally rewritten since the prior
    /// publication capture.  The retained backing image remains immutable for
    /// the captured prefix, so Vulkan may patch only this interval.
    /// </summary>
    public AdvancedGpuDirtyRange DirtyByteRange { get; }

    public bool IsValid => _data is not null && BufferHandle.IsValid;

    public ReadOnlySpan<byte> Data
        => _data is null ? [] : _data.AsSpan(0, checked((int)ByteCount));
}
