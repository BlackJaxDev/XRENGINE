using System.Buffers.Binary;
using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Reuses a bounded binary draw arena. The consumer synchronously imports the sealed bytes and
/// retains neither the managed array nor a view of it after <see cref="EndConsume"/>.
/// </summary>
public sealed class BrowserFramePacket
{
    public const int HeaderBytes = 64;
    public const int DrawBytes = 112;
    public const int MaximumDraws = 4096;

    private byte[] _bytes;
    private BrowserFramePacketState _state;
    private uint _frameSequence;
    private int _drawCount;
    private int _sessionId;
    private int _surfaceGeneration;

    public BrowserFramePacket(int initialDrawCapacity = 64)
    {
        if (initialDrawCapacity is < 1 or > MaximumDraws)
            throw new ArgumentOutOfRangeException(nameof(initialDrawCapacity));

        _bytes = new byte[HeaderBytes + initialDrawCapacity * DrawBytes];
        DrawCapacity = initialDrawCapacity;
        ArenaGeneration = 1;
    }

    public int ArenaGeneration { get; private set; }

    public int DrawCapacity { get; private set; }

    public int GrowthCount { get; private set; }

    public int DrawCount => _drawCount;

    public uint FrameSequence => _frameSequence;

    /// <summary>The sealed payload, borrowed only through the synchronous import window.</summary>
    public Span<byte> WrittenBytes
    {
        get
        {
            if (_state is not (BrowserFramePacketState.Sealed or BrowserFramePacketState.Consuming))
                throw new InvalidOperationException("The packet must be sealed before its bytes can be read.");
            return _bytes.AsSpan(0, HeaderBytes + _drawCount * DrawBytes);
        }
    }

    /// <summary>Grows the arena at an idle boundary; no browser view may span this operation.</summary>
    public void EnsureDrawCapacity(int required)
    {
        if (_state != BrowserFramePacketState.Idle)
            throw new InvalidOperationException("The draw arena can grow only while idle.");
        if (required is < 0 or > MaximumDraws)
            throw new ArgumentOutOfRangeException(nameof(required));
        if (required <= DrawCapacity)
            return;
        if (ArenaGeneration == int.MaxValue || GrowthCount == int.MaxValue)
            throw new InvalidOperationException("The draw arena generation is exhausted.");

        int capacity = DrawCapacity;
        while (capacity < required)
            capacity = Math.Min(capacity * 2, MaximumDraws);

        _bytes = new byte[HeaderBytes + capacity * DrawBytes];
        DrawCapacity = capacity;
        ArenaGeneration++;
        GrowthCount++;
    }

