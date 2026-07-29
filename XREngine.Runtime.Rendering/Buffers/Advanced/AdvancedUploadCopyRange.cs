namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral copy operation produced from one coalesced dirty upload range.
/// Backends translate the storage generation and frame slot into their mapped buffer,
/// staging buffer, or persistent SSBO binding.
/// </summary>
public readonly record struct AdvancedUploadCopyRange(
    EAdvancedFrameUploadStream Stream,
    ulong StorageGeneration,
    uint FrameSlot,
    uint SourceOffsetBytes,
    uint DestinationOffsetBytes,
    uint ByteCount,
    bool IsOverflow);
