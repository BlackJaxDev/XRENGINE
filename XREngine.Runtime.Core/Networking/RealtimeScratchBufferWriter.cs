using System.Buffers;

namespace XREngine.Networking;

/// <summary>
/// Reusable growable buffer for composing realtime frames. Unlike <see cref="ArrayBufferWriter{T}"/>
/// it exposes the written bytes as writable, so a header can be reserved first and patched after
/// the payload length is known. One instance is reused per thread; it never shrinks.
/// </summary>
public sealed class RealtimeScratchBufferWriter : IBufferWriter<byte>
{
    private byte[] _buffer;
    private int _written;

    public RealtimeScratchBufferWriter(int initialCapacity = 1024)
        => _buffer = new byte[Math.Max(64, initialCapacity)];

    public int WrittenCount => _written;
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    /// <summary>The written bytes as writable, for patching a reserved header.</summary>
    public Span<byte> WrittenSpanMutable => _buffer.AsSpan(0, _written);

    /// <summary>Clears the written count while retaining capacity.</summary>
    public void Reset()
        => _written = 0;

    public void Advance(int count)
    {
        if (count < 0 || _written + count > _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    /// <summary>Reserves <paramref name="count"/> zeroed bytes and returns them for later patching.</summary>
    public Span<byte> Reserve(int count)
    {
        Span<byte> span = GetSpan(count)[..count];
        span.Clear();
        _written += count;
        return span;
    }

    private void EnsureCapacity(int sizeHint)
    {
        int required = _written + Math.Max(1, sizeHint);
        if (required <= _buffer.Length)
            return;

        int capacity = _buffer.Length;
        while (capacity < required)
            capacity = checked(capacity * 2);
        Array.Resize(ref _buffer, capacity);
    }
}
