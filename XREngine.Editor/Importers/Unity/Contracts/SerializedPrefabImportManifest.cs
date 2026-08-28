namespace XREngine.Scene.Prefabs;

/// <summary>
/// Reproducible dependency and conversion record embedded in an imported Unity prefab.
/// </summary>
[Serializable]
public sealed class SerializedPrefabImportManifest
{
    public const int CurrentFormatVersion = 2;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string EntrySourcePath { get; set; } = string.Empty;
    public string SourceProjectRoot { get; set; } = string.Empty;
    public string? SourceEditorVersion { get; set; }
    public string? OutputAssetPath { get; set; }
    public SourceImportCompletionTier CompletionTier { get; set; }
    public string FingerprintAlgorithm { get; set; } = "SHA-256";
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
    public List<SourceImportDependencyManifestEntry> Dependencies { get; set; } = [];
    public List<SourceImportDiagnostic> Diagnostics { get; set; } = [];
    public List<SerializedAnimatorRecord> Animators { get; set; } = [];
    public List<SerializedAvatarAnimationGraphRecord> AvatarAnimationGraphs { get; set; } = [];
    public List<UnsupportedSourceBehaviourMetadata> UnsupportedBehaviours { get; set; } = [];
    public List<string> OwnedOutputPaths { get; set; } = [];

    /// <summary>
    /// Returns reached dependencies whose exact SHA-256 fingerprint no longer
    /// matches the source Unity project. Unrelated project files are never scanned.
    /// </summary>
    public List<string> GetChangedDependencyPaths()
    {
        var changed = new List<string>();
        foreach (SourceImportDependencyManifestEntry dependency in Dependencies)
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

    /// <summary>
    /// Computes a path-independent digest of the exact source dependency graph
    /// used by this conversion. File locations and timestamps are deliberately
    /// excluded so a move does not invalidate identical content.
    /// </summary>
    public string ComputeSourceContentSha256()
    {
        var canonical = new System.Text.StringBuilder(Dependencies.Count * 160);
        canonical.Append(FormatVersion).Append('\n');
        foreach (SourceImportDependencyManifestEntry dependency in Dependencies
            .OrderBy(static item => item.SourceGuid ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static item => item.LocalFileId ?? long.MinValue)
            .ThenBy(static item => item.Kind)
            .ThenBy(static item => item.Sha256, StringComparer.OrdinalIgnoreCase))
        {
            AppendCanonical(canonical, dependency.SourceGuid);
            canonical.Append(dependency.LocalFileId ?? long.MinValue).Append('\n');
            canonical.Append((int)dependency.Kind).Append('\n');
            AppendCanonical(canonical, dependency.ReferringProperty);
            AppendCanonical(canonical, dependency.Sha256.ToUpperInvariant());
            canonical.Append((int)dependency.Outcome).Append('\n');
        }

        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendCanonical(System.Text.StringBuilder builder, string? value)
    {
        value ??= string.Empty;
        builder.Append(value.Length).Append(':').Append(value).Append('\n');
    }

    public string? ResolveDependencySourcePath(string normalizedPath)
    {
        if (Path.IsPathRooted(normalizedPath))
            return Path.GetFullPath(normalizedPath);

        string portable = normalizedPath.Replace('\\', '/');
        if (portable.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(SourceProjectRoot, portable.Replace('/', Path.DirectorySeparatorChar));

        if (!portable.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(SourceProjectRoot, portable.Replace('/', Path.DirectorySeparatorChar));

        string directPath = Path.Combine(SourceProjectRoot, portable.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(directPath))
            return directPath;

        string packageRelative = portable["Packages/".Length..];
        int separatorIndex = packageRelative.IndexOf('/');
        string packageName = separatorIndex < 0 ? packageRelative : packageRelative[..separatorIndex];
        string remainder = separatorIndex < 0 ? string.Empty : packageRelative[(separatorIndex + 1)..];
        string packageCacheRoot = Path.Combine(SourceProjectRoot, "Library", "PackageCache");
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
