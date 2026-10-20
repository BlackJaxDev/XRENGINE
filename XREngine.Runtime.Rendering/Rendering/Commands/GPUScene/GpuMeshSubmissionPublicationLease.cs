namespace XREngine.Rendering.Commands;

/// <summary>
/// Allocation-free, generation-checked pin. Copies share one release token;
/// use TryRetain for an independently owned asynchronous consumer.
/// </summary>
public readonly struct GpuMeshSubmissionPublicationLease : IDisposable
{
    private readonly GPUScene? _owner;
    private readonly int _tokenIndex;
    private readonly ulong _tokenGeneration;

    internal GpuMeshSubmissionPublicationLease(GPUScene owner, int tokenIndex, ulong tokenGeneration)
    {
        _owner = owner;
        _tokenIndex = tokenIndex;
        _tokenGeneration = tokenGeneration;
    }

    public bool IsValid => _owner?.GetMeshSubmissionLeasePublication(_tokenIndex, _tokenGeneration) is not null;
    public GpuMeshSubmissionPublication Publication
        => _owner?.GetMeshSubmissionLeasePublication(_tokenIndex, _tokenGeneration)
           ?? throw new ObjectDisposedException(nameof(GpuMeshSubmissionPublicationLease));

    public bool TryRetain(out GpuMeshSubmissionPublicationLease lease)
    {
        lease = default;
        return _owner is not null && _owner.TryRetainMeshSubmissionPublication(_tokenIndex, _tokenGeneration, out lease);
    }

    public void Dispose() => _owner?.ReleaseMeshSubmissionPublication(_tokenIndex, _tokenGeneration);
}
