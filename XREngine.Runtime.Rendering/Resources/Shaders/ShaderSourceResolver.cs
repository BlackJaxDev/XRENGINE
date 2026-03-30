using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Rendering;

internal sealed class ShaderSourceResolverOptions
{
    public IReadOnlyList<string>? AdditionalShaderRoots { get; init; }
    public Action<string>? WarningLogger { get; init; }
}

internal readonly record struct ShaderSourceFileDependency(string Path, long LastWriteTimeUtcTicks, long Length);

internal sealed class ShaderSourceResolutionResult
{
    public ShaderSourceResolutionResult(string source, string[] resolvedPaths, ShaderSourceFileDependency[] fileDependencies)
    {
        Source = source;
        ResolvedPaths = resolvedPaths;
        FileDependencies = fileDependencies;
    }

    public string Source { get; }
    public string[] ResolvedPaths { get; }
    public ShaderSourceFileDependency[] FileDependencies { get; }
}

internal static partial class ShaderSourceResolver
{
    private static readonly string[] SupportedSnippetExtensions = [".glsl", ".snip", ".frag", ".vert", ".fs", ".vs"];
    private static readonly ConcurrentDictionary<string, CachedTextFile> TextFileCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<IncludeExpansionCacheKey, IncludeExpansionCacheEntry> IncludeExpansionCache = new();
    private static readonly ConcurrentDictionary<string, FileIndexCacheEntry> ShaderRootFileIndexCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, FileIndexCacheEntry> SnippetFileIndexCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<SnippetResolutionCacheKey, SnippetResolutionCacheEntry> SnippetResolutionCache = new();
    private static readonly ConcurrentDictionary<string, string> RegisteredSnippets = new(StringComparer.OrdinalIgnoreCase);

    private static long _registeredSnippetVersion;

