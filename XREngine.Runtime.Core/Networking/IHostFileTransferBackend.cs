namespace XREngine.Networking;

/// <summary>Provides operating-system file metadata and streams for network file transfers.</summary>
public interface IHostFileTransferBackend
{
    /// <summary>Gets the file length before a transfer opens its transport.</summary>
    long GetLength(string path);

    /// <summary>Opens a source file. The caller owns the returned stream.</summary>
    Stream OpenRead(string path);

    /// <summary>Opens a destination file. The caller owns the returned stream.</summary>
    Stream OpenWrite(string path);
}
