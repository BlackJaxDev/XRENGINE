using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

/// <summary>
/// One slot-owned raw SSBO containing the sealed canonical global tables.
/// The first 32 uvec4 rows describe table offsets, counts, and CPU strides;
/// payload bytes follow at 16-byte aligned absolute offsets.
/// </summary>
internal sealed unsafe class OpenGLAdvancedSceneArena : IDisposable
{
    internal const uint Binding = 0u;
    internal const int TableCount = 32;
    private const int HeaderWords = TableCount * 4;
    private const int HeaderBytes = HeaderWords * sizeof(uint);
    private readonly OpenGLRenderer _renderer;
    private readonly int _maximumBytes;
    private byte[] _bytes = new byte[HeaderBytes];
    private int _usedBytes;
    private uint _buffer;
    private bool _finalized;

    internal OpenGLAdvancedSceneArena(OpenGLRenderer renderer, int maximumBytes)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        if (maximumBytes < HeaderBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
    }

    internal void Begin()
    {
        _usedBytes = HeaderBytes;
        _finalized = false;
        _bytes.AsSpan(0, HeaderBytes).Clear();
        for (uint binding = 0u; binding < TableCount; ++binding)
            WriteHeader(binding, HeaderWords, 0u, 0u);
    }

    internal bool TryWrite<T>(uint binding, ReadOnlySpan<T> values, out string reason)
        where T : unmanaged
    {
        if (binding >= TableCount)
        {
            reason = "The canonical GL scene-table binding is outside the packed arena header.";
            return false;
        }

        int strideBytes = Unsafe.SizeOf<T>();
        if (strideBytes % sizeof(uint) != 0)
        {
            reason = "Packed OpenGL scene records must have a whole-word stride.";
            return false;
        }
        uint strideWords = checked((uint)(strideBytes / sizeof(uint)));
        int offset = Align16(_usedBytes);
        int byteCount;
        try { byteCount = checked(values.Length * strideBytes); }
        catch (OverflowException)
        {
            reason = "The canonical GL scene-table payload size overflows the packed arena.";
            return false;
        }
        int end;
        try { end = checked(offset + byteCount); }
        catch (OverflowException)
        {
            reason = "The canonical GL scene-table payload end overflows the packed arena.";
            return false;
        }
        if (end > _maximumBytes || !EnsureCapacity(end))
        {
            reason = $"The canonical GL scene arena requires {end} bytes, exceeding GL_MAX_SHADER_STORAGE_BLOCK_SIZE ({_maximumBytes}).";
            return false;
        }

        if (!values.IsEmpty)
            MemoryMarshal.AsBytes(values).CopyTo(_bytes.AsSpan(offset, byteCount));
        WriteHeader(binding, checked((uint)(offset / sizeof(uint))), checked((uint)values.Length), strideWords);
        _usedBytes = end;
        reason = "Ready";
        return true;
    }

    internal bool TryFinalizeAndBind(out string reason)
    {
        if (_usedBytes < HeaderBytes || _usedBytes > _maximumBytes)
        {
            reason = "The canonical GL scene arena has an invalid finalized byte range.";
            return false;
        }
        if (_buffer == 0u)
            _buffer = _renderer.RawGL.CreateBuffer();
        if (_buffer == 0u)
        {
            reason = "OpenGL could not allocate the packed canonical scene arena buffer.";
            return false;
        }

        fixed (byte* payload = _bytes)
            _renderer.RawGL.NamedBufferData(_buffer, checked((nuint)_usedBytes), payload, GLEnum.DynamicDraw);
        _renderer.RawGL.BindBufferBase(GLEnum.ShaderStorageBuffer, Binding, _buffer);
        _finalized = true;
        reason = "Ready";
        return true;
    }

    internal bool TryBind(out string reason)
    {
        if (!_finalized || _buffer == 0u)
        {
            reason = "The canonical GL scene arena has not been finalized for this publication.";
            return false;
        }
        _renderer.RawGL.BindBufferBase(GLEnum.ShaderStorageBuffer, Binding, _buffer);
        reason = "Ready";
        return true;
    }

    public void Dispose()
    {
        if (_buffer != 0u && RuntimeEngine.IsRenderThread)
            _renderer.RawGL.DeleteBuffer(_buffer);
        _buffer = 0u;
        _finalized = false;
        _usedBytes = 0;
    }

    private bool EnsureCapacity(int requiredBytes)
    {
        if (requiredBytes <= _bytes.Length)
            return true;
        if (requiredBytes > _maximumBytes)
            return false;

        int capacity = _bytes.Length;
        while (capacity < requiredBytes)
        {
            int next = (int)Math.Min(_maximumBytes, (long)capacity * 2);
            if (next <= capacity)
                return false;
            capacity = next;
        }
        Array.Resize(ref _bytes, capacity);
        return true;
    }

    private void WriteHeader(uint binding, uint offsetWords, uint elementCount, uint strideWords)
    {
        Span<uint> words = MemoryMarshal.Cast<byte, uint>(_bytes.AsSpan(0, HeaderBytes));
        int offset = checked((int)binding * 4);
        words[offset] = offsetWords;
        words[offset + 1] = elementCount;
        words[offset + 2] = strideWords;
        words[offset + 3] = 0u;
    }

    private static int Align16(int value)
        => checked((value + 15) & ~15);
}
