using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>
/// Packs the sealed shared scene records without lowering their precision or logical handles.
/// Each of the 32 directory rows contains eight words: record offset/count/stride,
/// lookup offset/count, and three reserved words. Offsets are absolute word offsets.
/// </summary>
internal sealed class WebGpuAdvancedSceneArena
{
    internal const int TableCount = 32;
    internal const int DirectoryWords = 8;
    internal const uint MaterialLayoutHandles = 30;
    internal const uint MaterialLayoutMembers = 31;
    private const int HeaderBytes = TableCount * DirectoryWords * sizeof(uint);
    private byte[] _bytes = new byte[HeaderBytes];
    private int _usedBytes;
    private int _maximumBytes;

    internal ReadOnlySpan<byte> Bytes => _bytes.AsSpan(0, _usedBytes);

    internal void Pack(AdvancedGpuScenePublicationSnapshot snapshot, int maximumBytes)
    {
        _maximumBytes = maximumBytes;
        _usedBytes = HeaderBytes;
        _bytes.AsSpan(0, HeaderBytes).Clear();
        Write(AdvancedGlobalResourceBindings.Draws, snapshot.Draws);
        Write(AdvancedGlobalResourceBindings.Instances, snapshot.Instances);
        Write(AdvancedGlobalResourceBindings.Meshes, snapshot.Geometry);
        Write(AdvancedGlobalResourceBindings.Materials, snapshot.Materials);
        Write(AdvancedGlobalResourceBindings.Lights, snapshot.GlobalResources.Lights);
        Write(AdvancedGlobalResourceBindings.Shadows, snapshot.GlobalResources.Shadows);
        Write(AdvancedGlobalResourceBindings.Textures, snapshot.Textures);
        Write(AdvancedGlobalResourceBindings.Samplers, snapshot.Samplers);
        Write(AdvancedGlobalResourceBindings.Deformations, snapshot.Deformations);
        Write(AdvancedGlobalResourceBindings.MaterialConstants, snapshot.MaterialPayloads.ConstantWords);
        Write(AdvancedGlobalResourceBindings.MaterialTextureBindings, snapshot.MaterialPayloads.TextureBindings);
        Write(AdvancedGlobalResourceBindings.Probes, snapshot.GlobalResources.Probes);
        Write(AdvancedGlobalResourceBindings.Environments, snapshot.GlobalResources.Environments);
        Write(AdvancedGlobalResourceBindings.Decals, snapshot.GlobalResources.Decals);
        Write(AdvancedGlobalResourceBindings.GiResources, snapshot.GlobalResources.GiResources);
        Write(AdvancedGlobalResourceBindings.Transforms, snapshot.Transforms);
        Write(AdvancedGlobalResourceBindings.RenderStates, snapshot.RenderStates);
        Write(AdvancedGlobalResourceBindings.ShadingKernels, snapshot.Kernels);
        Write(AdvancedGlobalResourceBindings.MaterialLayouts, snapshot.Layouts);
        Write(AdvancedGlobalResourceBindings.EditorIdentities, snapshot.EditorIdentities);
        Write(MaterialLayoutHandles, snapshot.MaterialPayloads.MaterialLayoutHandles);
        Write(MaterialLayoutMembers, snapshot.MaterialPayloads.LayoutMembers);
        // Views and physical texture descriptors are output/backend bindings, not
        // scene rows. Their directory entries remain empty rather than fabricated.
    }

    private void Write<T>(uint table, AdvancedGpuRecordTablePublicationSnapshot<T> snapshot) where T : unmanaged
        => Write(table, snapshot.PhysicalRecords, snapshot.HandleLookups);

    private void Write<T>(uint table, ReadOnlySpan<T> values,
        ReadOnlySpan<AdvancedGpuHandleLookup> lookups = default) where T : unmanaged
    {
        int stride = Unsafe.SizeOf<T>();
        if ((stride & 3) != 0)
            throw new InvalidOperationException("WebGPU.Advanced.SceneStride: shared scene records require whole-word strides.");
        int records = Append(MemoryMarshal.AsBytes(values));
        int lookup = Append(MemoryMarshal.AsBytes(lookups));
        Span<uint> directory = MemoryMarshal.Cast<byte, uint>(_bytes.AsSpan(0, HeaderBytes));
        int row = checked((int)table * DirectoryWords);
        directory[row] = checked((uint)(records / sizeof(uint)));
        directory[row + 1] = checked((uint)values.Length);
        directory[row + 2] = checked((uint)(stride / sizeof(uint)));
        directory[row + 3] = checked((uint)(lookup / sizeof(uint)));
        directory[row + 4] = checked((uint)lookups.Length);
    }

    private int Append(ReadOnlySpan<byte> values)
    {
        int offset = checked((_usedBytes + 15) & ~15);
        int end = checked(offset + values.Length);
        if (end > _maximumBytes)
            throw new NotSupportedException("WebGPU.Advanced.SceneCapacity: the exact canonical scene image exceeds the retained storage binding limit.");
        if (end > _bytes.Length)
        {
            int capacity = _bytes.Length;
            while (capacity < end) capacity = checked((int)Math.Min(_maximumBytes, (long)capacity * 2));
            Array.Resize(ref _bytes, capacity);
        }
        _bytes.AsSpan(_usedBytes, offset - _usedBytes).Clear();
        values.CopyTo(_bytes.AsSpan(offset, values.Length));
        _usedBytes = end;
        return offset;
    }
}
