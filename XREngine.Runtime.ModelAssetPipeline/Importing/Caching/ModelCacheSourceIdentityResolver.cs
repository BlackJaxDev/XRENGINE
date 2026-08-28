using System.Security.Cryptography;
using System.Text;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Resolves stable model-source identity and origin without consulting current culture.
/// </summary>
public static class ModelCacheSourceIdentityResolver
{
    public static ModelCacheSourceIdentity Resolve(
        string sourceFilePath,
        string? projectAssetsRoot,
        string? engineAssetsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        if (!Path.IsPathFullyQualified(sourceFilePath))
            throw new ArgumentException(
                "Model cache source paths must be fully qualified before identity resolution.",
                nameof(sourceFilePath));

        string fullPath = Path.GetFullPath(sourceFilePath);
        string resolvedPath = ResolveFinalTarget(fullPath, out bool usedFinalTargetFallback);
        string portableAbsolutePath = ToPortablePath(resolvedPath);
        string caseFoldedAbsolutePath = FoldWindowsPath(portableAbsolutePath);

        ModelCacheSourceOrigin origin;
        string? relativePath;
        if (TryMakeRelative(resolvedPath, projectAssetsRoot, out relativePath))
            origin = ModelCacheSourceOrigin.Project;
        else if (TryMakeRelative(resolvedPath, engineAssetsRoot, out relativePath))
            origin = ModelCacheSourceOrigin.Engine;
        else
        {
            origin = ModelCacheSourceOrigin.External;
            relativePath = null;
        }

        relativePath = relativePath is null ? null : FoldWindowsPath(relativePath);
        string caseFoldedIdentityPath = relativePath ?? caseFoldedAbsolutePath;
        string resolutionMode = usedFinalTargetFallback ? "path-fallback" : "final-target";
        string canonicalIdentity =
            $"v{ModelBinaryCacheVersions.SourceIdentityPolicy}:{origin.ToString().ToLowerInvariant()}:{resolutionMode}:{caseFoldedIdentityPath}";
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalIdentity));
        string identityHash = Convert.ToHexString(digest).ToLowerInvariant();

        return new ModelCacheSourceIdentity(
            origin,
            canonicalIdentity,
            caseFoldedAbsolutePath,
            relativePath,
            identityHash,
            usedFinalTargetFallback);
    }

    private static string ResolveFinalTarget(string fullPath, out bool usedFallback)
    {
        usedFallback = false;
        try
        {
            FileInfo source = new(fullPath);
            FileSystemInfo? finalTarget = source.Exists
                ? source.ResolveLinkTarget(returnFinalTarget: true)
                : null;
            return Path.GetFullPath(finalTarget?.FullName ?? fullPath);
        }
        catch (IOException)
        {
            usedFallback = true;
            return fullPath;
        }
        catch (UnauthorizedAccessException)
        {
            usedFallback = true;
            return fullPath;
        }
        catch (PlatformNotSupportedException)
        {
            usedFallback = true;
            return fullPath;
        }
    }

    private static bool TryMakeRelative(
        string sourcePath,
        string? root,
        out string? relativePath)
    {
        relativePath = null;
        if (string.IsNullOrWhiteSpace(root))
            return false;

        string normalizedRoot = Path.GetFullPath(root);
        string relative = Path.GetRelativePath(normalizedRoot, sourcePath);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            return false;

        relativePath = ToPortablePath(relative);
        return true;
    }

    private static string ToPortablePath(string path)
        => path.Normalize(NormalizationForm.FormC).Replace('\\', '/');

    private static string FoldWindowsPath(string path)
        => path.ToLowerInvariant().Normalize(NormalizationForm.FormC);
}
