using System.Text;
using XREngine.Data;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Writes authoring asset files to desktop host paths.</summary>
public sealed class DesktopHostAssetFileOutput : IHostAssetFileOutput, IHostAssetFileAsyncOutput, IHostAssetFileOpenWrite
{
    public Stream OpenWrite(string path) => File.OpenWrite(path);

    public Stream CreateFileWithParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
        return File.Create(path);
    }

    public void WriteAllText(string path, string text, Encoding encoding)
        => File.WriteAllText(path, text, encoding);

    public Task WriteAllTextAsync(string path, string text, Encoding encoding)
        => File.WriteAllTextAsync(path, text, encoding);

    public Task WriteAllBytesWithParentDirectoryAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        return File.WriteAllBytesAsync(path, bytes, cancellationToken);
    }
}
