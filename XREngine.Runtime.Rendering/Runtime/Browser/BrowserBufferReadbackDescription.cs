namespace XREngine.Rendering;

/// <summary>A bounded, four-byte-aligned snapshot of a GPU buffer with copy-source usage.</summary>
public readonly record struct BrowserBufferReadbackDescription(int BufferHandle, int Offset, int ByteLength)
{
    public const int MaximumByteLength = 16 * 1024 * 1024;

    public void Validate()
    {
        _ = BrowserResourceHandle.FromPacked(BufferHandle);
        if (Offset < 0 || (Offset & 3) != 0 || ByteLength <= 0 ||
            (ByteLength & 3) != 0 || ByteLength > MaximumByteLength)
            throw new ArgumentOutOfRangeException(nameof(ByteLength), "Readback requires an aligned positive range within the bounded byte budget.");
    }
}
