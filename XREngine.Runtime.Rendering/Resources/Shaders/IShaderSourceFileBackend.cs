using System.Text;

namespace XREngine.Rendering;

/// <summary>Reads desktop shader root files and checks that a changed file is readable.</summary>
public interface IShaderSourceFileBackend
{
    /// <summary>Gets the file length only after a read handle can be opened.</summary>
    bool TryGetReadableLength(string path, out long length);

    /// <summary>Reads a shader root with its retained text encoding.</summary>
    Task<string> ReadAllTextAsync(string path, Encoding encoding, CancellationToken cancellationToken);
}
