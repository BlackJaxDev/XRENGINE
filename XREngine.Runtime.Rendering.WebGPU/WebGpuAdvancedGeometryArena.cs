using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>
/// Packs the seven canonical immutable geometry byte streams behind a seven-row
/// four-word directory (absolute word offset, exact byte length, two reserved words).
/// Stream bytes and cooked layouts are identical to the shared GL/Vulkan publication.
/// </summary>
internal sealed class WebGpuAdvancedGeometryArena
{
    internal const int StreamCount = 7;
    private const int HeaderBytes = StreamCount * 4 * sizeof(uint);
    private byte[] _bytes = new byte[HeaderBytes];
    private readonly AdvancedGpuHandle[] _handles = new AdvancedGpuHandle[StreamCount];
    private readonly uint[] _lengths = new uint[StreamCount];
    private int _usedBytes;
    private int _maximumBytes;
    private ulong _databaseEpoch;

    internal ReadOnlySpan<byte> Bytes => _bytes.AsSpan(0, _usedBytes);

    internal bool Matches(AdvancedGeometryPublicationSnapshot geometry, ulong databaseEpoch)
        => _databaseEpoch == databaseEpoch && _usedBytes >= HeaderBytes &&
           Matches(0, geometry.StaticVertices) && Matches(1, geometry.Indices) &&
           Matches(2, geometry.PreSkinnedCurrent) && Matches(3, geometry.PreSkinnedPrevious) &&
           Matches(4, geometry.MeshletDescriptors) && Matches(5, geometry.MeshletVertexIndices) &&
           Matches(6, geometry.MeshletTriangleWords);

    private bool Matches(int stream, in AdvancedImmutableByteArenaPublicationSnapshot source)
        => _handles[stream] == source.BufferHandle && _lengths[stream] == source.ByteCount;

    internal void Pack(AdvancedGeometryPublicationSnapshot geometry, ulong databaseEpoch, int maximumBytes)
    {
        _maximumBytes = maximumBytes;
        _usedBytes = HeaderBytes;
        _bytes.AsSpan(0, HeaderBytes).Clear();
        Write(0, geometry.StaticVertices);
        Write(1, geometry.Indices);
        Write(2, geometry.PreSkinnedCurrent);
        Write(3, geometry.PreSkinnedPrevious);
        Write(4, geometry.MeshletDescriptors);
        Write(5, geometry.MeshletVertexIndices);
        Write(6, geometry.MeshletTriangleWords);
        _databaseEpoch = databaseEpoch;
    }

    private void Write(int stream, in AdvancedImmutableByteArenaPublicationSnapshot source)
    {
        int offset = checked((_usedBytes + 15) & ~15);
        int end = checked((offset + source.Data.Length + 3) & ~3);
        if (end > _maximumBytes)
            throw new NotSupportedException("WebGPU.Advanced.GeometryCapacity: the exact immutable geometry image exceeds the retained storage binding limit.");
        if (end > _bytes.Length)
        {
            int capacity = _bytes.Length;
            while (capacity < end) capacity = checked((int)Math.Min(_maximumBytes, (long)capacity * 2));
            Array.Resize(ref _bytes, capacity);
        }
        _bytes.AsSpan(_usedBytes, end - _usedBytes).Clear();
        source.Data.CopyTo(_bytes.AsSpan(offset, source.Data.Length));
        Span<uint> directory = MemoryMarshal.Cast<byte, uint>(_bytes.AsSpan(0, HeaderBytes));
        directory[stream * 4] = checked((uint)(offset / sizeof(uint)));
        directory[stream * 4 + 1] = source.ByteCount;
        _handles[stream] = source.BufferHandle;
        _lengths[stream] = source.ByteCount;
        _usedBytes = end;
    }
}
