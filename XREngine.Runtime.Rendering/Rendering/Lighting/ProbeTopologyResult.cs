namespace XREngine.Rendering;

/// <summary>Immutable CPU result published by a detached topology worker.</summary>
internal sealed class ProbeTopologyResult(
    int pipelineInstanceId,
    int resourceGeneration,
    long requestToken,
    ulong layoutSignature,
    Guid[] probeIds,
    int[] sourceIndices,
    ProbeTetraData[] tetrahedra)
{
    public int PipelineInstanceId { get; } = pipelineInstanceId;
    public int ResourceGeneration { get; } = resourceGeneration;
    public long RequestToken { get; } = requestToken;
    public ulong LayoutSignature { get; } = layoutSignature;
    public Guid[] ProbeIds { get; } = probeIds;
    public int[] SourceIndices { get; } = sourceIndices;
    public ProbeTetraData[] Tetrahedra { get; } = tetrahedra;
    public int ProbeCount => ProbeIds.Length;
}
