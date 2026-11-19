namespace XREngine.UnityScriptHost.Admission;

/// <summary>Bounds a serialized inventory before bytes reach its temporary file.</summary>
internal sealed class UnityInventoryOutputStream(Stream destination) : Stream
{
    public const long MaxBytes = 64 * 1024 * 1024;
    private long _written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _written;
    public override long Position { get => _written; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > MaxBytes - _written)
            throw new InvalidDataException("The inventory report exceeds the output byte limit.");
        destination.Write(buffer);
        _written += buffer.Length;
    }

    public override void Flush() => destination.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
