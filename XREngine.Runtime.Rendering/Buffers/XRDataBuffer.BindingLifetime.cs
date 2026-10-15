namespace XREngine.Rendering;

public partial class XRDataBuffer
{
    private XRDataBufferBindingLease? _bindingStorageLease;

    /// <summary>Retains a new binding while this buffer is active.</summary>
    public bool TryRetainActiveBindingUse()
    {
        if (IsDestroyed)
            return false;

        XRDataBufferBindingLease lease = GetBindingStorageLease();
        if (!lease.TryAcquireActiveUse())
            return false;

        if (!IsDestroyed)
            return true;

        lease.ReleaseAuthoringUse();
        return false;
    }

    /// <summary>Transfers a counted binding use to a later consumer.</summary>
    public void RetainBindingUse()
        => GetBindingStorageLease().RetainAuthoringUse();

    /// <summary>Releases one counted binding use.</summary>
    public void ReleaseBindingUse()
        => GetBindingStorageLease().ReleaseAuthoringUse();

    /// <summary>Destroys this buffer after its final counted binding use.</summary>
    public void RetireBindingStorage()
        => GetBindingStorageLease().Retire();

    private XRDataBufferBindingLease GetBindingStorageLease()
    {
        XRDataBufferBindingLease? lease = Volatile.Read(ref _bindingStorageLease);
        return lease ?? LazyInitializer.EnsureInitialized(
            ref _bindingStorageLease,
            () => new XRDataBufferBindingLease(this))!;
    }
}
