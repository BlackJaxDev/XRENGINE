using System.Buffers.Binary;
using System.Numerics;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering;

/// <summary>
/// Reuses a bounded binary draw arena. The consumer synchronously imports the sealed bytes and
/// retains neither the managed array nor a view of it after <see cref="EndConsume"/>.
/// </summary>
public sealed class BrowserFramePacket : IDisposable
{
    public const int HeaderBytes = 96;
    public const int DrawBytes = 112;
    public const int MaximumDraws = 4096;

    private byte[] _bytes;
    private BrowserFramePacketState _state;
    private uint _frameSequence;
    private int _drawCount;
    private int _sessionId;
    private int _surfaceGeneration;
    private BrowserRenderPassDescription _renderPass;
    private int _outputWidth;
    private int _outputHeight;
    private bool _disposed;

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

    /// <summary>The number of draw slots needed by the last capacity fault, or zero otherwise.</summary>
    public int RequiredCapacity { get; private set; }

    public int DrawCount => _drawCount;

    public uint FrameSequence => _frameSequence;

    /// <summary>A read-only view of the sealed payload; do not retain it after consumption ends.</summary>
    public ReadOnlySpan<byte> WrittenBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is not (BrowserFramePacketState.Sealed or BrowserFramePacketState.Consuming))
                throw new InvalidOperationException("The packet must be sealed before its bytes can be read.");
            return _bytes.AsSpan(0, HeaderBytes + _drawCount * DrawBytes);
        }
    }

    /// <summary>Grows the arena at an idle boundary; no browser view may span this operation.</summary>
    public void EnsureDrawCapacity(int required)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != BrowserFramePacketState.Idle)
            throw new InvalidOperationException("The draw arena can grow only while idle.");
        if (required < 0)
            throw new ArgumentOutOfRangeException(nameof(required));
        if (required > MaximumDraws)
        {
            RequiredCapacity = required;
            throw new BrowserArenaCapacityException("draw", required, DrawCapacity, MaximumDraws);
        }
        if (required <= DrawCapacity)
        {
            RequiredCapacity = 0;
            return;
        }
        if (ArenaGeneration == int.MaxValue || GrowthCount == int.MaxValue)
            throw new InvalidOperationException("The draw arena generation is exhausted.");

        int capacity = DrawCapacity;
        while (capacity < required)
            capacity = Math.Min(capacity * 2, MaximumDraws);

        _bytes = new byte[HeaderBytes + capacity * DrawBytes];
        DrawCapacity = capacity;
        ArenaGeneration++;
        GrowthCount++;
        RequiredCapacity = 0;
    }

    /// <summary>Begins a frame with a fresh sequence; a device recreation requires a fresh session ID.</summary>
    public void Begin(int sessionId, int surfaceGeneration, in BrowserRenderPassDescription renderPass, int outputWidth, int outputHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != BrowserFramePacketState.Idle)
            throw new InvalidOperationException("The preceding packet has not completed consumption.");
        if (sessionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        if (surfaceGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(surfaceGeneration));
        renderPass.Validate();
        if (outputWidth <= 0 || outputHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputWidth), "The canvas output extent must be positive.");
        if (_frameSequence == uint.MaxValue)
            throw new InvalidOperationException("The frame sequence is exhausted; start a fresh scene session.");

        _frameSequence++;
        _sessionId = sessionId;
        _surfaceGeneration = surfaceGeneration;
        _renderPass = renderPass;
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;
        _drawCount = 0;
        RequiredCapacity = 0;
        _bytes.AsSpan(0, HeaderBytes).Clear();
        _state = BrowserFramePacketState.Writing;
    }

    /// <summary>Appends one indexed draw with a finite row-major transform.</summary>
    public void AddDraw(
        BrowserResourceHandle mesh, BrowserResourceHandle material,
        int x, int y, int width, int height, int firstIndex, int indexCount,
        in Matrix4x4 matrix)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != BrowserFramePacketState.Writing)
            throw new InvalidOperationException("A packet must be writing to accept draws.");

        if (_drawCount >= DrawCapacity || _drawCount >= MaximumDraws)
        {
            RequiredCapacity = _drawCount + 1;
            _state = BrowserFramePacketState.Faulted;
            throw new BrowserArenaCapacityException("draw", RequiredCapacity, DrawCapacity, MaximumDraws);
        }

        if (mesh.Slot == 0 || mesh.Generation is < 1 or > 32767 ||
            material.Slot == 0 || material.Generation is < 1 or > 32767 ||
            x < 0 || y < 0 || width <= 0 || height <= 0 ||
            (long)x + width > _outputWidth || (long)y + height > _outputHeight ||
            firstIndex < 0 || firstIndex % 3 != 0 || indexCount <= 0 || indexCount % 3 != 0 ||
            (long)firstIndex + indexCount > int.MaxValue ||
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
        BrowserShaderAbi.WriteTransform(draw.Slice(48, BrowserShaderAbi.TransformBytes), in matrix);
        _drawCount++;
    }

    public void Seal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != BrowserFramePacketState.Writing)
            throw new InvalidOperationException("Only a complete writing packet may be sealed.");

        Span<byte> header = _bytes.AsSpan(0, HeaderBytes);
        Write32(header, 0, 0x50524558);
        Write32(header, 4, 2);
        Write32(header, 8, 0x55504757);
        Write32(header, 12, HeaderBytes);
        Write32(header, 16, HeaderBytes + _drawCount * DrawBytes);
        Write32(header, 20, _drawCount);
        Write32(header, 24, _sessionId);
        Write32(header, 28, _sessionId);
        Write32(header, 32, _surfaceGeneration);
        Write32(header, 36, ArenaGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(40), _frameSequence);
        BinaryPrimitives.WriteSingleLittleEndian(header.Slice(64), _renderPass.ClearRed);
        BinaryPrimitives.WriteSingleLittleEndian(header.Slice(68), _renderPass.ClearGreen);
        BinaryPrimitives.WriteSingleLittleEndian(header.Slice(72), _renderPass.ClearBlue);
        BinaryPrimitives.WriteSingleLittleEndian(header.Slice(76), _renderPass.ClearAlpha);
        BinaryPrimitives.WriteSingleLittleEndian(header.Slice(80), _renderPass.ClearDepth);
        Write32(header, 84, _outputWidth);
        Write32(header, 88, _outputHeight);
        _state = BrowserFramePacketState.Sealed;
    }

    /// <summary>Hands the sealed bytes to a synchronous importer. The borrowed span is valid only until
    /// <see cref="EndConsume"/>; the importer must not retain or use it after that call.</summary>
    public Span<byte> BeginConsume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != BrowserFramePacketState.Sealed)
            throw new InvalidOperationException("Only a sealed packet can be consumed.");
        _state = BrowserFramePacketState.Consuming;
        return _bytes.AsSpan(0, HeaderBytes + _drawCount * DrawBytes);
    }

    public void EndConsume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != BrowserFramePacketState.Consuming)
            throw new InvalidOperationException("No packet is being consumed.");
        _state = BrowserFramePacketState.Idle;
    }

    /// <summary>Discards a partial or failed packet without reusing its frame sequence.</summary>
    public void Abort()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state == BrowserFramePacketState.Consuming)
            throw new InvalidOperationException("A packet cannot be aborted while its bytes are borrowed by the consumer.");
        _state = BrowserFramePacketState.Idle;
        _drawCount = 0;
    }

    /// <summary>Releases arena storage after consumption has ended.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        if (_state != BrowserFramePacketState.Idle)
            throw new InvalidOperationException("The packet must be idle before its arena can be released.");
        _bytes = Array.Empty<byte>();
        _disposed = true;
    }

    private static void Write32(Span<byte> destination, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset), value);

    private static bool IsFinite(in Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
