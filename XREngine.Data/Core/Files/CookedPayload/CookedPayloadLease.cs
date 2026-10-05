using XREngine.Data.Runtime.Memory;

namespace XREngine.Core.Files;

/// <summary>
/// Stack-confined payload access with shared, exactly-once ownership. Copies of a lease share
/// disposal and transfer state, so a copied lease cannot double-return or double-free storage.
/// Mapped leases retain their archive until disposed or transferred into a heap owner.
/// </summary>
public unsafe ref struct CookedPayloadLease
{
    private CookedPayloadOwner? _owner;
    private CookedPayloadLease(CookedPayloadOwner owner) => _owner = owner;

    internal static CookedPayloadLease Mapped(PublishedArchiveHandle archive, void* pointer, int length)
        => new(CookedPayloadOwner.FromArchive(archive, pointer, length));
    internal static CookedPayloadLease Pooled(byte[] buffer, int length)
        => new(CookedPayloadOwner.FromPooled(buffer, length));
    internal static CookedPayloadLease Native(void* pointer, int length, NativeMemoryPressureLease pressure)
        => new(CookedPayloadOwner.FromNative(pointer, length, pressure));

    public ReadOnlySpan<byte> Span => RequireOwner().LeaseSpan;
    internal Span<byte> WritableSpan
    {
        get
        {
            var owner = RequireOwner();
            if (owner.Storage == ECookedPayloadStorage.Mapped)
                throw new InvalidOperationException("Mapped cooked payload leases are read-only.");
            return owner.LeaseSpan;
        }
    }
    public int Length => IsDisposed ? 0 : _owner!.Length;
    public ECookedPayloadStorage Storage => IsDisposed ? ECookedPayloadStorage.None : _owner!.Storage;
    public bool IsDisposed => _owner is null || !_owner.IsLeaseActive;

    /// <summary>Transfers retained mapped, pooled or native storage without copying its bytes.</summary>
    public CookedPayloadOwner TransferToOwner()
    {
        var owner = RequireOwner().TransferLease();
        _owner = null;
        return owner;
    }

    public void Dispose()
    {
        _owner?.DisposeLease();
        _owner = null;
    }

    private readonly CookedPayloadOwner RequireOwner()
        => _owner is { IsLeaseActive: true } owner ? owner
            : throw new ObjectDisposedException(nameof(CookedPayloadLease), "The cooked payload lease was disposed or transferred.");
}
