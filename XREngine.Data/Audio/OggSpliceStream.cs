namespace XREngine.Data;

/// <summary>
/// A read-only stream that gives the bytes of a prefix buffer first and then the bytes of a source stream from an offset.
/// The Vorbis page index uses it to give NVorbis the stream headers followed by the pages from a restart page on.
/// The stream does not own the prefix or the source, and its dispose does not close the source.
/// </summary>
internal sealed class OggSpliceStream : Stream
{
    private byte[] _prefix = [];
    private int _prefixLength;
    private Stream? _source;
    private long _sourceStart;
    private long _position;

    /// <summary>Sets the parts of the stream and moves the position to its start.</summary>
    /// <param name="prefix">The buffer that holds the first bytes.</param>
    /// <param name="prefixLength">The number of bytes of <paramref name="prefix"/> to use.</param>
    /// <param name="source">The stream that gives the bytes after the prefix.</param>
    /// <param name="sourceStart">The offset in <paramref name="source"/> of the first byte after the prefix.</param>
    public void Reset(byte[] prefix, int prefixLength, Stream source, long sourceStart)
    {
        _prefix = prefix;
        _prefixLength = prefixLength;
        _source = source;
        _sourceStart = sourceStart;
        _position = 0;
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => true;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => _prefixLength + Math.Max(0L, (_source?.Length ?? 0L) - _sourceStart);

    /// <inheritdoc/>
    public override long Position
    {
        get => _position;
        set => _position = Math.Max(0L, value);
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;

        if (_position < _prefixLength)
        {
            int count = (int)Math.Min(buffer.Length, _prefixLength - _position);
            _prefix.AsSpan((int)_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        if (_source is null)
            return 0;
        _source.Position = _sourceStart + (_position - _prefixLength);
        int read = _source.Read(buffer);
        _position += read;
        return read;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        long start = origin switch
        {
            SeekOrigin.Begin => 0L,
            SeekOrigin.Current => _position,
            _ => Length,
        };
        Position = start + offset;
        return _position;
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
        => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();
}
