using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal enum PendingDescriptorSource : byte { Buffer, Image, TexelBuffer }

internal readonly record struct PendingDescriptorWrite(
    uint Set, uint Binding, DescriptorType DescriptorType, uint DescriptorCount,
    PendingDescriptorSource Source, int SourceStartIndex)
{
    internal static PendingDescriptorWrite Buffer(uint set, uint binding, DescriptorType type, uint count, int start)
        => new(set, binding, type, count, PendingDescriptorSource.Buffer, start);
    internal static PendingDescriptorWrite Image(uint set, uint binding, DescriptorType type, uint count, int start)
        => new(set, binding, type, count, PendingDescriptorSource.Image, start);
    internal static PendingDescriptorWrite Texel(uint set, uint binding, DescriptorType type, uint count, int start)
        => new(set, binding, type, count, PendingDescriptorSource.TexelBuffer, start);
}

/// <summary>
/// Per-program, grow-only descriptor publication columns. This is deliberately
/// not a general collection: command recording resets logical counts only and
/// keeps the native ABI backing arrays owned by the program.
/// </summary>
internal sealed class VulkanComputeDescriptorScratchBuilder
{
    internal readonly record struct Telemetry(
        ulong ScannedBindings, ulong DirtyWriteRanges, ulong DescriptorElements,
        ulong NativeWriteBytes, int WriteHighWater, int BufferHighWater,
        int ImageHighWater, int TexelHighWater);
    private PendingDescriptorWrite[] _writes = new PendingDescriptorWrite[8];
    private DescriptorBufferInfo[] _buffers = new DescriptorBufferInfo[16];
    private DescriptorImageInfo[] _images = new DescriptorImageInfo[16];
    private BufferView[] _texels = new BufferView[8];
    private DescriptorPoolSize[] _poolSizes = new DescriptorPoolSize[8];
    private WriteDescriptorSet[] _nativeWrites = new WriteDescriptorSet[8];

    internal int WriteCount { get; private set; }
    internal int BufferCount { get; private set; }
    internal int ImageCount { get; private set; }
    internal int TexelCount { get; private set; }
    internal int PoolSizeCount { get; private set; }
    internal ulong ScannedTotal { get; private set; }
    internal ulong DirtyWritesTotal { get; private set; }
    internal ulong NativeWriteBytesTotal { get; private set; }
    internal ulong DescriptorElementsTotal { get; private set; }
    internal int HighWaterWrites { get; private set; }
    internal int HighWaterBuffers { get; private set; }
    internal int HighWaterImages { get; private set; }
    internal int HighWaterTexels { get; private set; }

    internal PendingDescriptorWrite[] Writes => _writes;
    internal DescriptorBufferInfo[] Buffers => _buffers;
    internal DescriptorImageInfo[] Images => _images;
    internal BufferView[] Texels => _texels;
    internal WriteDescriptorSet[] NativeWrites => _nativeWrites;
    internal DescriptorPoolSize[] PoolSizeArray => _poolSizes;
    internal ReadOnlySpan<DescriptorPoolSize> PoolSizes => _poolSizes.AsSpan(0, PoolSizeCount);

    internal void Reset()
    {
        WriteCount = 0;
        BufferCount = 0;
        ImageCount = 0;
        TexelCount = 0;
        PoolSizeCount = 0;
    }

    internal void RecordScanned() => ScannedTotal++;

    internal void AddPoolSize(DescriptorType type, uint count)
    {
        for (int index = 0; index < PoolSizeCount; index++)
            if (_poolSizes[index].Type == type)
            {
                _poolSizes[index].DescriptorCount += count;
                return;
            }

        Ensure(ref _poolSizes, PoolSizeCount + 1);
        _poolSizes[PoolSizeCount++] = new DescriptorPoolSize { Type = type, DescriptorCount = count };
    }

    internal void AddBuffer(in DescriptorBufferInfo value, uint count)
    {
        Ensure(ref _buffers, checked(BufferCount + (int)count));
        int start = BufferCount;
        for (int index = 0; index < count; index++)
            _buffers[BufferCount++] = value;
        if (BufferCount > HighWaterBuffers)
            HighWaterBuffers = BufferCount;
        DescriptorElementsTotal += count;
    }

    internal void AddImage(in DescriptorImageInfo value, uint count)
    {
        Ensure(ref _images, checked(ImageCount + (int)count));
        for (int index = 0; index < count; index++)
            _images[ImageCount++] = value;
        if (ImageCount > HighWaterImages)
            HighWaterImages = ImageCount;
        DescriptorElementsTotal += count;
    }

    internal void AddTexel(in BufferView value, uint count)
    {
        Ensure(ref _texels, checked(TexelCount + (int)count));
        for (int index = 0; index < count; index++)
            _texels[TexelCount++] = value;
        if (TexelCount > HighWaterTexels)
            HighWaterTexels = TexelCount;
        DescriptorElementsTotal += count;
    }

    internal void AddWrite(uint set, uint binding, DescriptorType type, uint count, PendingDescriptorSource source, int sourceStart)
    {
        Ensure(ref _writes, WriteCount + 1);
        _writes[WriteCount++] = new PendingDescriptorWrite(set, binding, type, count, source, sourceStart);
        DirtyWritesTotal++;
        NativeWriteBytesTotal += (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<WriteDescriptorSet>();
        if (WriteCount > HighWaterWrites)
            HighWaterWrites = WriteCount;
    }

    internal void EnsureNativeWriteCapacity(int count) => Ensure(ref _nativeWrites, count);

    /// <summary>Allocation-free accounting export for compute descriptor publication.</summary>
    internal Telemetry GetTelemetry() => new(
        ScannedTotal,
        DirtyWritesTotal,
        DescriptorElementsTotal,
        NativeWriteBytesTotal,
        HighWaterWrites,
        HighWaterBuffers,
        HighWaterImages,
        HighWaterTexels);

    private static void Ensure<T>(ref T[] storage, int required)
    {
        if (storage.Length >= required)
            return;
        int capacity = Math.Max(storage.Length, 4);
        while (capacity < required)
            capacity = capacity > int.MaxValue / 2 ? required : capacity * 2;
        Array.Resize(ref storage, capacity);
    }
}
