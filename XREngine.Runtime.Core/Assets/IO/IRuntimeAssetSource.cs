namespace XREngine.Core.Files;

/// <summary>Application-installed asset I/O, including optional accelerated range reads.</summary>
public interface IRuntimeAssetSource
{
    bool IsAccelerated { get; }
    string Status { get; }
    IAssetFileSystem? FileSystem { get; }
    bool Exists(string path);
    ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);
    byte[] ReadAllBytes(string path);
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default);
    byte[] ReadRange(string path, long offset, int length);
    Task<byte[]> ReadRangeAsync(string path, long offset, int length, CancellationToken cancellationToken = default);
    unsafe bool TryReadInto(string path, long offset, int length, void* destination, CancellationToken cancellationToken = default);
    unsafe bool TryReadFileInto(string path, void* destination, int destinationSize, CancellationToken cancellationToken = default);
    IAssetReadBatch CreateBatch();
}
