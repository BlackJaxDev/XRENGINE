namespace XREngine.Rendering;

public abstract partial class XRTexture
{
    private XRTextureBindingLease? _bindingStorageLease;

    /// <summary>Retains a new binding while this texture is active.</summary>
    public bool TryRetainActiveBindingUse()
    {
        if (IsDestroyed)
            return false;

        XRTextureBindingLease lease = GetBindingStorageLease();
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

    /// <summary>Destroys this texture after its final counted binding use.</summary>
    public void RetireBindingStorage()
        => GetBindingStorageLease().Retire();

    private XRTextureBindingLease GetBindingStorageLease()
    {
        XRTextureBindingLease? lease = Volatile.Read(ref _bindingStorageLease);
        return lease ?? LazyInitializer.EnsureInitialized(
            ref _bindingStorageLease,
            () => new XRTextureBindingLease(this))!;
    }
}
