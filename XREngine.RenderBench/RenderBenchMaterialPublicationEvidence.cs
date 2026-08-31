namespace XREngine.RenderBench;

/// <summary>One native-backed material-table publication captured from the real opaque draw pass.</summary>
public sealed record RenderBenchMaterialPublicationEvidence
{
    public string Step { get; init; } = string.Empty;
    public ulong OwnerId { get; init; }
    public ulong Generation { get; init; }
    public uint RowCount { get; init; }
    public uint RowByteStride { get; init; }
    public ulong DescriptorClosureGeneration { get; init; }
    public int DescriptorReferenceCount { get; init; }
    public int ChunkCount { get; init; }
    public int CpuByteCount { get; init; }
    public ulong NativeBufferHandle { get; init; }
    public ulong NativeGeneration { get; init; }
    public ulong NativeRange { get; init; }
    public ulong NativeRowGeneration { get; init; }
    public ulong NativeDescriptorClosureGeneration { get; init; }
    public bool NativeBytesMatchPublication { get; init; }
    public ulong MaterialBytesWritten { get; init; }
    public int MaterialRangeCount { get; init; }
    public RenderBenchMaterialRangeEvidence[] MaterialRanges { get; init; } = [];
}
