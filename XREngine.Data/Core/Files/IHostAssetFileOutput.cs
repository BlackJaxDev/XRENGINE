using System.Text;

namespace XREngine.Data;

/// <summary>Writes authoring asset files through the installed host.</summary>
public interface IHostAssetFileOutput
{
    /// <summary>Creates a file and its parent directory when necessary. The caller owns the stream.</summary>
    Stream CreateFileWithParentDirectory(string path);

    /// <summary>Writes text with the specified encoding.</summary>
    void WriteAllText(string path, string text, Encoding encoding);

    /// <summary>Writes text asynchronously with the specified encoding.</summary>
    Task WriteAllTextAsync(string path, string text, Encoding encoding);
}
