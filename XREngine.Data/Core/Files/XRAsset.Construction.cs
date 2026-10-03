namespace XREngine.Core.Files;

public abstract partial class XRAsset
{
    /// <summary>Restores constructor-owned metadata when this asset is explicitly revived.</summary>
    public override void Generate()
    {
        bool reviving = IsDestroyed;
        base.Generate();
        if (reviving && !IsDestroyed && _ownedEmbeddedAssets.IsDestroyed)
            _ownedEmbeddedAssets.Generate();
    }

    /// <summary>Releases this asset's metadata container without destroying referenced assets.</summary>
    protected override void OnDestroying()
    {
        _ownedEmbeddedAssets.Destroy(now: true);
        if (!_ownedEmbeddedAssets.IsDestroyed)
            throw new InvalidOperationException("Asset metadata storage destruction was vetoed.");
        base.OnDestroying();
    }

    /// <summary>Initializes an asset whose global cache publication is owned by its derived constructor.</summary>
    protected XRAsset(bool deferObjectCachePublication)
        : base(deferObjectCachePublication)
    {
        _ownedEmbeddedAssets = _embeddedAssets;
    }
}
