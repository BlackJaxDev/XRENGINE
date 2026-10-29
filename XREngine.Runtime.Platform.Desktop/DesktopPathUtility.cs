namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Prepares directories for desktop file paths.</summary>
public static class DesktopPathUtility
{
    /// <summary>Creates the directory for a path when it does not exist.</summary>
    public static void EnsureDirPathExists(string path)
    {
        if (path.Contains(Path.AltDirectorySeparatorChar))
            path = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        if (Path.HasExtension(path))
            path = Path.GetDirectoryName(path) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(path))
            return;

        string fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
            return;
        Directory.CreateDirectory(fullPath);
    }
}