    [GeneratedRegex(@"^\s*#\s*include\s+[""<](?<path>[^"">]+)["">]\s*$", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex IncludeRegex();

    [GeneratedRegex(@"#pragma\s+snippet\s+[""<](?<name>[^"">]+)["">]", RegexOptions.Compiled)]
    private static partial Regex SnippetDirectiveRegex();

    private readonly record struct IncludeExpansionCacheKey(string Path, bool AnnotateIncludes, string SearchRootsKey);
    private readonly record struct SnippetResolutionCacheKey(string ExpandedSource, string SearchRootsKey, long RegisteredSnippetVersion);
    private readonly record struct DirectoryDependency(string Path, long LastWriteTimeUtcTicks);

    private sealed class SearchContext
    {
        public SearchContext(string? sourceDirectory, string[] shaderRoots, string searchRootsKey, Action<string>? warningLogger)
        {
            SourceDirectory = sourceDirectory;
            ShaderRoots = shaderRoots;
            SearchRootsKey = searchRootsKey;
            WarningLogger = warningLogger;
        }

        public string? SourceDirectory { get; }
        public string[] ShaderRoots { get; }
        public string SearchRootsKey { get; }
        public Action<string>? WarningLogger { get; }
    }

    private sealed class CachedTextFile
    {
        public CachedTextFile(string text, ShaderSourceFileDependency dependency)
        {
            Text = text;
            Dependency = dependency;
        }

        public string Text { get; }
        public ShaderSourceFileDependency Dependency { get; }
    }

    private sealed class IncludeExpansionCacheEntry
    {
        public IncludeExpansionCacheEntry(
            string expandedSource,
            string[] resolvedPaths,
            ShaderSourceFileDependency[] fileDependencies,
            DirectoryDependency[] searchRootDependencies)
        {
            ExpandedSource = expandedSource;
            ResolvedPaths = resolvedPaths;
            FileDependencies = fileDependencies;
            SearchRootDependencies = searchRootDependencies;
        }

        public string ExpandedSource { get; }
        public string[] ResolvedPaths { get; }
        public ShaderSourceFileDependency[] FileDependencies { get; }
        public DirectoryDependency[] SearchRootDependencies { get; }
    }

    private sealed class SnippetResolutionCacheEntry
    {
        public SnippetResolutionCacheEntry(
            string resolvedSource,
            ShaderSourceFileDependency[] fileDependencies,
            DirectoryDependency[] searchRootDependencies)
        {
            ResolvedSource = resolvedSource;
            FileDependencies = fileDependencies;
            SearchRootDependencies = searchRootDependencies;
        }

        public string ResolvedSource { get; }
        public ShaderSourceFileDependency[] FileDependencies { get; }
        public DirectoryDependency[] SearchRootDependencies { get; }
    }

    private sealed class FileIndexCacheEntry
    {
        public FileIndexCacheEntry(Dictionary<string, string> pathsByName, DirectoryDependency[] directoryDependencies)
        {
            PathsByName = pathsByName;
            DirectoryDependencies = directoryDependencies;
        }

        public Dictionary<string, string> PathsByName { get; }
        public DirectoryDependency[] DirectoryDependencies { get; }
    }

    public static string ResolveSource(string source, string? sourcePath, bool annotateIncludes = false)
        => ResolveSource(source, sourcePath, options: null, annotateIncludes);

    public static string ResolveSource(string source, string? sourcePath, out List<string> resolvedPaths, bool annotateIncludes = false)
        => ResolveSource(source, sourcePath, options: null, out resolvedPaths, annotateIncludes);

    internal static string ResolveSource(string source, string? sourcePath, ShaderSourceResolverOptions? options, bool annotateIncludes = false)
        => ResolveSource(source, sourcePath, options, out _, annotateIncludes);

    internal static string ResolveSource(string source, string? sourcePath, ShaderSourceResolverOptions? options, out List<string> resolvedPaths, bool annotateIncludes = false)
    {
        ShaderSourceResolutionResult result = ResolveSourceDetailed(source, sourcePath, options, annotateIncludes);
        resolvedPaths = [.. result.ResolvedPaths];
        return result.Source;
    }

    internal static ShaderSourceResolutionResult ResolveSourceDetailed(string source, string? sourcePath, ShaderSourceResolverOptions? options = null, bool annotateIncludes = false)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new(source, [], []);

        SearchContext context = CreateSearchContext(sourcePath, options);
        List<string> resolvedPaths = [];
        Dictionary<string, ShaderSourceFileDependency> fileDependencies = new(StringComparer.OrdinalIgnoreCase);

        string resolvedIncludes = ExpandIncludesRecursive(
            source,
            context.SourceDirectory,
            context,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            resolvedPaths,
            fileDependencies,
            annotateIncludes);

        SnippetResolutionCacheEntry resolvedSnippets = ResolveSnippetsCached(resolvedIncludes, context);
        MergeDependencies(fileDependencies, resolvedSnippets.FileDependencies);

        return new(resolvedSnippets.ResolvedSource, [.. resolvedPaths], [.. fileDependencies.Values]);
    }

    internal static bool AreDependenciesCurrent(IReadOnlyList<ShaderSourceFileDependency>? dependencies)
    {
        if (dependencies is null)
            return true;

        for (int i = 0; i < dependencies.Count; i++)
        {
            ShaderSourceFileDependency dependency = dependencies[i];
            if (!TryGetCurrentFileDependency(dependency.Path, out ShaderSourceFileDependency currentDependency))
                return false;

            if (currentDependency.LastWriteTimeUtcTicks != dependency.LastWriteTimeUtcTicks ||
                currentDependency.Length != dependency.Length)
            {
                return false;
            }
        }

        return true;
    }

    internal static void RegisterSnippet(string snippetName, string snippetSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snippetName);
        ArgumentNullException.ThrowIfNull(snippetSource);

        RegisteredSnippets[snippetName] = snippetSource;
        Interlocked.Increment(ref _registeredSnippetVersion);
    }

    internal static bool UnregisterSnippet(string snippetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snippetName);

        bool removed = RegisteredSnippets.TryRemove(snippetName, out _);
        if (removed)
            Interlocked.Increment(ref _registeredSnippetVersion);

