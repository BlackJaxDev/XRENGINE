using System.Text;

namespace XREngine.UnitTests;

/// <summary>
/// Reads repository source for contract tests without coupling those tests to
/// the physical file layout of partial types.
/// </summary>
internal static class SourceContractWorkspace
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    /// <summary>
    /// Reads one workspace file. If a refactor moved the file, a unique
    /// filename match is accepted so path-only changes do not break a contract.
    /// </summary>
    public static string ReadFile(string relativePath)
    {
        string fullPath = ResolveFile(relativePath);
        return NormalizeLineEndings(File.ReadAllText(fullPath));
    }

    /// <summary>
    /// Reads every source file contributing to the same C# partial type.
    /// </summary>
    public static string ReadPartialType(string relativePath)
    {
        string fullPath = ResolveFile(relativePath);

        if (!string.Equals(Path.GetExtension(fullPath), ".cs", StringComparison.OrdinalIgnoreCase))
            return NormalizeLineEndings(File.ReadAllText(fullPath));

        string fileName = Path.GetFileNameWithoutExtension(fullPath);
        int separatorIndex = fileName.IndexOf('.');
        string typeStem = separatorIndex >= 0 ? fileName[..separatorIndex] : fileName;
        string projectRoot = ResolveProjectRoot(relativePath);

        string[] relatedPaths = Directory
            .EnumerateFiles(projectRoot, $"{typeStem}*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string candidateName = Path.GetFileNameWithoutExtension(path);
                return string.Equals(candidateName, typeStem, StringComparison.OrdinalIgnoreCase) ||
                    candidateName.StartsWith($"{typeStem}.", StringComparison.OrdinalIgnoreCase);
            })
            .Where(path => !IsGeneratedOrValidationPath(path))
            .OrderBy(path => string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (relatedPaths.Length <= 1)
            return NormalizeLineEndings(File.ReadAllText(fullPath));

        StringBuilder source = new();
        foreach (string path in relatedPaths)
        {
            source.AppendLine();
            source.AppendLine($"// Source contract file: {Path.GetRelativePath(RepositoryRoot, path)}");
            source.AppendLine(File.ReadAllText(path));
        }

        return NormalizeLineEndings(source.ToString());
    }

    private static string ResolveFile(string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(RepositoryRoot, relativePath));
        if (File.Exists(fullPath))
            return fullPath;

        string fileName = Path.GetFileName(relativePath);
        string[] matches = Directory
            .EnumerateFiles(RepositoryRoot, fileName, SearchOption.AllDirectories)
            .Where(path => !IsGeneratedOrValidationPath(path))
            .Take(2)
            .ToArray();
        if (matches.Length == 1)
            return matches[0];

        throw new FileNotFoundException(
            $"Could not uniquely resolve workspace path for '{relativePath}' from repository root '{RepositoryRoot}'.",
            fullPath);
    }

    private static string ResolveProjectRoot(string relativePath)
    {
        string normalizedPath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        int separatorIndex = normalizedPath.IndexOf(Path.DirectorySeparatorChar);
        string rootSegment = separatorIndex >= 0 ? normalizedPath[..separatorIndex] : normalizedPath;
        string projectRoot = Path.Combine(RepositoryRoot, rootSegment);
        return Directory.Exists(projectRoot) ? projectRoot : RepositoryRoot;
    }

    private static bool IsGeneratedOrValidationPath(string path)
    {
        string relativePath = Path.GetRelativePath(RepositoryRoot, path);
        string[] segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "_AgentValidation", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "Submodules", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeLineEndings(string source)
        => source.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the XRENGINE repository root from '{AppContext.BaseDirectory}'.");
    }
}
