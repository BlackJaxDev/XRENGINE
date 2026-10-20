using XREngine.Data;

namespace XREngine.Rendering.Models.Gaussian;

public sealed partial class GaussianSplatCloud
{
    /// <summary>Reads a cloud from one captured asset source without scheduling a blocking file job.</summary>
    public static async Task<GaussianSplatCloud?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture(cancellationToken);
        return await LoadAsync(read, path).ConfigureAwait(false);
    }

    // A runtime owner keeps this exact source lease until its final adoption decision.
    internal static async Task<GaussianSplatCloud?> LoadAsync(RuntimeAssetReadLease read, string path)
    {
        bool hasExistence = read.TryGetExists(path, out bool exists);
        if (hasExistence && !exists)
            return null;

        byte[] bytes;
        try { bytes = await read.ReadAllBytesAsync(path).ConfigureAwait(false); }
        catch (IOException error) when (!hasExistence && error is FileNotFoundException or DirectoryNotFoundException)
        {
            read.EnsureCurrent();
            return null;
        }
        return DecodeCapturedBytes(read, path, bytes);
    }

    internal void DiscardUnpublished() => AbortFailedConstruction();

    private static GaussianSplatCloud DecodeCapturedBytes(RuntimeAssetReadLease read, string path, byte[] bytes)
    {
        // Source callbacks run outside its lock. The reservation prevents a source
        // replacement from publishing a cloud decoded from an obsolete installation.
        using IDisposable publication = read.BeginPublication();
        using MemoryStream stream = new(bytes, writable: false);
        GaussianSplatCloud cloud = Load(stream);
        try
        {
            cloud.Name = Path.GetFileNameWithoutExtension(path);
            read.EnsureCurrent();
            return cloud;
        }
        catch
        {
            try { cloud.AbortFailedConstruction(); }
            catch (Exception cleanupError)
            {
                System.Diagnostics.Trace.TraceError("Failed to release an unpublished Gaussian cloud: {0}", cleanupError);
            }
            throw;
        }
    }
}
