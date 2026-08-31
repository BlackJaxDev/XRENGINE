using System.Threading;
using XREngine.Data;

namespace XREngine.Rendering.Materials;

/// <summary>
/// Reference-counted immutable CPU snapshot of one published material table and its exact Vulkan
/// descriptor-element generations. Native lowering must bind a dedicated backing for this token;
/// it must never fall back to the mutable table buffer.
/// </summary>
public sealed class GPUMaterialTablePublication : IDisposable
{
    private const int MaximumChunkBytes = 64 * 1024;

    private readonly ReadOnlyStoragePublication[] _chunks;
    private int _referenceCount = 1;

    private GPUMaterialTablePublication(
        ReadOnlyStoragePublication[] chunks,
        GPUMaterialTableDescriptorClosure descriptorClosure,
        ulong ownerId,
        ulong generation,
        uint rowCount,
        uint rowByteStride)
    {
        _chunks = chunks;
        DescriptorClosure = descriptorClosure;
        OwnerId = ownerId;
        Generation = generation;
        RowCount = rowCount;
        RowByteStride = rowByteStride;
    }

    public ulong Generation { get; }
    /// <summary>Stable identity of the material-table publisher; never derive this from row content.</summary>
    public ulong OwnerId { get; }
    /// <summary>Changes only when the exact descriptor index/generation closure changes.</summary>
    public ulong DescriptorClosureGeneration => DescriptorClosure.Generation;
    public GPUMaterialTableDescriptorClosure DescriptorClosure { get; }
    public uint RowCount { get; }
    public uint RowByteStride { get; }
    public ReadOnlySpan<ReadOnlyStoragePublication> Chunks => _chunks;
    public ReadOnlySpan<GPUMaterialTextureReference> VulkanTextureReferences => DescriptorClosure.References;

    internal static unsafe GPUMaterialTablePublication Capture(
        XRDataBuffer buffer,
        GPUMaterialTablePublication? previous,
        in GPUMaterialTable.SparseDirtyByteRanges dirtyRanges,
        GPUMaterialTextureReference[] closureReferences,
        ulong ownerId,
        ulong generation,
        ulong descriptorClosureGeneration,
        uint rowWordCount)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        DataSource source = buffer.ClientSideSource ?? throw new InvalidOperationException(
            "A material-table publication requires CPU-owned row storage.");
        if (!source.Address.IsValid || source.Length < buffer.Length || buffer.ElementSize == 0u)
            throw new InvalidOperationException(
                "The material-table CPU storage is unavailable or does not match the published row extent.");

        uint rowByteStride = checked(rowWordCount * sizeof(uint));
        if (rowByteStride != buffer.ElementSize)
            throw new InvalidOperationException(
                "The material-table publication row stride does not match the packed GPU row ABI.");

        int chunkCount = checked((int)((buffer.Length + MaximumChunkBytes - 1u) / MaximumChunkBytes));
        ReadOnlyStoragePublication[] chunks = new ReadOnlyStoragePublication[chunkCount];
        GPUMaterialTableDescriptorClosure? closure = null;
        try
        {
            byte* sourceBytes = (byte*)source.Address.Pointer;
            for (int index = 0; index < chunks.Length; ++index)
            {
                uint offset = checked((uint)index * MaximumChunkBytes);
                int chunkLength = checked((int)Math.Min(
                    (uint)MaximumChunkBytes,
                    buffer.Length - offset));
                if (previous is not null &&
                    index < previous._chunks.Length &&
                    previous._chunks[index].Length == chunkLength &&
                    !dirtyRanges.Intersects(offset, (uint)chunkLength))
                {
                    chunks[index] = previous._chunks[index].Retain();
                    continue;
                }

                chunks[index] = ReadOnlyStoragePublication.CopyFrom(
                    sourceBytes + offset,
                    chunkLength,
                    ownerId,
                    generation,
                    abiSignature: rowByteStride);
            }

            closure = previous is not null && previous.OwnerId == ownerId &&
                previous.DescriptorClosureGeneration == descriptorClosureGeneration &&
                previous.HasSameDescriptorClosure(closureReferences)
                ? previous.DescriptorClosure.Retain()
                : new GPUMaterialTableDescriptorClosure(closureReferences, ownerId, descriptorClosureGeneration);
            return new GPUMaterialTablePublication(
                chunks,
                closure,
                ownerId,
                generation,
                buffer.ElementCount,
                rowByteStride);
        }
        catch
        {
            closure?.Dispose();
            foreach (ReadOnlyStoragePublication chunk in chunks)
                if (chunk.IsValid)
                    chunk.Dispose();
            throw;
        }
    }

    public GPUMaterialTablePublication Retain()
    {
        while (true)
        {
            int count = Volatile.Read(ref _referenceCount);
            if (count <= 0)
                throw new ObjectDisposedException(nameof(GPUMaterialTablePublication));
            if (count == int.MaxValue)
                throw new InvalidOperationException("Material publication reference count overflow.");
            if (Interlocked.CompareExchange(ref _referenceCount, count + 1, count) == count)
                return this;
        }
    }

    public void Dispose()
    {
        int count = Interlocked.Decrement(ref _referenceCount);
        if (count > 0)
            return;
        if (count < 0)
        {
            Interlocked.Increment(ref _referenceCount);
            throw new InvalidOperationException("Material publication reference count underflow.");
        }

        try
        {
            foreach (ReadOnlyStoragePublication chunk in _chunks)
                chunk.Dispose();
        }
        finally
        {
            DescriptorClosure.Dispose();
        }
    }

    internal bool HasSameDescriptorClosure(
        ReadOnlySpan<GPUMaterialTextureReference> references)
        => VulkanTextureReferences.SequenceEqual(references);

    internal static GPUMaterialTextureReference[] CaptureVulkanTextureReferences(
        IReadOnlyDictionary<uint, GPUMaterialTextureReferences> textureReferences)
    {
        if (textureReferences.Count == 0)
            return [];

        GPUMaterialTextureReference[] references = new GPUMaterialTextureReference[
            checked(textureReferences.Count * 3)];
        int count = 0;
        foreach (GPUMaterialTextureReferences row in textureReferences.Values)
        {
            AddVulkanReference(row.Albedo, references, ref count);
            AddVulkanReference(row.Normal, references, ref count);
            AddVulkanReference(row.RM, references, ref count);
        }

        if (count != references.Length)
            Array.Resize(ref references, count);
        Array.Sort(references, static (left, right) =>
        {
            int indexComparison = left.VulkanDescriptorIndex.CompareTo(right.VulkanDescriptorIndex);
            return indexComparison != 0
                ? indexComparison
                : left.VulkanDescriptorGeneration.CompareTo(right.VulkanDescriptorGeneration);
        });
        return references;
    }

    private static void AddVulkanReference(
        GPUMaterialTextureReference reference,
        Span<GPUMaterialTextureReference> destination,
        ref int count)
    {
        if (reference.Kind != EGPUMaterialTextureReferenceKind.VulkanDescriptorIndex ||
            reference.VulkanDescriptorIndex == GPUMaterialTable.InvalidTextureHandleIndex)
        {
            return;
        }

        if (reference.VulkanDescriptorGeneration == 0u)
            throw new InvalidOperationException(
                "A Vulkan material row referenced a descriptor element without its exact slot generation.");

        for (int index = 0; index < count; ++index)
            if (destination[index].Equals(reference))
                return;

        destination[count++] = reference;
    }
}
