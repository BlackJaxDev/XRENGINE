namespace XREngine.Core.Files;

public abstract partial class XRAsset
{
    /// <summary>Releases this asset's metadata container without destroying referenced assets.</summary>
    protected override void OnDestroying()
    {
        _embeddedAssets.Destroy(now: true);
        base.OnDestroying();
    }

    /// <summary>Initializes an asset whose global cache publication is owned by its derived constructor.</summary>
    protected XRAsset(bool deferObjectCachePublication)
        : base(deferObjectCachePublication)
    {
    }
}
