using System.Diagnostics.CodeAnalysis;

namespace XREngine.Core.Files;

/// <summary>
/// Creates and resolves portable paths for assets rooted in a game or engine asset tree.
/// </summary>
public static class AssetReferencePath
{
    public const string GamePrefix = "game://";
    public const string EnginePrefix = "engine://";

    /// <summary>
    /// Returns whether <paramref name="value"/> uses a recognized portable asset-root prefix.
    /// </summary>
    public static bool IsPortable(string? value)
        => value?.StartsWith(GamePrefix, StringComparison.OrdinalIgnoreCase) == true
        || value?.StartsWith(EnginePrefix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Creates a portable reference when <paramref name="assetPath"/> is contained by
    /// <paramref name="assetRoot"/>.
    /// </summary>
    public static bool TryCreate(
        string? assetRoot,
        string assetPath,
        string prefix,
        [NotNullWhen(true)] out string? reference)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(assetRoot)
            || string.IsNullOrWhiteSpace(assetPath)
            || string.IsNullOrWhiteSpace(prefix))
        {
            return false;
        }

        try
        {
            string root = Path.GetFullPath(assetRoot);
            string fullPath = Path.GetFullPath(assetPath);
            string relativePath = Path.GetRelativePath(root, fullPath);
            if (!IsContainedRelativePath(relativePath))
            {
                return false;
            }

            reference = string.Concat(prefix, relativePath.Replace(Path.DirectorySeparatorChar, '/'));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves a portable reference while rejecting paths that escape the selected asset root.
    /// </summary>
    public static bool TryResolve(
        string reference,
        string? gameAssetsRoot,
        string? engineAssetsRoot,
        [NotNullWhen(true)] out string? assetPath)
    {
        assetPath = null;
        if (string.IsNullOrWhiteSpace(reference))
            return false;

        string? root;
        string relativePath;
        if (reference.StartsWith(GamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            root = gameAssetsRoot;
            relativePath = reference[GamePrefix.Length..];
        }
        else if (reference.StartsWith(EnginePrefix, StringComparison.OrdinalIgnoreCase))
        {
            root = engineAssetsRoot;
            relativePath = reference[EnginePrefix.Length..];
        }
        else
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath))
            return false;

        try
        {
            string fullRoot = Path.GetFullPath(root);
            string fullPath = Path.GetFullPath(Path.Combine(
                fullRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string containmentCheck = Path.GetRelativePath(fullRoot, fullPath);
            if (!IsContainedRelativePath(containmentCheck))
            {
                return false;
            }

            assetPath = fullPath;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsContainedRelativePath(string path)
        => path != "."
        && path != ".."
        && !path.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !path.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
        && !Path.IsPathRooted(path);
}
