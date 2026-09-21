using System.Numerics;
using XREngine.Data.Vectors;

namespace XREngine.Rendering;

internal sealed class ProbeGridResourceCandidate(
    XRDataBuffer? cellBuffer,
    XRDataBuffer? indexBuffer,
    Vector3 origin,
    float cellSize,
    IVector3 dimensions)
{
    public XRDataBuffer? CellBuffer = cellBuffer;
    public XRDataBuffer? IndexBuffer = indexBuffer;
    public Vector3 Origin { get; } = origin;
    public float CellSize { get; } = cellSize;
    public IVector3 Dimensions { get; } = dimensions;

    public void RelinquishBuffers(out XRDataBuffer? cellBuffer, out XRDataBuffer? indexBuffer)
    {
        cellBuffer = CellBuffer;
        indexBuffer = IndexBuffer;
        CellBuffer = null;
        IndexBuffer = null;
    }

    public void Destroy()
    {
        ForwardLightProbeInstanceResources.DestroyBuffer(ref CellBuffer);
        ForwardLightProbeInstanceResources.DestroyBuffer(ref IndexBuffer);
    }
}
