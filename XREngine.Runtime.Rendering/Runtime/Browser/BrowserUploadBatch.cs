using System.Buffers.Binary;
using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Reuses bounded command and payload arenas for browser resource updates. A renderer may borrow
/// mutable spans only between <see cref="BeginConsume"/> and <see cref="EndConsume"/> and must copy
/// both arenas synchronously before ending that window.
/// </summary>
public sealed class BrowserUploadBatch : IDisposable
{
    public const int HeaderBytes = 64;
    public const int CommandBytes = 48;
    public const int MaximumCommands = 4096;
    public const int MaximumPayloadBytes = 32 * 1024 * 1024;

    private byte[] _commands;
    private byte[] _payload;
    private BrowserUploadBatchState _state;
    private uint _sequence;
    private int _sessionId;
    private int _commandCount;
    private int _payloadUsed;

    public BrowserUploadBatch(int initialCommandCapacity = 64, int initialPayloadCapacity = 64 * 1024)
    {
        if (initialCommandCapacity is < 1 or > MaximumCommands)
            throw new ArgumentOutOfRangeException(nameof(initialCommandCapacity));
        if (initialPayloadCapacity is < 1 or > MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(initialPayloadCapacity));

        _commands = new byte[HeaderBytes + initialCommandCapacity * CommandBytes];
        _payload = new byte[initialPayloadCapacity];
        CommandCapacity = initialCommandCapacity;
        PayloadCapacity = initialPayloadCapacity;
        CommandArenaGeneration = 1;
        PayloadArenaGeneration = 1;
    }

    public int CommandCapacity { get; private set; }
    public int PayloadCapacity { get; private set; }
    public int CommandArenaGeneration { get; private set; }
    public int PayloadArenaGeneration { get; private set; }
    public int CommandGrowthCount { get; private set; }
    public int PayloadGrowthCount { get; private set; }
    public int CommandCount => _commandCount;
    public int PayloadUsed => _payloadUsed;
    public uint Sequence => _sequence;

    /// <summary>The sealed command header and records; the view expires when consumption ends or the batch is aborted.</summary>
    public ReadOnlySpan<byte> WrittenCommands
    {
        get
        {
            RequireReadable();
            return _commands.AsSpan(0, HeaderBytes + _commandCount * CommandBytes);
        }
    }

    /// <summary>The sealed payload; the view expires when consumption ends or the batch is aborted.</summary>
    public ReadOnlySpan<byte> WrittenPayload
    {
        get
        {
            RequireReadable();
            return _payload.AsSpan(0, _payloadUsed);
        }
    }

    /// <summary>Resizes either arena only while idle; existing browser views must have expired.</summary>
    public void EnsureCapacity(int commands, int payloadBytes)
    {
        if (_state != BrowserUploadBatchState.Idle)
            throw new InvalidOperationException("Upload arenas can grow only while idle.");
        if (commands < 0)
            throw new ArgumentOutOfRangeException(nameof(commands));
        if (payloadBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(payloadBytes));
        if (commands > MaximumCommands)
            throw new BrowserArenaCapacityException("command", commands, CommandCapacity, MaximumCommands);
        if (payloadBytes > MaximumPayloadBytes)
            throw new BrowserArenaCapacityException("payload", payloadBytes, PayloadCapacity, MaximumPayloadBytes);

        bool growCommands = commands > CommandCapacity;
        bool growPayload = payloadBytes > PayloadCapacity;
        if (growCommands && (CommandArenaGeneration == int.MaxValue || CommandGrowthCount == int.MaxValue) ||
            growPayload && (PayloadArenaGeneration == int.MaxValue || PayloadGrowthCount == int.MaxValue))
            throw new InvalidOperationException("The upload arena generation is exhausted.");

        if (growCommands)
        {
            int capacity = Grow(CommandCapacity, commands, MaximumCommands);
            _commands = new byte[HeaderBytes + capacity * CommandBytes];
            CommandCapacity = capacity;
            CommandArenaGeneration++;
            CommandGrowthCount++;
        }
        if (growPayload)
        {
            int capacity = Grow(PayloadCapacity, payloadBytes, MaximumPayloadBytes);
            _payload = new byte[capacity];
            PayloadCapacity = capacity;
            PayloadArenaGeneration++;
            PayloadGrowthCount++;
        }
    }

