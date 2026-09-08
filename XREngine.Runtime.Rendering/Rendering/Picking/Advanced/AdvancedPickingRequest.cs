using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Single-owner asynchronous picking request. It pins the exact canonical publication until
/// the backend readback completes, is superseded, or the caller disposes it.
/// </summary>
public sealed class AdvancedPickingRequest : IDisposable
{
    private AdvancedGpuScenePublicationLease _publicationLease;
    private readonly AdvancedGpuScenePublicationSnapshot? _snapshot;
    private Action<AdvancedPickingResult>? _completed;
    private int _settled;

    internal AdvancedPickingRequest(
        ulong generation,
        in AdvancedPickingQuery query,
        int pipelineInstanceId,
        int resourceGeneration,
        long packageGeneration,
        AdvancedGpuScenePublicationLease publicationLease,
        Action<AdvancedPickingResult> completed)
    {
        Generation = generation;
        Query = query;
        PipelineInstanceId = pipelineInstanceId;
        ResourceGeneration = resourceGeneration;
        PackageGeneration = packageGeneration;
        _publicationLease = publicationLease;
        _snapshot = publicationLease.Reference.Snapshot;
        DatabaseEpoch = publicationLease.Reference.DatabaseEpoch;
        PublicationSequence = publicationLease.Reference.Sequence;
        _completed = completed;
    }

    public ulong Generation { get; }
    public AdvancedPickingQuery Query { get; }
    public int PipelineInstanceId { get; }
    public int ResourceGeneration { get; }
    public long PackageGeneration { get; }
    public ulong DatabaseEpoch { get; }
    public ulong PublicationSequence { get; }
    public bool IsPending => Volatile.Read(ref _settled) == 0;

    internal bool TryBeginDelivery(
        out AdvancedGpuScenePublicationSnapshot? snapshot)
    {
        if (Interlocked.CompareExchange(ref _settled, 2, 0) != 0)
        {
            snapshot = null;
            return false;
        }

        snapshot = _snapshot;
        return true;
    }

    internal void CompleteDelivery(in AdvancedPickingResult result)
    {
        if (Interlocked.CompareExchange(ref _settled, 1, 2) != 2)
            throw new InvalidOperationException(
                "Advanced picking delivery completed without owning the request.");
        Action<AdvancedPickingResult>? callback = Interlocked.Exchange(ref _completed, null);
        ReleasePublication();
        callback?.Invoke(result);
    }

    internal bool TrySupersede()
    {
        if (Interlocked.CompareExchange(ref _settled, 1, 0) != 0)
            return false;

        Interlocked.Exchange(ref _completed, null);
        ReleasePublication();
        return true;
    }

    public void Dispose() => TrySupersede();

    private void ReleasePublication()
    {
        _publicationLease.Dispose();
        _publicationLease = default;
    }
}
