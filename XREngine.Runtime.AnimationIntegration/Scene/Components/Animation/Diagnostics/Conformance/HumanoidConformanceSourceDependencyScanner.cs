using System.Text;

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

        var result = new HumanoidConformanceDependencyScanResult();
        List<string> roots = NormalizeDirectories(productionRoots, "production", requireExisting: true, result);
        List<string> allowed = NormalizeDirectories(allowedRoots, "allowed", requireExisting: false, result);
        List<string> forbidden = NormalizeTokens(forbiddenIdentities);
        if (forbidden.Count == 0)
        {
            result.Errors.Add("The source dependency scan was given no forbidden fixture identities.");
            return result;
        }

        result.ScannedRoots.AddRange(roots);

        for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(roots[rootIndex], "*", SearchOption.AllDirectories))
                {
                    if (IsAllowed(path, allowed) || !IsTextCandidate(path))
                        continue;
                    result.ScannedFileCount++;
                    ScanFile(path, forbidden, result);
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
        return result;
    }

    private static void ScanFile(string path, IReadOnlyList<string> forbidden, HumanoidConformanceDependencyScanResult result)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
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
        HumanoidConformanceDependencyScanResult result)
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
            if (!Directory.Exists(fullPath))
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
