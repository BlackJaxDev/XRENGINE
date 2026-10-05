using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, string> VulkanTypeFamilySources =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    internal readonly record struct SourceFile(string RelativePath, string Source);

    /// <summary>
    /// Reads a canonical workspace file. Legacy Vulkan contracts retain their
    /// separate partial-type lookup until that renderer's contract migration.
    /// </summary>
    public static string ReadFile(string relativePath)
    {
        string exactPath = Path.GetFullPath(Path.Combine(RepositoryRoot, relativePath));
        if (File.Exists(exactPath))
            return NormalizeLineEndings(File.ReadAllText(ResolveCanonicalFile(relativePath)));

        if (IsVulkanCSharpSourcePath(relativePath))
        {
            string requestedFileName = Path.GetFileName(relativePath);
            SourceFile[] movedFileMatches =
            [
                .. GetVulkanSourceFiles().Where(file =>
                {
                    string candidate = Path.GetFileName(file.RelativePath);
                    return string.Equals(
                            candidate,
                            requestedFileName,
                            StringComparison.OrdinalIgnoreCase) ||
                        candidate.EndsWith(
                            $".{requestedFileName}",
                            StringComparison.OrdinalIgnoreCase);
                }),
            ];
            if (movedFileMatches.Length == 1)
                return movedFileMatches[0].Source;

            if (movedFileMatches.Length == 0)
            {
                // Legacy VulkanRenderer partials were decomposed into focused files whose
                // names no longer retain the complete former filename. Resolve those requests
                // through the owning partial-type family instead of failing filename lookup.
                if (requestedFileName.StartsWith("VulkanRenderer.", StringComparison.OrdinalIgnoreCase))
                    return ReadPartialType(relativePath);

                string movedAcrossProject = ResolveFile(relativePath);
                return NormalizeLineEndings(File.ReadAllText(movedAcrossProject));
            }

            return ReadPartialType(relativePath);
        }

        string fullPath = ResolveCanonicalFile(relativePath);
        return NormalizeLineEndings(File.ReadAllText(fullPath));
    }

    /// <summary>
    /// Reads exactly one workspace file without expanding a partial-type
    /// family. Use this for negative or method-local assertions where unrelated
    /// partial files would create false positives.
    /// </summary>
    public static string ReadExactFile(string relativePath)
    {
        string fullPath = ResolveCanonicalFile(relativePath);
        return NormalizeLineEndings(File.ReadAllText(fullPath));
    }

    /// <summary>Resolves one required repository-relative file without relocation guesses.</summary>
    public static string ResolveCanonicalFile(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException("Source contracts require a repository-relative path.", nameof(relativePath));

        string fullPath = Path.GetFullPath(Path.Combine(RepositoryRoot, relativePath));
        if (!fullPath.StartsWith(RepositoryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source contract path must remain inside the repository.", nameof(relativePath));
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Required canonical repository file '{relativePath}' is missing.", relativePath);
        return fullPath;
    }

    /// <summary>
    /// Reads every source file contributing to the same C# partial type.
    /// </summary>
    public static string ReadPartialType(string relativePath)
    {
        if (IsVulkanCSharpSourcePath(relativePath))
        {
            string vulkanTypeStem = ResolveVulkanTypeStem(relativePath);
            return VulkanTypeFamilySources.GetOrAdd(vulkanTypeStem, static stem =>
            {
                SourceFile[] relatedFiles =
                [
                    .. GetVulkanSourceFiles()
                        .Where(file => IsTypeFamilyFile(file.RelativePath, stem))
                        .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase),
                ];

                if (relatedFiles.Length == 0)
                    throw new FileNotFoundException($"No Vulkan source files belong to the requested type family '{stem}'.");

                return CombineSources(relatedFiles);
            });
        }

        string fullPath = ResolveCanonicalFile(relativePath);
        if (!string.Equals(Path.GetExtension(fullPath), ".cs", StringComparison.OrdinalIgnoreCase))
            return NormalizeLineEndings(File.ReadAllText(fullPath));

        string typeStem = GetTypeStem(fullPath);
        string projectRoot = ResolveProjectRoot(relativePath);
        string[] relatedPaths = EnumerateEligibleFiles(projectRoot, $"{typeStem}*.cs")
            .Where(path => IsTypeFamilyFile(path, typeStem))
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
    /// Reads the files that contribute to the Vulkan command runtime.
    /// </summary>
    public static string ReadVulkanCommandRuntimeSource()
        => CombineSources(
            GetVulkanSourceFiles().Where(static file =>
                file.Source.Contains("partial class VulkanCommandRuntime", StringComparison.Ordinal)));

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

    private static bool IsVulkanCSharpSourcePath(string relativePath)
    {
        string normalized = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        return normalized.StartsWith($"{VulkanProjectDirectory}/", StringComparison.OrdinalIgnoreCase) &&
            normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveVulkanTypeStem(string relativePath)
    {
        string requestedFileName = Path.GetFileName(relativePath);
        string exactPath = Path.GetFullPath(Path.Combine(RepositoryRoot, relativePath));
        if (File.Exists(exactPath))
            return GetTypeStem(requestedFileName);

        const string rendererPrefix = "VulkanRenderer.";
        if (requestedFileName.StartsWith(rendererPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string deNestedFileName = requestedFileName[rendererPrefix.Length..];
            SourceFile[] matches =
            [
                .. GetVulkanSourceFiles().Where(file =>
                    string.Equals(Path.GetFileName(file.RelativePath), deNestedFileName, StringComparison.OrdinalIgnoreCase)),
            ];
            if (matches.Length == 1)
                return GetTypeStem(deNestedFileName);
        }

        return GetTypeStem(requestedFileName);
    }

    private static string GetTypeStem(string path)
    {
        string fileName = Path.GetFileNameWithoutExtension(path);
        int separatorIndex = fileName.IndexOf('.');
        return separatorIndex >= 0 ? fileName[..separatorIndex] : fileName;
    }

    private static bool IsTypeFamilyFile(string path, string typeStem)
    {
        string candidateName = Path.GetFileNameWithoutExtension(path);
        return string.Equals(candidateName, typeStem, StringComparison.OrdinalIgnoreCase) ||
            candidateName.StartsWith($"{typeStem}.", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveFile(string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(RepositoryRoot, relativePath));
        if (File.Exists(fullPath))
            return fullPath;

        string fileName = Path.GetFileName(relativePath);
        string[] matches = Directory.EnumerateDirectories(RepositoryRoot, "XREngine.*", SearchOption.TopDirectoryOnly)
            .Where(static path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .SelectMany(path => EnumerateEligibleFiles(path, fileName))
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
            .. EnumerateEligibleFiles(projectRoot, "*.cs")
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

    /// <summary>
    /// Finds source files without entering generated or external directories.
    /// </summary>
    private static IEnumerable<string> EnumerateEligibleFiles(string root, string searchPattern)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string file in Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
                yield return file;

            foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(child);
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("_AgentValidation", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Dependencies", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Submodules", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".codex", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    continue;

                pending.Push(child);
            }
        }
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
