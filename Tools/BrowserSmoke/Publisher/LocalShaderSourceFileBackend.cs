using System.Text;
using XREngine.Rendering;

/// <summary>Reads authored shader files for the local publisher.</summary>
internal sealed class LocalShaderSourceFileBackend : IShaderSourceFileBackend
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool TryGetFileMetadata(string path, out long lastWriteTimeUtcTicks, out long length)
    {
        FileInfo file = new(path);
        if (!file.Exists)
        {
            lastWriteTimeUtcTicks = 0;
            length = 0;
            return false;
        }

        lastWriteTimeUtcTicks = file.LastWriteTimeUtc.Ticks;
        length = file.Length;
        return true;
    }

    public bool TryGetDirectoryLastWriteTimeUtcTicks(string path, out long ticks)
    {
        if (!Directory.Exists(path))
        {
            ticks = 0;
            return false;
        }

        ticks = Directory.GetLastWriteTimeUtc(path).Ticks;
        return true;
    }

    public string ReadAllText(string path) => File.ReadAllText(path);

    public bool TryGetReadableLength(string path, out long length)
    {
        FileInfo file = new(path);
        if (!file.Exists)
        {
            length = 0;
            return false;
        }

        length = file.Length;
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return true;
    }

    public Task<string> ReadAllTextAsync(string path, Encoding encoding, CancellationToken cancellationToken)
        => File.ReadAllTextAsync(path, encoding, cancellationToken);
}
