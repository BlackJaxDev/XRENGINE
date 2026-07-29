using System.Text;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Normalizes producer-reported file identities without culture-sensitive formatting.
/// </summary>
public static class ModelImportPathNormalizer
{
    /// <summary>
    /// Returns an absolute, Unicode-normalized path with portable separators.
    /// </summary>
    public static string NormalizeAbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Path.GetFullPath(path)
            .Replace('\\', '/')
            .ToLowerInvariant()
            .Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Resolves a local URI or file reference relative to its model source.
    /// Data URIs do not represent dependencies and return <see langword="null"/>.
    /// </summary>
    public static string? ResolveLocalReference(string sourceFilePath, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)
            || reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        string decoded = Uri.UnescapeDataString(reference.Trim());
        if (Uri.TryCreate(decoded, UriKind.Absolute, out Uri? absoluteUri))
        {
            if (!absoluteUri.IsFile)
                return null;

            return NormalizeAbsolutePath(absoluteUri.LocalPath);
        }

        string? sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourceFilePath));
        string combined = Path.IsPathRooted(decoded) || string.IsNullOrWhiteSpace(sourceDirectory)
            ? decoded
            : Path.Combine(sourceDirectory, decoded.Replace('/', Path.DirectorySeparatorChar));
        return NormalizeAbsolutePath(combined);
    }
}
