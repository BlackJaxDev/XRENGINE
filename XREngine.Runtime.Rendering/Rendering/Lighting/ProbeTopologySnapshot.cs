using System.Numerics;

namespace XREngine.Rendering;

/// <summary>Detached CPU input for one light-probe topology request.</summary>
internal sealed class ProbeTopologySnapshot(
    int pipelineInstanceId,
    int resourceGeneration,
    long requestToken,
    ulong layoutSignature,
    Guid[] probeIds,
    int[] sourceIndices,
    Vector3[] positions)
{
    public int PipelineInstanceId { get; } = pipelineInstanceId;
    public int ResourceGeneration { get; } = resourceGeneration;
    public long RequestToken { get; } = requestToken;
    public ulong LayoutSignature { get; } = layoutSignature;
    public Guid[] ProbeIds { get; } = probeIds;
    public int[] SourceIndices { get; } = sourceIndices;
    public Vector3[] Positions { get; } = positions;
    public int ProbeCount => Positions.Length;
}
