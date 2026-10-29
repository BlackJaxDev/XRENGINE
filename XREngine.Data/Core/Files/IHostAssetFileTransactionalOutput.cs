using System.Text;

namespace XREngine.Data;

/// <summary>Writes an asset through a temporary file and replaces the destination.</summary>
public interface IHostAssetFileTransactionalOutput
{
    /// <summary>Serializes to an adjacent temporary file and commits it.</summary>
    void WriteTextWithReplacement(string path, Encoding encoding, Action<TextWriter> serialize);

    /// <summary>Serializes text, writes it to an adjacent temporary file, and commits it.</summary>
    Task WriteTextWithReplacementAsync(string path, Encoding encoding, Func<string> serialize);
}
