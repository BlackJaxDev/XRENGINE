namespace XREngine.Browser;

/// <summary>A weak-keyed allocation identity used only for measurement; never retains or disposes its native data source.</summary>
internal sealed class BrowserAssetNativeEstimate
{
    public int References { get; set; }
    public long Bytes { get; set; }
}
