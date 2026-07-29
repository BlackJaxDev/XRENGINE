namespace XREngine.Rendering;

/// <summary>
/// Per-frame-slot byte capacity reserved for each advanced upload stream.
/// </summary>
public readonly record struct AdvancedFrameUploadCapacityProfile(
    uint InstanceBytes,
    uint ViewBytes,
    uint DeformationJobBytes,
    uint LightBytes,
    uint MaterialBytes)
{
    public const int StreamCount = 5;

    public ulong TotalBytesPerSlot =>
        (ulong)InstanceBytes +
        ViewBytes +
        DeformationJobBytes +
        LightBytes +
        MaterialBytes;

    public uint Get(EAdvancedFrameUploadStream stream)
        => stream switch
        {
            EAdvancedFrameUploadStream.Instance => InstanceBytes,
            EAdvancedFrameUploadStream.View => ViewBytes,
            EAdvancedFrameUploadStream.DeformationJob => DeformationJobBytes,
            EAdvancedFrameUploadStream.Light => LightBytes,
            EAdvancedFrameUploadStream.Material => MaterialBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(stream), stream, null),
        };

    public AdvancedFrameUploadCapacityProfile With(
        EAdvancedFrameUploadStream stream,
        uint byteCapacity)
        => stream switch
        {
            EAdvancedFrameUploadStream.Instance => this with { InstanceBytes = byteCapacity },
            EAdvancedFrameUploadStream.View => this with { ViewBytes = byteCapacity },
            EAdvancedFrameUploadStream.DeformationJob => this with { DeformationJobBytes = byteCapacity },
            EAdvancedFrameUploadStream.Light => this with { LightBytes = byteCapacity },
            EAdvancedFrameUploadStream.Material => this with { MaterialBytes = byteCapacity },
            _ => throw new ArgumentOutOfRangeException(nameof(stream), stream, null),
        };

    public bool AnyGreaterThan(in AdvancedFrameUploadCapacityProfile other)
    {
        for (int i = 0; i < StreamCount; i++)
        {
            EAdvancedFrameUploadStream stream = (EAdvancedFrameUploadStream)i;
            if (Get(stream) > other.Get(stream))
                return true;
        }

        return false;
    }

    public static AdvancedFrameUploadCapacityProfile Max(
        in AdvancedFrameUploadCapacityProfile left,
        in AdvancedFrameUploadCapacityProfile right)
        => new(
            Math.Max(left.InstanceBytes, right.InstanceBytes),
            Math.Max(left.ViewBytes, right.ViewBytes),
            Math.Max(left.DeformationJobBytes, right.DeformationJobBytes),
            Math.Max(left.LightBytes, right.LightBytes),
            Math.Max(left.MaterialBytes, right.MaterialBytes));
}
