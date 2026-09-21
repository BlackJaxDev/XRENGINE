using XREngine.Data.Vectors;

namespace XREngine.Rendering;

internal struct ProbeGridCell
{
    public IVector4 OffsetCount;
    public IVector4 FallbackIndices;
}
