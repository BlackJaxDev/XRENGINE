namespace XREngine.Rendering.Commands;

/// <summary>Ring-owned submission sidecar captured alongside the canonical table images.</summary>
public sealed class AdvancedSceneSubmissionPublicationSnapshot
{
    private AdvancedDrawSubmissionRecord[] _records = new AdvancedDrawSubmissionRecord[64];
    private AdvancedManagedDeformationSourceRow[] _deformationSources = new AdvancedManagedDeformationSourceRow[64];

    public ulong Sequence { get; private set; }
    public int Count { get; private set; }
    public ReadOnlySpan<AdvancedDrawSubmissionRecord> Records => _records.AsSpan(0, Count);
    public ReadOnlySpan<AdvancedManagedDeformationSourceRow> DeformationSources => _deformationSources.AsSpan(0, Count);

    internal bool TryCapture(ulong sequence, ReadOnlySpan<AdvancedDrawSubmissionRecord> records, ReadOnlySpan<AdvancedManagedDeformationSourceRow> sources)
    {
        if (records.Length != sources.Length)
            return false;
        if (_records.Length < records.Length)
            Array.Resize(ref _records, records.Length);
        if (_deformationSources.Length < sources.Length)
            Array.Resize(ref _deformationSources, sources.Length);
        records.CopyTo(_records);
        sources.CopyTo(_deformationSources);
        Count = records.Length;
        Sequence = sequence;
        return true;
    }
}

/// <summary>Managed-only deformation closure; source references are never used for normal draw submission.</summary>
public readonly record struct AdvancedManagedDeformationSourceRow(
    IRenderCommandMesh? Source,
    XRMeshRenderer? Renderer,
    uint MeshVertexCount,
    ulong SourceVersion,
    ulong MeshVersion);
