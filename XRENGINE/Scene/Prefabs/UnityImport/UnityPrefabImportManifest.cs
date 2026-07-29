namespace XREngine.Scene.Prefabs;

/// <summary>
/// Reproducible dependency and conversion record embedded in an imported Unity prefab.
/// </summary>
[Serializable]
public sealed class UnityPrefabImportManifest
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string EntrySourcePath { get; set; } = string.Empty;
    public string UnityProjectRoot { get; set; } = string.Empty;
    public string? UnityEditorVersion { get; set; }
    public string? OutputAssetPath { get; set; }
    public UnityImportCompletionTier CompletionTier { get; set; }
    public string FingerprintAlgorithm { get; set; } = "SHA-256";
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
    public List<UnityImportDependencyManifestEntry> Dependencies { get; set; } = [];
    public List<UnityImportDiagnostic> Diagnostics { get; set; } = [];
    public List<UnityUnsupportedBehaviourMetadata> UnsupportedBehaviours { get; set; } = [];
    public List<string> OwnedOutputPaths { get; set; } = [];

    /// <summary>
    /// Returns reached dependencies whose exact SHA-256 fingerprint no longer
    /// matches the source Unity project. Unrelated project files are never scanned.
    /// </summary>
    public List<string> GetChangedDependencyPaths()
    {
        var changed = new List<string>();
        foreach (UnityImportDependencyManifestEntry dependency in Dependencies)
        {
            if (dependency.NormalizedPath.StartsWith("missing://", StringComparison.OrdinalIgnoreCase))
                continue;

            string? sourcePath = ResolveDependencySourcePath(dependency.NormalizedPath);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                changed.Add(dependency.NormalizedPath);
                continue;
            }

            var fileInfo = new FileInfo(sourcePath);
            if (fileInfo.Length != dependency.Length)
            {
                changed.Add(sourcePath);
                continue;
            }

            string fingerprint;
            try
            {
                using FileStream stream = File.OpenRead(sourcePath);
                fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            }
            catch (IOException)
            {
                changed.Add(sourcePath);
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                changed.Add(sourcePath);
                continue;
            }

            if (!string.Equals(fingerprint, dependency.Sha256, StringComparison.OrdinalIgnoreCase))
                changed.Add(sourcePath);
        }

        return changed;
    }

    public bool HasDependencyChanges()
        => GetChangedDependencyPaths().Count > 0;

    public string? ResolveDependencySourcePath(string normalizedPath)
    {
        if (Path.IsPathRooted(normalizedPath))
            return Path.GetFullPath(normalizedPath);

        string portable = normalizedPath.Replace('\\', '/');
        if (portable.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(UnityProjectRoot, portable.Replace('/', Path.DirectorySeparatorChar));

        if (!portable.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(UnityProjectRoot, portable.Replace('/', Path.DirectorySeparatorChar));

        string directPath = Path.Combine(UnityProjectRoot, portable.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(directPath))
            return directPath;

        string packageRelative = portable["Packages/".Length..];
        int separatorIndex = packageRelative.IndexOf('/');
        string packageName = separatorIndex < 0 ? packageRelative : packageRelative[..separatorIndex];
        string remainder = separatorIndex < 0 ? string.Empty : packageRelative[(separatorIndex + 1)..];
        string packageCacheRoot = Path.Combine(UnityProjectRoot, "Library", "PackageCache");
        if (!Directory.Exists(packageCacheRoot))
            return directPath;

        string? packageDirectory = Directory.EnumerateDirectories(packageCacheRoot, $"{packageName}@*")
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return packageDirectory is null
            ? directPath
            : Path.Combine(packageDirectory, remainder.Replace('/', Path.DirectorySeparatorChar));
    }
}
