namespace XREngine;

/// <summary>Identifies one asset authoring context captured from a watcher event.</summary>
public sealed class AssetAuthoringTicket
{
    internal AssetAuthoringTicket(AssetManager owner, object context, string assetsRoot,
        string metadataRoot, string? cacheRoot, string? projectRoot)
    {
        Owner = owner;
        Context = context;
        AssetsRoot = assetsRoot;
        MetadataRoot = metadataRoot;
        CacheRoot = cacheRoot;
        ProjectRoot = projectRoot;
    }

    internal AssetManager Owner { get; }
    internal object Context { get; }
    public string AssetsRoot { get; }
    public string MetadataRoot { get; }
    public string? CacheRoot { get; }
    public string? ProjectRoot { get; }
}
