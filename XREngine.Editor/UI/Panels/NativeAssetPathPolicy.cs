using System.IO;

namespace XREngine.Editor;

/// <summary>Pure path checks for native asset mutations; no editor services required.</summary>
internal static class NativeAssetPathPolicy
{
    public static bool PathsEqual(string left, string right)
        => string.Equals(left, right, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static string RequireUnderRoot(string root, string path, bool allowRoot)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(path);
        string trimmedRoot = Path.TrimEndingDirectorySeparator(fullRoot);
        string prefix = Path.EndsInDirectorySeparator(trimmedRoot)
            ? trimmedRoot : trimmedRoot + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!(allowRoot && PathsEqual(Path.TrimEndingDirectorySeparator(fullPath), trimmedRoot)) &&
            !fullPath.StartsWith(prefix, comparison))
            throw new InvalidOperationException("Asset path is outside the selected asset root.");

        // Reject links anywhere in the existing path, including an ancestor of the root.
        // Otherwise a lexically contained destination could write outside the project.
        RejectReparseSegments(fullPath);
        return fullPath;
    }

    public static string ValidateName(string name)
    {
        string valid = name.Trim();
        if (valid.Length == 0 || valid is "." or ".." || valid.EndsWith('.') || valid.EndsWith(' ') ||
            valid.IndexOfAny("<>:\"/\\|?*".ToCharArray()) >= 0 || valid.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Enter a valid file or folder name.", nameof(name));
        return valid;
    }

    private static void RejectReparseSegments(string fullPath)
    {
        string current = Path.GetPathRoot(fullPath) ?? throw new InvalidOperationException("Asset path has no root.");
        foreach (string segment in fullPath[current.Length..].Split(Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Asset mutations cannot traverse a linked directory or file.");
        }
    }
}
