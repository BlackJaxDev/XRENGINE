using System.Text;

namespace XREngine.UnitTests;

/// <summary>
/// Reads repository source for contract tests without coupling those tests to
/// the physical file layout of partial types.
/// </summary>
internal static class SourceContractWorkspace
{
    private const string VulkanProjectDirectory = "XREngine.Runtime.Rendering.Vulkan";
    private static readonly Lazy<IReadOnlyList<SourceFile>> VulkanSourceFiles =
        new(DiscoverVulkanSourceFiles);
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    internal readonly record struct SourceFile(string RelativePath, string Source);

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

    /// <summary>
    /// Returns every hand-authored C# source file in the Vulkan rendering
    /// project, recursively and in stable repository-relative path order.
    /// </summary>
    public static IReadOnlyList<SourceFile> GetVulkanSourceFiles()
        => VulkanSourceFiles.Value;

    /// <summary>
    /// Reads all files that currently contribute to <c>VulkanRenderer</c>.
    /// Contract tests should prefer this over naming one physical partial file
    /// when the member's owner is what matters.
    /// </summary>
    public static string ReadVulkanRendererSource()
        => CombineSources(
            GetVulkanSourceFiles().Where(static file =>
                file.Source.Contains("partial class VulkanRenderer", StringComparison.Ordinal)));

    /// <summary>
    /// Reads the Vulkan source files containing any supplied contract marker.
    /// This keeps source-text assertions independent of file moves and splits.
    /// </summary>
    public static string ReadVulkanSourcesContaining(params string[] markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        if (markers.Length == 0)
            throw new ArgumentException("At least one source marker is required.", nameof(markers));

        SourceFile[] matches =
        [
            .. GetVulkanSourceFiles().Where(file =>
                markers.Any(marker =>
                    file.Source.Contains(marker, StringComparison.Ordinal))),
        ];

        if (matches.Length == 0)
            throw new InvalidOperationException(
                $"No Vulkan source file contains any requested marker: {string.Join(", ", markers)}.");

        return CombineSources(matches);
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

    private static IReadOnlyList<SourceFile> DiscoverVulkanSourceFiles()
    {
        string projectRoot = Path.Combine(RepositoryRoot, VulkanProjectDirectory);
        if (!Directory.Exists(projectRoot))
            throw new DirectoryNotFoundException(
                $"Could not locate the Vulkan rendering project at '{projectRoot}'.");

        return
        [
            .. Directory
                .EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsGeneratedOrValidationPath(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new SourceFile(
                    Path.GetRelativePath(RepositoryRoot, path)
                        .Replace(Path.DirectorySeparatorChar, '/'),
                    NormalizeLineEndings(File.ReadAllText(path)))),
        ];
    }

    private static string CombineSources(IEnumerable<SourceFile> files)
    {
        StringBuilder source = new();
        int fileCount = 0;
        foreach (SourceFile file in files)
        {
            fileCount++;
            source.AppendLine();
            source.AppendLine($"// Source contract file: {file.RelativePath}");
            source.AppendLine(file.Source);
        }

        if (fileCount == 0)
            throw new InvalidOperationException("No source files matched the requested Vulkan contract.");

        return NormalizeLineEndings(source.ToString());
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
