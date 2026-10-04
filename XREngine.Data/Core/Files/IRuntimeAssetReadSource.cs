namespace XREngine.Data;

/// <summary>Provides raw asset reads without a dependency on the runtime or a host file system.</summary>
public interface IRuntimeAssetReadSource
{
    bool SupportsSynchronousReads { get; }
    bool SupportsHostFileAccess { get; }
    bool Exists(string path);
    byte[] ReadAllBytes(string path);
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default);
}
