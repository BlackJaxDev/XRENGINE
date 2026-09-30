namespace XREngine.Core.Files;

/// <summary>Desktop asset source using DirectStorage when enabled by the application policy.</summary>
public sealed class DirectStorageAssetSource : IRuntimeAssetSource
{
    public bool IsAccelerated => NativeDirectStorageIO.IsEnabled;
    public string Status => NativeDirectStorageIO.Status;
    public IAssetFileSystem? FileSystem => AssetFileSystemServices.Current;
    public bool Exists(string path) => File.Exists(path);
    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }
    public byte[] ReadAllBytes(string path) => NativeDirectStorageIO.ReadAllBytes(path);
    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
        => NativeDirectStorageIO.ReadAllBytesAsync(path, cancellationToken);
    public byte[] ReadRange(string path, long offset, int length)
        => NativeDirectStorageIO.ReadRange(path, offset, length);
    public Task<byte[]> ReadRangeAsync(string path, long offset, int length, CancellationToken cancellationToken = default)
        => NativeDirectStorageIO.ReadRangeAsync(path, offset, length, cancellationToken);
    public unsafe bool TryReadInto(string path, long offset, int length, void* destination, CancellationToken cancellationToken = default)
        => NativeDirectStorageIO.TryReadInto(path, offset, length, destination, cancellationToken);
    public unsafe bool TryReadFileInto(string path, void* destination, int destinationSize, CancellationToken cancellationToken = default)
        => NativeDirectStorageIO.TryReadFileInto(path, destination, destinationSize, cancellationToken);
    public IAssetReadBatch CreateBatch() => new NativeDirectStorageIO.ReadBatch();
}
