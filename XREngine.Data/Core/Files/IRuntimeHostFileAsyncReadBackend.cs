namespace XREngine.Data;

/// <summary>Provides cancellable asynchronous host-file reads.</summary>
public interface IRuntimeHostFileAsyncReadBackend
{
    /// <summary>Reads all bytes from a host file with cancellation.</summary>
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken);
}
