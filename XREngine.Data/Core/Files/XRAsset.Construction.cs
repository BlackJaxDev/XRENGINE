namespace XREngine.Core.Files;

public abstract partial class XRAsset
{
    /// <summary>Initializes an asset whose global cache publication is owned by its derived constructor.</summary>
    protected XRAsset(bool deferObjectCachePublication)
        : base(deferObjectCachePublication)
    {
    }
}
