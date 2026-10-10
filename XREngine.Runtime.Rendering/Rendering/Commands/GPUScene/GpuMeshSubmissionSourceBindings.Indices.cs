using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

public sealed partial class GpuMeshSubmissionSourceBindings
{
    /// <summary>Exact prepared source indices frozen at publication; null remains explicitly pending.</summary>
    public XRDataBuffer? IndexBuffer { get; private set; }
    public IndexSize IndexSize { get; private set; }
    public uint IndexCount { get; private set; }
    public ulong IndexRevision { get; private set; }
    private uint _indexLength;
    private uint _indexElementSize;
    private EComponentType _indexComponentType;
    private uint _indexComponentCount;
    private bool _indexPaddedToVec4;

    private void CaptureIndexSource()
    {
        XRDataBuffer? buffer = null;
        IndexSize size = default;
        _sourceMesh?.TryGetPreparedIndexBuffer(EPrimitiveType.Triangles, out buffer, out size);
        IndexBuffer = buffer;
        IndexSize = size;
        IndexCount = buffer?.ElementCount ?? 0;
        IndexRevision = buffer?.Revision ?? 0;
        _indexLength = buffer?.Length ?? 0;
        _indexElementSize = buffer?.ElementSize ?? 0;
        _indexComponentType = buffer?.ComponentType ?? default;
        _indexComponentCount = buffer?.ComponentCount ?? 0;
        _indexPaddedToVec4 = buffer?.PadEndingToVec4 ?? false;
    }

    /// <summary>The physical unsigned index encoding must match one scalar of the captured source layout.</summary>
    public bool HasValidIndexLayout
    {
        get
        {
            // The canonical uint32 cache is built from validated nonnegative Int32
            // topology. Its unchanged four-byte bit pattern is the uint32 index ABI.
            uint bytes = IndexSize switch
            {
                IndexSize.TwoBytes when _indexComponentType == EComponentType.UShort => 2u,
                IndexSize.FourBytes when _indexComponentType is EComponentType.UInt or EComponentType.Int => 4u,
                _ => 0u,
            };
            ulong scalarBytes = (ulong)IndexCount * bytes;
            ulong declaredBytes = _indexPaddedToVec4 ? (scalarBytes + 15UL) & ~15UL : scalarBytes;
            return bytes != 0 && _indexComponentCount == 1 && _indexElementSize == bytes &&
                IndexCount != 0 && declaredBytes == _indexLength;
        }
    }

    /// <summary>Checks separate cached-index ownership and committed contents before and after authored callbacks.</summary>
    public bool AreIndexBindingsCurrent
        => HasValidIndexLayout && IndexBuffer is { IsDestroyed: false } buffer && _sourceMesh is not null &&
           _sourceMesh.TryGetPreparedIndexBuffer(EPrimitiveType.Triangles, out XRDataBuffer? current, out IndexSize size) &&
           ReferenceEquals(buffer, current) && size == IndexSize && buffer.Revision == IndexRevision &&
           buffer.ElementCount == IndexCount && buffer.Length == _indexLength && buffer.ElementSize == _indexElementSize &&
           buffer.ComponentType == _indexComponentType && buffer.ComponentCount == _indexComponentCount &&
           buffer.PadEndingToVec4 == _indexPaddedToVec4 && buffer.Target == EBufferTarget.ElementArrayBuffer;
}