        return removed;
    }

    internal static bool TryGetSnippetSource(string snippetName, ShaderSourceResolverOptions? options, out string? snippetSource)
    {
        SearchContext context = CreateSearchContext(sourcePath: null, options);
        return TryLoadSnippet(context, snippetName, out snippetSource, out _);
    }

    internal static IEnumerable<string> GetAvailableSnippetNames(ShaderSourceResolverOptions? options)
    {
        SearchContext context = CreateSearchContext(sourcePath: null, options);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (string registeredSnippet in RegisteredSnippets.Keys)
            names.Add(registeredSnippet);

        foreach (string shaderRoot in context.ShaderRoots)
        {
            if (!TryGetSnippetFileIndex(shaderRoot, out FileIndexCacheEntry? snippetIndex) || snippetIndex is null)
                continue;

            foreach (string snippetName in snippetIndex.PathsByName.Keys)
                names.Add(snippetName);
        }

        return [.. names];
    }

    internal static string ResolveSnippetDirectives(string source, ShaderSourceResolverOptions? options = null)
    {
        if (string.IsNullOrEmpty(source))
            return source;

        SearchContext context = CreateSearchContext(sourcePath: null, options);
        return ResolveSnippetsCached(source, context).ResolvedSource;
    }

    internal static void ClearCaches(bool clearRegisteredSnippets = false)
    {
        TextFileCache.Clear();
        IncludeExpansionCache.Clear();
        ShaderRootFileIndexCache.Clear();
        SnippetFileIndexCache.Clear();
        SnippetResolutionCache.Clear();

        if (clearRegisteredSnippets)
        {
            RegisteredSnippets.Clear();
            Interlocked.Increment(ref _registeredSnippetVersion);
        }
    }

    private static SearchContext CreateSearchContext(string? sourcePath, ShaderSourceResolverOptions? options)
    {
        string? sourceDirectory = string.IsNullOrWhiteSpace(sourcePath)
            ? null
            : Path.GetDirectoryName(sourcePath);

        List<string> shaderRoots = [];

        AddShaderRoot(shaderRoots, FindShaderRoot(sourcePath, sourceDirectory));
        if (options?.AdditionalShaderRoots is not null)
        {
            foreach (string shaderRoot in options.AdditionalShaderRoots)
                AddShaderRoot(shaderRoots, shaderRoot);
        }

        string searchRootsKey = shaderRoots.Count == 0
            ? string.Empty
            : string.Join("|", shaderRoots);

        Action<string>? warningLogger = options?.WarningLogger;
        if (warningLogger is null && RuntimeShaderServices.Current is IRuntimeShaderServices runtimeShaderServices)
            warningLogger = runtimeShaderServices.LogWarning;

        return new(
            sourceDirectory,
            [.. shaderRoots],
            searchRootsKey,
            warningLogger);
    }

    private static void AddShaderRoot(List<string> shaderRoots, string? shaderRoot)
    {
        if (string.IsNullOrWhiteSpace(shaderRoot) || !Directory.Exists(shaderRoot))
            return;

        string normalizedRoot = Path.GetFullPath(shaderRoot);
        foreach (string existingRoot in shaderRoots)
        {
            if (string.Equals(existingRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return;
        }

        shaderRoots.Add(normalizedRoot);
    }

    private static string ExpandIncludesRecursive(
        string source,
        string? currentDirectory,
        SearchContext context,
        HashSet<string> includeStack,
        List<string> resolvedPaths,
        Dictionary<string, ShaderSourceFileDependency> fileDependencies,
        bool annotateIncludes)
    {
        StringBuilder output = new(source.Length + 128);
        using StringReader reader = new(source);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            Match includeMatch = IncludeRegex().Match(line);
            if (!includeMatch.Success)
            {
                output.AppendLine(line);
                continue;
            }

            string includePath = includeMatch.Groups["path"].Value.Trim();
            string resolvedPath = ResolveIncludePath(currentDirectory, context, includePath)
                ?? throw new InvalidOperationException($"Failed to resolve shader include '{includePath}'.");

            IncludeExpansionCacheEntry expandedInclude = ExpandIncludeFile(resolvedPath, context, includeStack, annotateIncludes);
            resolvedPaths.AddRange(expandedInclude.ResolvedPaths);
            MergeDependencies(fileDependencies, expandedInclude.FileDependencies);

            if (annotateIncludes)
            {
                output.AppendLine($"// begin include {includePath}");
                output.AppendLine(expandedInclude.ExpandedSource);
                output.AppendLine($"// end include {includePath}");
            }
            else
            {
                output.AppendLine(expandedInclude.ExpandedSource);
            }
        }

        return output.ToString();
    }

    private static IncludeExpansionCacheEntry ExpandIncludeFile(string includePath, SearchContext context, HashSet<string> includeStack, bool annotateIncludes)
    {
        string normalizedPath = Path.GetFullPath(includePath);
        if (!includeStack.Add(normalizedPath))
            throw new InvalidOperationException($"Recursive shader include detected for '{normalizedPath}'.");

        try
        {
            IncludeExpansionCacheKey cacheKey = new(normalizedPath, annotateIncludes, context.SearchRootsKey);
            if (IncludeExpansionCache.TryGetValue(cacheKey, out IncludeExpansionCacheEntry? cachedEntry) &&
                cachedEntry is not null &&
                AreDependenciesCurrent(cachedEntry.FileDependencies) &&
                AreDirectoriesCurrent(cachedEntry.SearchRootDependencies))
            {
                EnsureNoRecursiveDependency(normalizedPath, cachedEntry.FileDependencies, includeStack);
                return cachedEntry;
            }

            string includedSource = ReadTextFile(normalizedPath, out ShaderSourceFileDependency sourceDependency);
            Dictionary<string, ShaderSourceFileDependency> fileDependencies = new(StringComparer.OrdinalIgnoreCase)
            {
                [sourceDependency.Path] = sourceDependency,
            };
            List<string> resolvedPaths = [normalizedPath];

            string expandedSource = ExpandIncludesRecursive(
                includedSource,
                Path.GetDirectoryName(normalizedPath),
                context,
                includeStack,
                resolvedPaths,
                fileDependencies,
                annotateIncludes);

            IncludeExpansionCacheEntry includeEntry = new(
                expandedSource,
                [.. resolvedPaths],
                [.. fileDependencies.Values],
                CaptureSearchRootDependencies(context.ShaderRoots));
            IncludeExpansionCache[cacheKey] = includeEntry;
            return includeEntry;
        }
        finally
        {
            includeStack.Remove(normalizedPath);
        }
    }

    private static void EnsureNoRecursiveDependency(string currentPath, IReadOnlyList<ShaderSourceFileDependency> fileDependencies, HashSet<string> includeStack)
    {
        for (int i = 0; i < fileDependencies.Count; i++)
        {
            string dependencyPath = fileDependencies[i].Path;
            if (string.Equals(dependencyPath, currentPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (includeStack.Contains(dependencyPath))
                throw new InvalidOperationException($"Recursive shader include detected for '{dependencyPath}'.");
        }
    }

    private static SnippetResolutionCacheEntry ResolveSnippetsCached(string source, SearchContext context)
    {
        if (string.IsNullOrEmpty(source))
            return new(source, [], CaptureSearchRootDependencies(context.ShaderRoots));

        MatchCollection matches = SnippetDirectiveRegex().Matches(source);
        if (matches.Count == 0)
            return new(source, [], CaptureSearchRootDependencies(context.ShaderRoots));

        long registeredSnippetVersion = Volatile.Read(ref _registeredSnippetVersion);
        SnippetResolutionCacheKey cacheKey = new(source, context.SearchRootsKey, registeredSnippetVersion);
        if (SnippetResolutionCache.TryGetValue(cacheKey, out SnippetResolutionCacheEntry? cachedEntry) &&
            cachedEntry is not null &&
            AreDependenciesCurrent(cachedEntry.FileDependencies) &&
            AreDirectoriesCurrent(cachedEntry.SearchRootDependencies))
        {
            return cachedEntry;
        }

        Dictionary<string, ShaderSourceFileDependency> fileDependencies = new(StringComparer.OrdinalIgnoreCase);
        string resolvedSource = ResolveSnippetsRecursive(source, context, new HashSet<string>(StringComparer.OrdinalIgnoreCase), fileDependencies);
        SnippetResolutionCacheEntry resolvedEntry = new(
            resolvedSource,
            [.. fileDependencies.Values],
            CaptureSearchRootDependencies(context.ShaderRoots));
        SnippetResolutionCache[cacheKey] = resolvedEntry;
        return resolvedEntry;
    }

    private static string ResolveSnippetsRecursive(
        string source,
        SearchContext context,
        HashSet<string> resolvedSnippets,
        Dictionary<string, ShaderSourceFileDependency> fileDependencies)
    {
        MatchCollection matches = SnippetDirectiveRegex().Matches(source);
        if (matches.Count == 0)
            return source;

        StringBuilder result = new(source.Length * 2);
        int lastIndex = 0;

        foreach (Match match in matches)
        {
            result.Append(source, lastIndex, match.Index - lastIndex);

            string snippetName = match.Groups["name"].Value;
            if (resolvedSnippets.Contains(snippetName))
            {
                result.AppendLine($"// [Snippet '{snippetName}' already included]");
            }
            else if (TryLoadSnippet(context, snippetName, out string? snippetSource, out ShaderSourceFileDependency fileDependency) && snippetSource is not null)
            {
                resolvedSnippets.Add(snippetName);
                if (!string.IsNullOrWhiteSpace(fileDependency.Path))
                    fileDependencies[fileDependency.Path] = fileDependency;

                result.AppendLine($"// ===== BEGIN SNIPPET: {snippetName} =====");
                result.Append(ResolveSnippetsRecursive(snippetSource, context, resolvedSnippets, fileDependencies));
                result.AppendLine();
                result.AppendLine($"// ===== END SNIPPET: {snippetName} =====");
            }
            else
            {
                context.WarningLogger?.Invoke($"Shader snippet '{snippetName}' not found.");
                result.AppendLine($"// [WARNING: Snippet '{snippetName}' not found]");
            }

            lastIndex = match.Index + match.Length;
        }

        result.Append(source, lastIndex, source.Length - lastIndex);
        return result.ToString();
    }

    private static bool TryLoadSnippet(SearchContext context, string snippetName, out string? snippetSource, out ShaderSourceFileDependency fileDependency)
    {
        if (RegisteredSnippets.TryGetValue(snippetName, out string? registeredSnippetSource))
        {
            snippetSource = registeredSnippetSource;
            fileDependency = default;
            return true;
        }

        foreach (string shaderRoot in context.ShaderRoots)
        {
            if (!TryResolveSnippetPath(shaderRoot, snippetName, out string? snippetPath) || snippetPath is null)
                continue;

            snippetSource = ReadTextFile(snippetPath, out fileDependency);
            return true;
        }

        snippetSource = null;
        fileDependency = default;
        return false;
    }

    private static bool TryResolveSnippetPath(string shaderRoot, string snippetName, out string? snippetPath)
    {
        snippetPath = null;
        if (!TryGetSnippetFileIndex(shaderRoot, out FileIndexCacheEntry? snippetIndex) || snippetIndex is null)
            return false;

        return snippetIndex.PathsByName.TryGetValue(snippetName, out snippetPath);
    }

    private static bool TryGetSnippetFileIndex(string shaderRoot, out FileIndexCacheEntry? snippetIndex)
    {
        snippetIndex = null;
        if (string.IsNullOrWhiteSpace(shaderRoot))
            return false;

        string snippetsDirectory = Path.Combine(shaderRoot, "Snippets");
        if (!Directory.Exists(snippetsDirectory))
            return false;

        string normalizedDirectory = Path.GetFullPath(snippetsDirectory);
        if (SnippetFileIndexCache.TryGetValue(normalizedDirectory, out FileIndexCacheEntry? cachedIndex) &&
            cachedIndex is not null &&
            AreDirectoriesCurrent(cachedIndex.DirectoryDependencies))
        {
            snippetIndex = cachedIndex;
            return true;
        }

        Dictionary<string, string> pathsByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (string extension in SupportedSnippetExtensions)
        {
            foreach (string filePath in Directory.EnumerateFiles(normalizedDirectory, "*" + extension, SearchOption.TopDirectoryOnly))
                pathsByName.TryAdd(Path.GetFileNameWithoutExtension(filePath), Path.GetFullPath(filePath));

            foreach (string filePath in Directory.EnumerateFiles(normalizedDirectory, "*" + extension, SearchOption.AllDirectories))
                pathsByName.TryAdd(Path.GetFileNameWithoutExtension(filePath), Path.GetFullPath(filePath));
        }

        FileIndexCacheEntry rebuiltIndex = new(pathsByName, CaptureDirectoryDependencies(normalizedDirectory));
        SnippetFileIndexCache[normalizedDirectory] = rebuiltIndex;
        snippetIndex = rebuiltIndex;
        return true;
    }

    private static string ReadTextFile(string normalizedPath, out ShaderSourceFileDependency dependency)
    {
        if (!TryGetCurrentFileDependency(normalizedPath, out dependency))
            throw new FileNotFoundException($"Shader source file '{normalizedPath}' does not exist.", normalizedPath);

        if (TextFileCache.TryGetValue(normalizedPath, out CachedTextFile? cachedFile) &&
            cachedFile is not null &&
            cachedFile.Dependency.LastWriteTimeUtcTicks == dependency.LastWriteTimeUtcTicks &&
            cachedFile.Dependency.Length == dependency.Length)
        {
            return cachedFile.Text;
        }

        string text = File.ReadAllText(normalizedPath);
        TextFileCache[normalizedPath] = new(text, dependency);
        return text;
    }

    private static bool TryGetCurrentFileDependency(string path, out ShaderSourceFileDependency dependency)
    {
        dependency = default;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        string normalizedPath = Path.GetFullPath(path);
        FileInfo info = new(normalizedPath);
        if (!info.Exists)
            return false;

        dependency = new(normalizedPath, info.LastWriteTimeUtc.Ticks, info.Length);
        return true;
    }

    private static bool AreDirectoriesCurrent(IReadOnlyList<DirectoryDependency> directoryDependencies)
    {
        for (int i = 0; i < directoryDependencies.Count; i++)
        {
            DirectoryDependency dependency = directoryDependencies[i];
            if (!Directory.Exists(dependency.Path))
                return false;

            long currentTicks = new DirectoryInfo(dependency.Path).LastWriteTimeUtc.Ticks;
            if (currentTicks != dependency.LastWriteTimeUtcTicks)
                return false;
        }

        return true;
    }

    private static DirectoryDependency[] CaptureDirectoryDependencies(string rootDirectory)
    {
        List<DirectoryDependency> dependencies = [];
        foreach (string directory in Directory.EnumerateDirectories(rootDirectory, "*", SearchOption.AllDirectories))
            dependencies.Add(new(Path.GetFullPath(directory), new DirectoryInfo(directory).LastWriteTimeUtc.Ticks));

        dependencies.Add(new(Path.GetFullPath(rootDirectory), new DirectoryInfo(rootDirectory).LastWriteTimeUtc.Ticks));
        return [.. dependencies];
    }

    private static DirectoryDependency[] CaptureSearchRootDependencies(IReadOnlyList<string> shaderRoots)
    {
        Dictionary<string, DirectoryDependency> dependencies = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < shaderRoots.Count; i++)
        {
            foreach (DirectoryDependency dependency in CaptureDirectoryDependencies(shaderRoots[i]))
                dependencies[dependency.Path] = dependency;
        }

        return [.. dependencies.Values];
    }

    private static void MergeDependencies(Dictionary<string, ShaderSourceFileDependency> target, IReadOnlyList<ShaderSourceFileDependency> dependencies)
    {
        for (int i = 0; i < dependencies.Count; i++)
            target[dependencies[i].Path] = dependencies[i];
    }

    private static string? ResolveIncludePath(string? currentDirectory, SearchContext context, string includePath)
    {
        if (string.IsNullOrWhiteSpace(includePath))
            return null;

        if (Path.IsPathRooted(includePath))
        {
            string absolutePath = Path.GetFullPath(includePath);
            if (File.Exists(absolutePath))
                return absolutePath;
        }

        if (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            string fromCurrentDirectory = Path.GetFullPath(Path.Combine(currentDirectory, includePath));
            if (File.Exists(fromCurrentDirectory))
                return fromCurrentDirectory;
        }

        foreach (string shaderRoot in context.ShaderRoots)
        {
            string fromRoot = Path.GetFullPath(Path.Combine(shaderRoot, includePath));
            if (File.Exists(fromRoot))
                return fromRoot;
        }

        if (includePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return null;

        foreach (string shaderRoot in context.ShaderRoots)
        {
            if (TryResolveIndexedShaderFile(shaderRoot, includePath, out string? indexedPath))
                return indexedPath;
        }

        return null;
    }

    private static bool TryResolveIndexedShaderFile(string shaderRoot, string fileName, out string? resolvedPath)
    {
        resolvedPath = null;
        if (!TryGetShaderRootFileIndex(shaderRoot, out FileIndexCacheEntry? fileIndex) || fileIndex is null)
            return false;

        return fileIndex.PathsByName.TryGetValue(fileName, out resolvedPath);
    }

    private static bool TryGetShaderRootFileIndex(string shaderRoot, out FileIndexCacheEntry? fileIndex)
    {
        fileIndex = null;
        if (string.IsNullOrWhiteSpace(shaderRoot) || !Directory.Exists(shaderRoot))
            return false;

        string normalizedRoot = Path.GetFullPath(shaderRoot);
        if (ShaderRootFileIndexCache.TryGetValue(normalizedRoot, out FileIndexCacheEntry? cachedIndex) &&
            cachedIndex is not null &&
            AreDirectoriesCurrent(cachedIndex.DirectoryDependencies))
        {
            fileIndex = cachedIndex;
            return true;
        }

        Dictionary<string, string> pathsByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (string filePath in Directory.EnumerateFiles(normalizedRoot, "*", SearchOption.AllDirectories))
            pathsByName.TryAdd(Path.GetFileName(filePath), Path.GetFullPath(filePath));

        FileIndexCacheEntry rebuiltIndex = new(pathsByName, CaptureDirectoryDependencies(normalizedRoot));
        ShaderRootFileIndexCache[normalizedRoot] = rebuiltIndex;
        fileIndex = rebuiltIndex;
        return true;
    }

    private static string? FindShaderRoot(string? sourcePath, string? sourceDirectory)
    {
        IEnumerable<string?> candidates = [sourcePath, sourceDirectory, AppContext.BaseDirectory];
        foreach (string? candidate in candidates)
        {
            string? root = WalkForShaderRoot(candidate);
            if (!string.IsNullOrWhiteSpace(root))
                return root;
        }

        return null;
    }

    private static string? WalkForShaderRoot(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
            return null;

        DirectoryInfo? directory = Directory.Exists(startPath)
            ? new DirectoryInfo(startPath)
            : new FileInfo(startPath).Directory;

        while (directory is not null)
        {
            if (string.Equals(directory.Name, "Shaders", StringComparison.OrdinalIgnoreCase))
                return directory.FullName;

            string buildCommonAssetsShaders = Path.Combine(directory.FullName, "Build", "CommonAssets", "Shaders");
            if (Directory.Exists(buildCommonAssetsShaders))
                return buildCommonAssetsShaders;

            directory = directory.Parent;
        }

        return null;
    }
}