using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Retains one exact snapshot generation. Copies share one token, so repeated
/// disposal releases that token only once.
/// </summary>
public readonly struct EditorPickingSnapshotLease : IDisposable
{
    private readonly EditorPickingSnapshotRing? _ring;
    internal readonly int TokenIndex;
    internal readonly ulong TokenGeneration;
    internal readonly int SlotIndex;
    internal readonly ulong SlotGeneration;

    internal EditorPickingSnapshotLease(
        EditorPickingSnapshotRing ring,
        int tokenIndex,
        ulong tokenGeneration,
        int slotIndex,
        ulong slotGeneration,
        in BackendReadyFramePackageIdentity identity,
        long packageGeneration)
    {
        _ring = ring;
        TokenIndex = tokenIndex;
        TokenGeneration = tokenGeneration;
        SlotIndex = slotIndex;
        SlotGeneration = slotGeneration;
        PackageIdentity = identity;
        PackageGeneration = packageGeneration;
    }

    public BackendReadyFramePackageIdentity PackageIdentity { get; }
    public long PackageGeneration { get; }
    public bool IsValid => _ring?.IsLeaseValid(in this) == true;

    public bool TryRetain(
        out EditorPickingSnapshotLease retained,
        out EditorPickingSnapshotFailure failure)
    {
        if (_ring is null)
        {
            retained = default;
            failure = EditorPickingSnapshotFailure.StaleLease;
            return false;
        }

        return _ring.TryRetain(in this, out retained, out failure);
    }

    public void Dispose() => _ring?.Release(in this);

    internal bool BelongsTo(EditorPickingSnapshotRing ring)
        => ReferenceEquals(_ring, ring);

    internal bool IsSameLease(in EditorPickingSnapshotLease other)
        => ReferenceEquals(_ring, other._ring) &&
            TokenIndex == other.TokenIndex &&
            TokenGeneration == other.TokenGeneration;
}
