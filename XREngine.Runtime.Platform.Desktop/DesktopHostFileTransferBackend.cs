using XREngine.Networking;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Provides desktop file metadata and streams for network file transfers.</summary>
public sealed class DesktopHostFileTransferBackend : IHostFileTransferBackend
{
    public long GetLength(string path) => new FileInfo(path).Length;

    public Stream OpenRead(string path) => File.OpenRead(path);

    public Stream OpenWrite(string path) => File.OpenWrite(path);
}