    /// <summary>Begins a frame with a fresh sequence; a device recreation requires a fresh session ID.</summary>
    public void Begin(int sessionId, int surfaceGeneration)
    {
        if (_state != BrowserFramePacketState.Idle)
            throw new InvalidOperationException("The preceding packet has not completed consumption.");
        if (sessionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        if (surfaceGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(surfaceGeneration));
        if (_frameSequence == uint.MaxValue)
            throw new InvalidOperationException("The frame sequence is exhausted; start a fresh scene session.");

        _frameSequence++;
        _sessionId = sessionId;
        _surfaceGeneration = surfaceGeneration;
        _drawCount = 0;
        _bytes.AsSpan(0, HeaderBytes).Clear();
        _state = BrowserFramePacketState.Writing;
    }

    /// <summary>Appends one indexed draw with a finite row-major transform.</summary>
    public void AddDraw(
        BrowserResourceHandle mesh, BrowserResourceHandle material,
        int x, int y, int width, int height, int firstIndex, int indexCount,
        in Matrix4x4 matrix)
    {
        if (_state != BrowserFramePacketState.Writing)
            throw new InvalidOperationException("A packet must be writing to accept draws.");

        if (_drawCount >= DrawCapacity || _drawCount >= MaximumDraws)
        {
            _state = BrowserFramePacketState.Faulted;
            throw new InvalidOperationException($"Draw capacity {DrawCapacity} exceeded; discard the packet and grow the arena while idle.");
        }

        if (mesh.Slot == 0 || mesh.Generation is < 1 or > 32767 ||
            material.Slot == 0 || material.Generation is < 1 or > 32767 ||
            x < 0 || y < 0 || width <= 0 || height <= 0 ||
            firstIndex < 0 || indexCount <= 0 ||
            (long)x + width > int.MaxValue || (long)y + height > int.MaxValue ||
            !IsFinite(in matrix))
        {
            _state = BrowserFramePacketState.Faulted;
            throw new ArgumentOutOfRangeException(nameof(matrix), "Draw handles, rectangle, index range, and matrix must be valid and finite; discard this packet.");
        }

        Span<byte> draw = _bytes.AsSpan(HeaderBytes + _drawCount * DrawBytes, DrawBytes);
        Write32(draw, 0, 1);
        Write32(draw, 4, DrawBytes);
        Write32(draw, 8, mesh.Slot);
        Write32(draw, 12, mesh.Generation);
        Write32(draw, 16, material.Slot);
        Write32(draw, 20, material.Generation);
        Write32(draw, 24, x);
        Write32(draw, 28, y);
        Write32(draw, 32, width);
        Write32(draw, 36, height);
        Write32(draw, 40, firstIndex);
        Write32(draw, 44, indexCount);
        BinaryPrimitives.WriteSingleLittleEndian(draw.Slice(48), matrix.M11);
        BinaryPrimitives.WriteSingleLittleEndian(draw.Slice(52), matrix.M12);
        BinaryPrimitives.WriteSingleLittleEndian(draw.Slice(56), matrix.M13);
        BinaryPrimitives.WriteSingleLittleEndian(draw.Slice(60), matrix.M14);
        BinaryPrimitives.WriteSingleLittleEndian(draw.Slice(64), matrix.M21);
        BinaryPrimitives.WriteSingleLittleEndian(draw.Slice(68), matrix.M22);
        BinaryPrimitives.WriteSingleLittleEndian(draw.Slice(72), matrix.M23);
        BinaryPrimitives.WriteSingleLittleEndian(draw.Slice(76), matrix.M24);
        BinaryPrimitives.WriteSingleLittleEndian(draw.Slice(80), matrix.M31);
        BinaryPrimitives.WriteSingleLittleEndian(draw.Slice(84), matrix.M32);
        BinaryPrimitives.WriteSingleLittleEndian(draw.Slice(88), matrix.M33);
        BinaryPrimitives.WriteSingleLittleEndian(draw.Slice(92), matrix.M34);
        BinaryPrimitives.WriteSingleLittleEndian(draw.Slice(96), matrix.M41);
        BinaryPrimitives.WriteSingleLittleEndian(draw.Slice(100), matrix.M42);
        BinaryPrimitives.WriteSingleLittleEndian(draw.Slice(104), matrix.M43);
        BinaryPrimitives.WriteSingleLittleEndian(draw.Slice(108), matrix.M44);
        _drawCount++;
    }

    public void Seal()
    {
        if (_state != BrowserFramePacketState.Writing)
            throw new InvalidOperationException("Only a complete writing packet may be sealed.");

        Span<byte> header = _bytes.AsSpan(0, HeaderBytes);
        Write32(header, 0, 0x50524558);
        Write32(header, 4, 1);
        Write32(header, 8, 0x55504757);
        Write32(header, 12, HeaderBytes);
        Write32(header, 16, HeaderBytes + _drawCount * DrawBytes);
        Write32(header, 20, _drawCount);
        Write32(header, 24, _sessionId);
        Write32(header, 28, _sessionId);
        Write32(header, 32, _surfaceGeneration);
        Write32(header, 36, ArenaGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(40), _frameSequence);
        _state = BrowserFramePacketState.Sealed;
    }

    /// <summary>Hands the sealed bytes to a synchronous importer; the returned span must not be retained.</summary>
    public Span<byte> BeginConsume()
    {
        if (_state != BrowserFramePacketState.Sealed)
            throw new InvalidOperationException("Only a sealed packet can be consumed.");
        _state = BrowserFramePacketState.Consuming;
        return _bytes.AsSpan(0, HeaderBytes + _drawCount * DrawBytes);
    }

    public void EndConsume()
    {
        if (_state != BrowserFramePacketState.Consuming)
            throw new InvalidOperationException("No packet is being consumed.");
        _state = BrowserFramePacketState.Idle;
    }

    /// <summary>Discards a partial or failed packet without reusing its frame sequence.</summary>
    public void Abort()
    {
        _state = BrowserFramePacketState.Idle;
        _drawCount = 0;
    }

    private static void Write32(Span<byte> destination, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset), value);

    private static bool IsFinite(in Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
