using XREngine.Data;

namespace XREngine.Rendering.GI.DDGI;

public sealed partial class DDGIBakedAsset
{
    /// <summary>Reads a baked volume through one captured source using its asynchronous byte API.</summary>
    public static async Task<DDGIBakedAsset> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture(cancellationToken);
        return await LoadAsync(read, path).ConfigureAwait(false);
    }

    // A pipeline owner keeps this exact source lease until its final adoption decision.
    internal static async Task<DDGIBakedAsset> LoadAsync(RuntimeAssetReadLease read, string path)
    {
        if (read.TryGetExists(path, out bool exists) && !exists)
            throw new FileNotFoundException($"Baked DDGI asset file not found: {path}", path);

        byte[] bytes = await read.ReadAllBytesAsync(path).ConfigureAwait(false);
        using IDisposable publication = read.BeginPublication();
        using MemoryStream stream = new(bytes, writable: false);
        DDGIBakedAsset asset = Load(stream);
        read.EnsureCurrent();
        return asset;
    }
}
