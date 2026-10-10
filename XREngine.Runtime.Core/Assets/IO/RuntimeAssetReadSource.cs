namespace XREngine.Core.Files;

/// <summary>Exposes a captured Core asset source to the lower-level Data read contract.</summary>
internal sealed class RuntimeAssetReadSource(IRuntimeAssetSource source) : XREngine.Data.IRuntimeAssetReadSource
{
    internal IRuntimeAssetSource Source { get; } = source;
    public bool SupportsSynchronousReads => Source.SupportsSynchronousReads;
    public bool SupportsHostFileAccess => Source.SupportsSynchronousReads && Source is not IRuntimeAssetCatalog;
    public bool Exists(string path)
        => Source is IRuntimeAssetCatalog catalog ? catalog.TryGetAsset(path, out _) : Source.Exists(path);
    public byte[] ReadAllBytes(string path) => Source.ReadAllBytes(path);
    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
        => Source.ReadAllBytesAsync(path, cancellationToken);
}
