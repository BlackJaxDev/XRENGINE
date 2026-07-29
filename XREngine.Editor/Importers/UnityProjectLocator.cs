namespace XREngine.Scene.Importers;

/// <summary>
/// Locates and validates Unity project roots without falling back to an arbitrary directory.
/// </summary>
public static class UnityProjectLocator
{
    public static UnityProjectLocation Locate(string sourcePath, string? explicitProjectOrAssetsRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        string sourceFullPath = Path.GetFullPath(sourcePath);
        string projectRoot = explicitProjectOrAssetsRoot is null
            ? FindOwningProjectRoot(sourceFullPath)
            : NormalizeExplicitRoot(explicitProjectOrAssetsRoot);
        string assetsRoot = Path.Combine(projectRoot, "Assets");
        if (!Directory.Exists(assetsRoot))
        {
            throw new DirectoryNotFoundException(
                $"'{projectRoot}' is not a Unity project because it has no Assets directory. " +
                "Select the Unity project root or its Assets directory explicitly.");
        }

        string versionPath = Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt");
        return new UnityProjectLocation
        {
            ProjectRoot = projectRoot,
            AssetsRoot = assetsRoot,
            UnityEditorVersion = TryReadEditorVersion(versionPath),
            HasProjectVersionFile = File.Exists(versionPath),
        };
    }

    public static bool TryLocate(
        string sourcePath,
        out UnityProjectLocation? location,
        out string? error,
        string? explicitProjectOrAssetsRoot = null)
    {
        try
        {
            location = Locate(sourcePath, explicitProjectOrAssetsRoot);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException)
        {
            location = null;
            error = ex.Message;
            return false;
        }
    }

    private static string FindOwningProjectRoot(string sourcePath)
    {
        var current = new DirectoryInfo(
            Directory.Exists(sourcePath)
                ? sourcePath
                : Path.GetDirectoryName(sourcePath) ?? sourcePath);

        while (current is not null)
        {
            if (string.Equals(current.Name, "Assets", StringComparison.OrdinalIgnoreCase))
                return current.Parent?.FullName ?? throw new DirectoryNotFoundException(
                    $"The Assets directory '{current.FullName}' has no parent Unity project directory.");

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate a Unity project for '{sourcePath}'. The selected asset must be below an " +
            "Assets directory, or an explicit Unity project/Assets directory must be supplied.");
    }

    private static string NormalizeExplicitRoot(string explicitRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(explicitRoot);

        string normalized = Path.GetFullPath(explicitRoot);
        if (string.Equals(Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar)), "Assets", StringComparison.OrdinalIgnoreCase))
            return Directory.GetParent(normalized)?.FullName ?? normalized;

        return normalized;
    }

    private static string? TryReadEditorVersion(string versionPath)
    {
        if (!File.Exists(versionPath))
            return null;

        foreach (string line in File.ReadLines(versionPath))
        {
            const string prefix = "m_EditorVersion:";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line[prefix.Length..].Trim();
        }

        return null;
    }
}
