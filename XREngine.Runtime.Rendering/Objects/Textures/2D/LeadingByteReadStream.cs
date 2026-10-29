namespace XREngine.Rendering;

/// <summary>Reads one consumed byte before the remaining stream. The caller owns the source.</summary>
internal sealed class LeadingByteReadStream(Stream source, byte firstByte) : Stream
{
    private bool _firstPending = true;

    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;
        if (!_firstPending)
            return source.Read(buffer);
        _firstPending = false;
        buffer[0] = firstByte;
        return 1;
    }

    public override int ReadByte()
    {
        if (!_firstPending)
            return source.ReadByte();
        _firstPending = false;
        return firstByte;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
