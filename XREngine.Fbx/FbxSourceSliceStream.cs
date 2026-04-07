using System.IO;

namespace XREngine.Fbx;

internal sealed class FbxSourceSliceStream(FbxStructuralDocument document, int offset, int length) : Stream
{
    private readonly FbxStructuralDocument _document = document;
    private readonly int _offset = offset;
    private readonly int _length = length;
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > _length)
                throw new ArgumentOutOfRangeException(nameof(value));

            _position = checked((int)value);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > buffer.Length)
            throw new ArgumentException("The buffer segment is out of range.", nameof(count));

        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        int remaining = _length - _position;
        if (remaining <= 0)
            return 0;

        int bytesToRead = Math.Min(buffer.Length, remaining);
        _document.Source.Slice(_offset + _position, bytesToRead).CopyTo(buffer);
        _position += bytesToRead;
        return bytesToRead;
    }

    public override int ReadByte()
    {
        if (_position >= _length)
            return -1;

        return _document.Source[_offset + _position++];
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0 || target > _length)
            throw new IOException("Attempted to seek outside the FBX source slice.");

        _position = checked((int)target);
        return _position;
    }

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    public override void Flush()
    {
    }
}
