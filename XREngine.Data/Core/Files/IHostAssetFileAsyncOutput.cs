namespace XREngine.Data;

/// <summary>Writes host asset bytes asynchronously.</summary>
public interface IHostAssetFileAsyncOutput
{
    /// <summary>Creates the parent directory and writes all bytes with cancellation.</summary>
    Task WriteAllBytesWithParentDirectoryAsync(string path, byte[] bytes, CancellationToken cancellationToken);
}
