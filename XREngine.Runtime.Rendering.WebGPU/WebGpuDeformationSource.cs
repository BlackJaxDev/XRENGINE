using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

/// <summary>Retains one canonical CPU input region and copies only changed regions into the packed GPU arena.</summary>
internal sealed unsafe class WebGpuDeformationSource : IDisposable
{
    private readonly XRDataBuffer _source;
    private readonly uint _sourceLength;
    private readonly uint _sourceElementSize;
    private readonly EComponentType _sourceComponentType;
    private readonly uint _sourceComponentCount;
    private readonly bool _sourceIntegral;
    private readonly bool _sourceNormalize;
    private readonly int _sourceOffset;
    private readonly int _sourceStride;
    private readonly int _sourceComponents;
    private readonly int _outputComponents;
    private readonly int _vertexCount;
    private readonly bool _logicalIndices;
    private ulong _revision;
    private int _dirty = 1;

    public int DestinationOffset { get; }
    public int ByteCount { get; }
    public bool HasSameLayout => !_source.IsDestroyed && !_source.GpuProduced &&
        _source.Length == _sourceLength && _source.ElementSize == _sourceElementSize &&
        _source.ComponentType == _sourceComponentType && _source.ComponentCount == _sourceComponentCount &&
        _source.Integral == _sourceIntegral && _source.Normalize == _sourceNormalize;

    public WebGpuDeformationSource(XRDataBuffer source, int destinationOffset, int byteCount, bool logicalIndices = false,
        int vertexCount = 0, int sourceOffset = 0, int sourceStride = 0, int sourceComponents = 0, int outputComponents = 0)
    {
        _source = source;
        _sourceLength = source.Length;
        _sourceElementSize = source.ElementSize;
        _sourceComponentType = source.ComponentType;
        _sourceComponentCount = source.ComponentCount;
        _sourceIntegral = source.Integral;
        _sourceNormalize = source.Normalize;
        DestinationOffset = destinationOffset;
        ByteCount = byteCount;
        _logicalIndices = logicalIndices;
        _vertexCount = vertexCount;
        _sourceOffset = sourceOffset;
        _sourceStride = sourceStride;
        _sourceComponents = sourceComponents;
        _outputComponents = outputComponents;
        long requiredBytes = vertexCount == 0 ? byteCount : (long)(vertexCount - 1) * sourceStride + sourceOffset + sourceComponents * 4;
        if (source.GpuProduced || requiredBytes < 0 || requiredBytes > source.Length ||
            vertexCount != 0 && (sourceOffset < 0 || sourceStride <= 0 || (sourceOffset | sourceStride) % 4 != 0 ||
                sourceComponents is < 3 or > 4 || outputComponents < sourceComponents || sourceStride < sourceOffset + sourceComponents * 4))
            throw new NotSupportedException("WebGPU.Deformation.SourceLayout: a canonical CPU-backed source region is required.");
        if (logicalIndices && source.ComponentType is not (EComponentType.Int or EComponentType.UInt or EComponentType.Float))
            throw new NotSupportedException("WebGPU.Deformation.IndexEncoding: logical sparse indices must be int, uint or float encoded.");
        _source.PushDataRequested += MarkDirty;
        _source.PushSubDataRequested += MarkDirtyRange;
        _source.DataPointerSet += MarkPointerDirty;
    }

    public bool CopyIfChanged(Span<byte> packed)
    {
        if (!HasSameLayout)
            throw new InvalidOperationException("WebGPU.Deformation.SourceGeneration: rebuild the packed generation before consuming changed layouts.");
        ulong revision = _source.Revision;
        if (Interlocked.Exchange(ref _dirty, 0) == 0 && _revision == revision)
            return false;
        try
        {
            Copy(packed);
            _revision = revision;
            return true;
        }
        catch
        {
            MarkDirty();
            throw;
        }
    }

    private void Copy(Span<byte> packed)
    {
        ReadOnlySpan<byte> source = GetSourceBytes(_source);
        Span<byte> destination = packed.Slice(DestinationOffset, ByteCount);
        if (_vertexCount != 0)
        {
            Span<float> output = MemoryMarshal.Cast<byte, float>(destination);
            for (int vertex = 0; vertex < _vertexCount; vertex++)
            {
                ReadOnlySpan<float> input = MemoryMarshal.Cast<byte, float>(source.Slice(
                    vertex * _sourceStride + _sourceOffset, _sourceComponents * 4));
                Span<float> target = output.Slice(vertex * _outputComponents, _outputComponents);
                target.Clear();
                input.CopyTo(target);
                if (_outputComponents == 4 && _sourceComponents == 3) target[3] = 1;
                foreach (float value in target)
                    if (!float.IsFinite(value))
                        throw new NotSupportedException("WebGPU.Deformation.NonFiniteAttribute: source positions, normals and tangents must be finite.");
            }
        }
        else if (_logicalIndices && _source.ComponentType == EComponentType.Float)
        {
            ReadOnlySpan<float> input = MemoryMarshal.Cast<byte, float>(source[..ByteCount]);
            Span<uint> output = MemoryMarshal.Cast<byte, uint>(destination);
            for (int index = 0; index < output.Length; index++)
            {
                float value = input[index];
                if (!float.IsFinite(value) || value < 0 || value >= uint.MaxValue || value != MathF.Truncate(value))
                    throw new NotSupportedException("WebGPU.Deformation.IndexEncoding: sparse logical indices require exact nonnegative integers.");
                output[index] = (uint)value;
            }
        }
        else source[..ByteCount].CopyTo(destination);
    }

    internal static ReadOnlySpan<byte> GetSourceBytes(XRDataBuffer source)
    {
        if (source.ClientSideSource is not { } mirror || mirror.Length < source.Length ||
            !source.TryGetAddress(out VoidPtr address) || address == VoidPtr.Zero)
            throw new NotSupportedException("WebGPU.Deformation.SourceMissing: canonical packed inputs require retained CPU source records.");
        return new ReadOnlySpan<byte>(address.Pointer, checked((int)source.Length));
    }

    private void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);
    private void MarkDirtyRange(int offset, uint length) => MarkDirty();
    private void MarkPointerDirty(VoidPtr address) => MarkDirty();

    public void Dispose()
    {
        _source.PushDataRequested -= MarkDirty;
        _source.PushSubDataRequested -= MarkDirtyRange;
        _source.DataPointerSet -= MarkPointerDirty;
    }
}
