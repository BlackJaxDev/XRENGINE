using System.Text;

namespace XREngine.Rendering;

/// <summary>Reads desktop shader root files and checks that a changed file is readable.</summary>
public interface IShaderSourceFileBackend
{
    /// <summary>Checks for a file with the host's false-on-invalid-path behavior.</summary>
    bool FileExists(string path);

    /// <summary>Checks for a directory with the host's false-on-invalid-path behavior.</summary>
    bool DirectoryExists(string path);

    /// <summary>Gets file metadata if the file exists.</summary>
    bool TryGetFileMetadata(string path, out long lastWriteTimeUtcTicks, out long length);

    /// <summary>Gets the last write time of a directory if it exists.</summary>
    bool TryGetDirectoryLastWriteTimeUtcTicks(string path, out long ticks);

    /// <summary>Reads text with the host's default UTF-8 and BOM behavior.</summary>
    string ReadAllText(string path);

    /// <summary>Gets the file length only after a read handle can be opened.</summary>
    bool TryGetReadableLength(string path, out long length);

    /// <summary>Reads a shader root with its retained text encoding.</summary>
    Task<string> ReadAllTextAsync(string path, Encoding encoding, CancellationToken cancellationToken);
}
