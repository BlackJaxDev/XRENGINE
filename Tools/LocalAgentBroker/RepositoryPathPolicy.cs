using System.Collections.ObjectModel;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Resolves source-focused repository paths without permitting traversal,
/// reparse-point escape, generated output, or common secret containers.
/// </summary>
internal sealed class RepositoryPathPolicy
{
    private static readonly ReadOnlySet<string> s_blockedDirectoryNames = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".hg", ".svn", ".vs", ".idea", "Build", "bin", "obj",
            "node_modules", "packages", ".secrets", "secrets",
        });

    private static readonly ReadOnlySet<string> s_blockedFileNames = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "credentials.json", "secrets.json", "session.json", "NuGet.Config",
            "id_rsa", "id_ed25519",
        });

    private static readonly ReadOnlySet<string> s_blockedExtensions = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".key", ".pem", ".pfx", ".p12", ".snk", ".kdbx",
        });

    private static readonly ReadOnlySet<string> s_allowedExtensions = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".bat", ".c", ".cmd", ".comp", ".config", ".cpp", ".cs",
            ".cshtml", ".csproj", ".css", ".csv", ".editorconfig", ".frag",
            ".geom", ".gitignore", ".gitattributes", ".glsl", ".h", ".hpp",
            ".htm", ".html", ".hlsl", ".inl", ".js", ".json", ".jsonc",
            ".jsx", ".lock", ".md", ".natvis", ".nuspec", ".props", ".proto",
            ".ps1", ".psd1", ".psm1", ".py", ".razor", ".resx", ".ruleset",
            ".scss", ".sh", ".sln", ".slnx", ".sql", ".targets", ".tesc",
            ".tese", ".toml", ".ts", ".tsv", ".tsx", ".txt", ".vert",
            ".xaml", ".xml", ".yaml", ".yml",
        });

    private static readonly ReadOnlySet<string> s_allowedExtensionlessNames = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".editorconfig", ".gitattributes", ".gitignore", "Dockerfile", "Makefile",
        });

    private static readonly ReadOnlySet<string> s_reservedDeviceNames = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CLOCK$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        });

    private readonly string _repositoryRoot;
    private readonly string _repositoryRootPrefix;

    public RepositoryPathPolicy(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        _repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (!Directory.Exists(_repositoryRoot))
            throw new DirectoryNotFoundException("The configured repository root does not exist.");
        _repositoryRootPrefix = _repositoryRoot + Path.DirectorySeparatorChar;
    }

    public IReadOnlyList<string> ResolveAllowedRoots(IReadOnlyList<string> relativeRoots)
    {
        ArgumentNullException.ThrowIfNull(relativeRoots);
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relativeRoot in relativeRoots)
        {
            string fullPath = ResolveDirectory(relativeRoot);
            resolved[ToRelativePath(fullPath)] = fullPath;
        }

        return resolved
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => pair.Value)
            .ToArray();
    }

    public string ResolveDirectory(string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath, allowRepositoryRoot: true);
        string fullPath = normalized.Length == 0
            ? _repositoryRoot
            : ResolveContainedFullPath(normalized);
        if (!Directory.Exists(fullPath))
            throw new ArgumentException($"Repository directory '{DisplayPath(normalized)}' was not found.");

        EnsureNoReparsePoints(fullPath);
        return fullPath;
    }

    public string ResolveTextFile(
        string relativePath,
        IReadOnlyList<string>? allowedRootFullPaths = null)
    {
        string normalized = NormalizeRelativePath(relativePath, allowRepositoryRoot: false);
        EnsureTextFileNameAllowed(normalized);
        string fullPath = ResolveContainedFullPath(normalized);
        if (allowedRootFullPaths is not null
            && !allowedRootFullPaths.Any(root => IsSameOrDescendant(fullPath, root)))
        {
            throw new ArgumentException($"Repository file '{normalized}' is outside the authorized roots.");
        }
        if (!File.Exists(fullPath))
            throw new ArgumentException($"Repository file '{normalized}' was not found.");

        FileAttributes attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new ArgumentException($"Repository path '{normalized}' is not an ordinary text file.");
        EnsureNoReparsePoints(fullPath);
        return fullPath;
    }

    public string ToRelativePath(string fullPath)
        => Path.GetRelativePath(_repositoryRoot, fullPath).Replace('\\', '/');

    public bool IsTextFileCandidate(string fullPath)
    {
        try
        {
            string relativePath = ToRelativePath(fullPath);
            if (relativePath.StartsWith("../", StringComparison.Ordinal)
                || string.Equals(relativePath, "..", StringComparison.Ordinal))
            {
                return false;
            }

            string normalized = NormalizeRelativePath(relativePath, allowRepositoryRoot: false);
            EnsureTextFileNameAllowed(normalized);
            FileAttributes attributes = File.GetAttributes(fullPath);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return false;
        }
    }

    public bool IsDirectoryTraversalAllowed(string fullPath)
    {
        try
        {
            string relativePath = ToRelativePath(fullPath);
            if (relativePath.StartsWith("../", StringComparison.Ordinal)
                || string.Equals(relativePath, "..", StringComparison.Ordinal))
            {
                return false;
            }

            _ = NormalizeRelativePath(relativePath, allowRepositoryRoot: false);
            FileAttributes attributes = File.GetAttributes(fullPath);
            return (attributes & FileAttributes.Directory) != 0
                && (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return false;
        }
    }

    public FileStream OpenReadValidated(string fullPath)
    {
        EnsureContained(fullPath);
        EnsureNoReparsePoints(fullPath);
        FileAttributes attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("The repository path is not an ordinary file.");

        return new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16_384,
            FileOptions.SequentialScan);
    }

    private string NormalizeRelativePath(string path, bool allowRepositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!string.Equals(path, path.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Repository paths cannot have leading or trailing whitespace.");
        if (Path.IsPathRooted(path)
            || path.StartsWith('\\')
            || path.StartsWith('/')
            || path.Contains(':', StringComparison.Ordinal)
            || path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("Repository paths must be ordinary repository-relative paths.");
        }

        string[] segments = path.Split(['\\', '/'], StringSplitOptions.None);
        if (allowRepositoryRoot && segments.Length == 1 && segments[0] == ".")
            return string.Empty;
        if (segments.Length == 0 || segments.Any(static segment => segment.Length == 0))
            throw new ArgumentException("Repository paths cannot contain empty segments.");

        foreach (string segment in segments)
        {
            if (segment is "." or "..")
                throw new ArgumentException("Repository paths cannot contain traversal segments.");
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
                throw new ArgumentException("Repository path segments cannot end in a space or period.");
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || segment.Contains('*')
                || segment.Contains('?'))
            {
                throw new ArgumentException("Repository paths contain an invalid character.");
            }

            string deviceName = Path.GetFileNameWithoutExtension(segment);
            if (s_reservedDeviceNames.Contains(deviceName))
                throw new ArgumentException("Repository paths cannot use reserved device names.");
            if (s_blockedDirectoryNames.Contains(segment))
                throw new ArgumentException($"Repository path segment '{segment}' is excluded.");
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private string ResolveContainedFullPath(string normalizedRelativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(_repositoryRoot, normalizedRelativePath));
        EnsureContained(fullPath);
        return fullPath;
    }

    private void EnsureContained(string fullPath)
    {
        string normalized = Path.GetFullPath(fullPath);
        if (!string.Equals(normalized, _repositoryRoot, StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith(_repositoryRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Repository path escaped the configured repository root.");
        }
    }

    private void EnsureNoReparsePoints(string fullPath)
    {
        EnsureContained(fullPath);
        string relativePath = Path.GetRelativePath(_repositoryRoot, fullPath);
        if (relativePath == ".")
            return;

        string current = _repositoryRoot;
        foreach (string segment in relativePath.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException(
                    $"Repository path '{ToRelativePath(current)}' crosses a reparse point.");
            }
        }
    }

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string prefix = normalizedRoot + Path.DirectorySeparatorChar;
        string normalizedCandidate = Path.GetFullPath(candidate);
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureTextFileNameAllowed(string normalizedRelativePath)
    {
        string fileName = Path.GetFileName(normalizedRelativePath);
        if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || s_blockedFileNames.Contains(fileName))
        {
            throw new ArgumentException($"Repository file '{fileName}' is excluded by the secret-file policy.");
        }

        string extension = Path.GetExtension(fileName);
        if (s_blockedExtensions.Contains(extension))
            throw new ArgumentException($"Repository file extension '{extension}' is excluded.");
        if (extension.Length == 0)
        {
            if (!s_allowedExtensionlessNames.Contains(fileName))
                throw new ArgumentException($"Repository file '{fileName}' is not an allowed text type.");
            return;
        }
        if (!s_allowedExtensions.Contains(extension))
            throw new ArgumentException($"Repository file extension '{extension}' is not an allowed text type.");
    }

    private static string DisplayPath(string normalizedPath)
        => normalizedPath.Length == 0 ? "." : normalizedPath.Replace('\\', '/');
}
