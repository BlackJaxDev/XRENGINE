using System.Text;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Reads shader source files through desktop file handles.</summary>
public sealed class DesktopShaderSourceFileBackend : IShaderSourceFileBackend
{
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
