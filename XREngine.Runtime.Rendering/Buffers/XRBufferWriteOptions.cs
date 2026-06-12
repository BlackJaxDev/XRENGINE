namespace XREngine.Rendering;

/// <summary>
/// Options for scoped <see cref="XRDataBuffer"/> writes. Defaults are usually supplied by the buffer.
/// </summary>
public readonly record struct XRBufferWriteOptions
{
    public XRBufferMemoryPolicy MemoryPolicy { get; init; }
    public XRBufferWriteMode WriteMode { get; init; }
    public XRBufferCpuAccess CpuAccess { get; init; }
    public XRBufferWriterDisposeBehavior DisposeBehavior { get; init; }
    public bool KeepCpuMirror { get; init; }
    public bool ClearOnAllocate { get; init; }
    public bool GrowCapacityGeometrically { get; init; }
    public bool AllowStagingCopy { get; init; }
    public uint AlignmentBytes { get; init; }

    public static XRBufferWriteOptions FromBuffer(XRDataBuffer buffer)
        => new()
        {
            MemoryPolicy = buffer.DefaultMemoryPolicy,
            WriteMode = buffer.DefaultWriteMode,
            CpuAccess = buffer.DefaultCpuAccess,
            DisposeBehavior = buffer.DefaultWriterDisposeBehavior,
            KeepCpuMirror = buffer.DefaultKeepCpuMirror,
            ClearOnAllocate = false,
            GrowCapacityGeometrically = true,
            AllowStagingCopy = true,
            AlignmentBytes = buffer.DefaultAlignmentBytes,
        };

    public XRBufferWriteOptions WithWriteMode(XRBufferWriteMode mode)
        => new()
        {
            MemoryPolicy = MemoryPolicy,
            WriteMode = mode,
            CpuAccess = CpuAccess,
            DisposeBehavior = DisposeBehavior,
            KeepCpuMirror = KeepCpuMirror,
            ClearOnAllocate = ClearOnAllocate,
            GrowCapacityGeometrically = GrowCapacityGeometrically,
            AllowStagingCopy = AllowStagingCopy,
            AlignmentBytes = AlignmentBytes,
        };
}

public readonly record struct XRBufferDirtyRange(uint OffsetBytes, uint LengthBytes)
{
    public uint EndBytes => OffsetBytes + LengthBytes;

    public bool TouchesOrOverlaps(XRBufferDirtyRange other)
        => OffsetBytes <= other.EndBytes && other.OffsetBytes <= EndBytes;

    public XRBufferDirtyRange Merge(XRBufferDirtyRange other)
    {
        uint start = Math.Min(OffsetBytes, other.OffsetBytes);
        uint end = Math.Max(EndBytes, other.EndBytes);
        return new XRBufferDirtyRange(start, end - start);
    }

    public override string ToString() => $"{OffsetBytes}+{LengthBytes}";
}

public readonly record struct XRBufferStateSnapshot(
    bool IsApiObjectGenerated,
    ulong AllocatedByteSize,
    ulong UploadedByteCount,
    ulong Revision,
    ulong UploadedRevision,
    bool HasPendingUpload,
    XRBufferMemoryPolicy MemoryPolicy,
    XRBufferResolvedRoute ResolvedRoute,
    bool IsPersistentlyMapped,
    bool HasCpuMirror,
    bool HasDeviceAddress,
    string DeviceAddressDowngradeReason,
    bool IsDescriptorBindingReady,
    bool IsReadyForGpuUse,
    int DirtyRangeCount);