    /// <summary>Starts a batch in the supplied live renderer session.</summary>
    public void Begin(int sessionId)
    {
        if (_state != BrowserUploadBatchState.Idle)
            throw new InvalidOperationException("The preceding upload batch has not completed consumption.");
        if (sessionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        if (_sequence == uint.MaxValue)
            throw new InvalidOperationException("The upload sequence is exhausted.");

        _sequence++;
        _sessionId = sessionId;
        _commandCount = 0;
        _payloadUsed = 0;
        _commands.AsSpan(0, HeaderBytes).Clear();
        _state = BrowserUploadBatchState.Writing;
    }

    /// <summary>Updates packed position.xyz/UV.xy vertices starting at a vertex index.</summary>
    public void AddMeshVertices(BrowserResourceHandle handle, int firstVertex, ReadOnlySpan<float> posuv)
    {
        RequireWriting();
        if (!ValidHandle(handle) || firstVertex < 0 || posuv.IsEmpty || posuv.Length % 5 != 0 ||
            (long)firstVertex * 20 > int.MaxValue || (long)posuv.Length * 4 > int.MaxValue)
            FailArgument(nameof(posuv), "A mesh vertex update requires a valid handle and a nonempty, aligned vertex range.");
        for (int i = 0; i < posuv.Length; i++)
            if (!float.IsFinite(posuv[i]))
                FailArgument(nameof(posuv), "Mesh coordinates and UVs must be finite.");

        int length = posuv.Length * sizeof(float);
        Span<byte> payload = Append(1, handle, firstVertex * 20, 0, 0, 0, length);
        for (int i = 0; i < posuv.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(payload.Slice(i * sizeof(float)), posuv[i]);
    }

    /// <summary>Updates uint32 triangle indices starting at an index offset.</summary>
    public void AddMeshIndices(BrowserResourceHandle handle, int firstIndex, ReadOnlySpan<uint> indices)
    {
        RequireWriting();
        if (!ValidHandle(handle) || firstIndex < 0 || indices.IsEmpty ||
            (long)firstIndex * 4 > int.MaxValue || (long)indices.Length * 4 > int.MaxValue)
            FailArgument(nameof(indices), "A mesh index update requires a valid handle and a nonempty index range.");

        int length = indices.Length * sizeof(uint);
        Span<byte> payload = Append(2, handle, firstIndex * sizeof(uint), 0, 0, 0, length);
        for (int i = 0; i < indices.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(payload.Slice(i * sizeof(uint)), indices[i]);
    }

    /// <summary>Updates a tightly packed RGBA8 texture rectangle.</summary>
    public void AddTexture(BrowserResourceHandle handle, int x, int y, int width, int height, ReadOnlySpan<byte> rgba)
    {
        RequireWriting();
        if (!ValidHandle(handle) || x < 0 || y < 0 || width <= 0 || height <= 0 ||
            (long)x + width > int.MaxValue || (long)y + height > int.MaxValue ||
            rgba.Length % 4 != 0 || (long)width * height != rgba.Length / 4)
            FailArgument(nameof(rgba), "A texture update requires a valid handle, rectangle, and tightly packed RGBA8 pixels.");

        rgba.CopyTo(Append(3, handle, x, y, width, height, rgba.Length));
    }

    /// <summary>Updates the linear normalized tint of an opaque material.</summary>
    public void AddMaterialTint(BrowserResourceHandle handle, Vector4 tint)
    {
        RequireWriting();
        if (!ValidHandle(handle) || !float.IsFinite(tint.X) || !float.IsFinite(tint.Y) ||
            !float.IsFinite(tint.Z) || tint.X is < 0 or > 1 || tint.Y is < 0 or > 1 ||
            tint.Z is < 0 or > 1 || tint.W != 1.0f)
            FailArgument(nameof(tint), "A material tint requires a valid handle and finite normalized RGB with opaque alpha.");

        Span<byte> payload = Append(4, handle, 0, 0, 0, 0, 16);
        BinaryPrimitives.WriteSingleLittleEndian(payload, tint.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.Slice(4), tint.Y);
        BinaryPrimitives.WriteSingleLittleEndian(payload.Slice(8), tint.Z);
        BinaryPrimitives.WriteSingleLittleEndian(payload.Slice(12), tint.W);
    }

    public void Seal()
    {
        if (_state != BrowserUploadBatchState.Writing)
            throw new InvalidOperationException("Only a writing upload batch may be sealed.");

        Span<byte> header = _commands.AsSpan(0, HeaderBytes);
        Write32(header, 0, 0x55524558);
        Write32(header, 4, 1);
        Write32(header, 8, 0x55504757);
        Write32(header, 12, HeaderBytes);
        Write32(header, 16, HeaderBytes + _commandCount * CommandBytes);
        Write32(header, 20, _commandCount);
        Write32(header, 24, _sessionId);
        Write32(header, 28, _sessionId);
        Write32(header, 32, CommandArenaGeneration);
        Write32(header, 36, PayloadArenaGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(40), _sequence);
        Write32(header, 44, _payloadUsed);
        _state = BrowserUploadBatchState.Sealed;
    }

    /// <summary>Borrows both mutable arenas for one synchronous browser import.</summary>
    public void BeginConsume(out Span<byte> commands, out Span<byte> payload)
    {
        if (_state != BrowserUploadBatchState.Sealed)
            throw new InvalidOperationException("Only a sealed upload batch can be consumed.");
        _state = BrowserUploadBatchState.Consuming;
        commands = _commands.AsSpan(0, HeaderBytes + _commandCount * CommandBytes);
        payload = _payload.AsSpan(0, _payloadUsed);
    }

    public void EndConsume()
    {
        if (_state != BrowserUploadBatchState.Consuming)
            throw new InvalidOperationException("No upload batch is being consumed.");
        _state = BrowserUploadBatchState.Idle;
    }

    /// <summary>Discards a partial, sealed, or failed batch without reusing its sequence.</summary>
    public void Abort()
    {
        if (_state is BrowserUploadBatchState.Consuming or BrowserUploadBatchState.Disposed)
            throw new InvalidOperationException("A consuming or disposed upload batch cannot be aborted.");
        _commandCount = 0;
        _payloadUsed = 0;
        _state = BrowserUploadBatchState.Idle;
    }

    public void Dispose()
    {
        if (_state == BrowserUploadBatchState.Consuming)
            throw new InvalidOperationException("The upload batch cannot be disposed during synchronous consumption.");
        if (_state == BrowserUploadBatchState.Disposed)
            return;
        _commands = [];
        _payload = [];
        _commandCount = 0;
        _payloadUsed = 0;
        _state = BrowserUploadBatchState.Disposed;
    }

    private Span<byte> Append(int opcode, BrowserResourceHandle handle, int destination, int y, int width, int height, int length)
    {
        long requiredPayload = (long)_payloadUsed + length;
        if (_commandCount >= CommandCapacity || _commandCount >= MaximumCommands)
        {
            _state = BrowserUploadBatchState.Faulted;
            throw new BrowserArenaCapacityException("command", _commandCount + 1, CommandCapacity, MaximumCommands);
        }
        if (requiredPayload > PayloadCapacity || requiredPayload > MaximumPayloadBytes)
        {
            _state = BrowserUploadBatchState.Faulted;
            throw new BrowserArenaCapacityException("payload", (int)Math.Min(requiredPayload, int.MaxValue), PayloadCapacity, MaximumPayloadBytes);
        }

        Span<byte> command = _commands.AsSpan(HeaderBytes + _commandCount * CommandBytes, CommandBytes);
        command.Clear();
        Write32(command, 0, opcode);
        Write32(command, 4, CommandBytes);
        Write32(command, 8, handle.Slot);
        Write32(command, 12, handle.Generation);
        Write32(command, 16, destination);
        Write32(command, 20, y);
        Write32(command, 24, width);
        Write32(command, 28, height);
        Write32(command, 32, _payloadUsed);
        Write32(command, 36, length);
        Span<byte> payload = _payload.AsSpan(_payloadUsed, length);
        _payloadUsed += length;
        _commandCount++;
        return payload;
    }

    private void RequireWriting()
    {
        if (_state != BrowserUploadBatchState.Writing)
            throw new InvalidOperationException("An upload batch must be writing to accept commands.");
    }

    private void RequireReadable()
    {
        if (_state != BrowserUploadBatchState.Sealed)
            throw new InvalidOperationException("Only a sealed upload batch exposes read-only byte views.");
    }

    private void FailArgument(string name, string message)
    {
        _state = BrowserUploadBatchState.Faulted;
        throw new ArgumentException($"{message} Abort this batch before reuse.", name);
    }

    private static bool ValidHandle(BrowserResourceHandle handle) =>
        handle.Slot != 0 && handle.Generation is >= 1 and <= 32767;

    private static int Grow(int capacity, int required, int maximum)
    {
        while (capacity < required)
            capacity = Math.Min(capacity * 2, maximum);
        return capacity;
    }

    private static void Write32(Span<byte> destination, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset), value);
}
