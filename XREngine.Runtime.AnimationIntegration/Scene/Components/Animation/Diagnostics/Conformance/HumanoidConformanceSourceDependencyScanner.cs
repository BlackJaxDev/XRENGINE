using System.Text;
using XREngine.Core.Files;
using XREngine.Data;

namespace XREngine.Components.Animation;

/// <summary>Scans caller-selected production roots for forbidden corpus identities while excluding caller-selected test and documentation roots.</summary>
public static class HumanoidConformanceSourceDependencyScanner
{
    /// <summary>Returns every textual production reference to a forbidden fixture identity or path.</summary>
    public static HumanoidConformanceDependencyScanResult Scan(
        IEnumerable<string> productionRoots,
        IEnumerable<string> allowedRoots,
        IEnumerable<string> forbiddenIdentities)
    {
        ArgumentNullException.ThrowIfNull(productionRoots);
        ArgumentNullException.ThrowIfNull(allowedRoots);
        ArgumentNullException.ThrowIfNull(forbiddenIdentities);

        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        read.EnsureHostFileAccess("Humanoid conformance source scan");
        IRuntimeFileDiscovery? discovery = RuntimeFileDiscoveryServices.Capture(out long generation);
        IAssetMetadataFileBackend directories = discovery as IAssetMetadataFileBackend
            ?? throw new NotSupportedException("AssetSource.MetadataFileUnavailable: the installed asset file system does not provide directory probes.");
        IRuntimeHostFileReadBackend files = discovery as IRuntimeHostFileReadBackend
            ?? throw new NotSupportedException("AssetSource.HostFileReadUnavailable: the installed file system does not provide direct host-file reads.");

        var result = new HumanoidConformanceDependencyScanResult();
        List<string> roots = NormalizeDirectories(productionRoots, "production", requireExisting: true, result, directories);
        List<string> allowed = NormalizeDirectories(allowedRoots, "allowed", requireExisting: false, result, directories);
        List<string> forbidden = NormalizeTokens(forbiddenIdentities);
        if (forbidden.Count == 0)
        {
            result.Errors.Add("The source dependency scan was given no forbidden fixture identities.");
            return Complete(read, discovery, generation, result);
        }

        result.ScannedRoots.AddRange(roots);

        for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
        {
            try
            {
                foreach (string path in discovery!.EnumerateFiles(roots[rootIndex], "*", SearchOption.AllDirectories))
                {
                    if (IsAllowed(path, allowed) || !IsTextCandidate(path))
                        continue;
                    result.ScannedFileCount++;
                    ScanFile(path, forbidden, result, files);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Errors.Add($"Could not enumerate production root '{roots[rootIndex]}': {ex.Message}");
            }
        }

        if (result.ScannedFileCount == 0)
            result.Errors.Add("The source dependency scan did not read any production text files.");

        result.Findings.Sort(static (left, right) =>
        {
            int pathComparison = StringComparer.Ordinal.Compare(left.FilePath, right.FilePath);
            return pathComparison != 0 ? pathComparison : left.Line.CompareTo(right.Line);
        });
        return Complete(read, discovery, generation, result);
    }

    private static HumanoidConformanceDependencyScanResult Complete(RuntimeAssetReadLease read,
        IRuntimeFileDiscovery? discovery, long generation, HumanoidConformanceDependencyScanResult result)
    {
        using IDisposable publication = read.BeginPublication();
        if (!RuntimeFileDiscoveryServices.IsCurrent(discovery, generation))
            throw new OperationCanceledException("AssetSource.StaleFileDiscovery: the source scan provider changed during the scan.");
        return result;
    }

    private static void ScanFile(string path, IReadOnlyList<string> forbidden, HumanoidConformanceDependencyScanResult result,
        IRuntimeHostFileReadBackend files)
    {
        try
        {
            using Stream input = files.OpenRead(path, FileShare.Read);
            using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            int lineNumber = 0;
            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                for (int i = 0; i < forbidden.Count; i++)
                {
                    if (line.Contains(forbidden[i], StringComparison.OrdinalIgnoreCase))
                        result.Findings.Add(new HumanoidConformanceDependencyFinding { FilePath = path, Identity = forbidden[i], Line = lineNumber });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            result.Errors.Add($"Could not read production source '{path}': {ex.Message}");
        }
    }

    private static List<string> NormalizeDirectories(
        IEnumerable<string> roots,
        string kind,
        bool requireExisting,
        HumanoidConformanceDependencyScanResult result,
        IAssetMetadataFileBackend directories)
    {
        var normalized = new List<string>();
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                if (requireExisting)
                    result.Errors.Add($"The source dependency scan contains an empty {kind} root.");
                continue;
            }
            string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!directories.DirectoryExists(fullPath))
            {
                if (requireExisting)
                    result.Errors.Add($"The source dependency scan {kind} root '{fullPath}' does not exist.");
                continue;
            }
            if (!normalized.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                normalized.Add(fullPath);
        }

        normalized.Sort(StringComparer.Ordinal);
        return normalized;
    }

    private static List<string> NormalizeTokens(IEnumerable<string> identities)
    {
        var tokens = new List<string>();
        foreach (string identity in identities)
        {
            if (!string.IsNullOrWhiteSpace(identity) && !tokens.Contains(identity, StringComparer.OrdinalIgnoreCase))
                tokens.Add(identity);
        }

        tokens.Sort(StringComparer.Ordinal);
        return tokens;
    }

    private static bool IsAllowed(string path, IReadOnlyList<string> allowedRoots)
    {
        for (int i = 0; i < allowedRoots.Count; i++)
        {
            string prefix = allowedRoots[i] + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsTextCandidate(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jsonc", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase);
    }
}
