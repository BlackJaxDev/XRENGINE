namespace XREngine.Rendering;

internal readonly record struct AdvancedFrameUploadDirtyRange(
    uint OffsetBytes,
    uint ByteCount)
{
    public ulong EndBytes => (ulong)OffsetBytes + ByteCount;
}
