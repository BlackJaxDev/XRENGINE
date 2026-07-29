namespace XREngine.Rendering;

/// <summary>
/// Writable allocation in one stable frame-slot storage generation.
/// </summary>
public readonly struct AdvancedFrameUploadAllocation
{
    internal AdvancedFrameUploadAllocation(
        Memory<byte> memory,
        EAdvancedFrameUploadStream stream,
        ulong storageGeneration,
        uint frameSlot,
        uint byteOffset,
        uint byteCount,
        bool isOverflow)
    {
        Memory = memory;
        Stream = stream;
        StorageGeneration = storageGeneration;
        FrameSlot = frameSlot;
        ByteOffset = byteOffset;
        ByteCount = byteCount;
        IsOverflow = isOverflow;
    }

    public Memory<byte> Memory { get; }
    public Span<byte> Span => Memory.Span;
    public EAdvancedFrameUploadStream Stream { get; }
    public ulong StorageGeneration { get; }
    public uint FrameSlot { get; }
    public uint ByteOffset { get; }
    public uint ByteCount { get; }
    public bool IsOverflow { get; }
}
