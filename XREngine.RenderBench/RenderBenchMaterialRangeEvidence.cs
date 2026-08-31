namespace XREngine.RenderBench;

/// <summary>Exact sparse material-row upload range reported by the opaque pass.</summary>
public sealed record RenderBenchMaterialRangeEvidence(uint FirstIndex, uint IndexCount, uint ByteOffset, uint ByteCount);
