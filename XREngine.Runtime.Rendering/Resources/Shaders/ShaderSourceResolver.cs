using XREngine.Core.Files;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using XREngine.Execution;

namespace XREngine.Rendering;

internal sealed class ShaderSourceResolverOptions
{
    public IReadOnlyList<string>? AdditionalShaderRoots { get; init; }
    public Action<string>? WarningLogger { get; init; }
    public bool EmitIncludeDeadCodeMarkers { get; init; }
    public bool EnableSnippetDeadCodeElimination { get; init; }
}

public readonly record struct ShaderSourceFileDependency(string Path, long LastWriteTimeUtcTicks, long Length);

internal sealed class ShaderSourceResolutionResult
{
    public ShaderSourceResolutionResult(string source, string[] resolvedPaths, ShaderSourceFileDependency[] fileDependencies,
        ShaderSourceDirectoryDependency[] searchRootDependencies, ShaderSourceProviderOwner owner)
    {
        Source = source;
        ResolvedPaths = resolvedPaths;
        FileDependencies = fileDependencies;
        SearchRootDependencies = searchRootDependencies;
        Owner = owner;
    }

    public string Source { get; }
    public string[] ResolvedPaths { get; }
    public ShaderSourceFileDependency[] FileDependencies { get; }
    public ShaderSourceDirectoryDependency[] SearchRootDependencies { get; }
    public ShaderSourceProviderOwner Owner { get; }
}

internal static partial class ShaderSourceResolver
{
    private static readonly string[] SupportedSnippetExtensions = [".glsl", ".snip", ".frag", ".vert", ".fs", ".vs"];
    private static readonly ConcurrentDictionary<(string Path, ShaderSourceProviderOwner Owner), CachedTextFile> TextFileCache = new();
    private static readonly ConcurrentDictionary<IncludeExpansionCacheKey, IncludeExpansionCacheEntry> IncludeExpansionCache = new();
    private static readonly ConcurrentDictionary<(string Path, ShaderSourceProviderOwner Owner), FileIndexCacheEntry> ShaderRootFileIndexCache = new();
    private static readonly ConcurrentDictionary<(string Path, ShaderSourceProviderOwner Owner), FileIndexCacheEntry> SnippetFileIndexCache = new();
    private static readonly ConcurrentDictionary<SnippetResolutionCacheKey, SnippetResolutionCacheEntry> SnippetResolutionCache = new();
    private static readonly ConcurrentDictionary<string, string> RegisteredSnippets = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheOwnerGate = new();
    private static ShaderSourceProviderOwner _cacheOwner;
    private static bool _hasCacheOwner;

    private static long _registeredSnippetVersion;

    internal static long RegisteredSnippetVersion => Volatile.Read(ref _registeredSnippetVersion);

    // The shader service reflects the bound asset owner; the execution checks also
    // cover direct resolver calls made before that service has been installed.
    private static bool HostShaderFilesAdmitted
        => !OperatingSystem.IsBrowser()
        && !RuntimeWorkScheduler.IsCallerThread
        && DirectStorageIO.Source is not IRuntimeAssetCatalog
        && DirectStorageIO.Source is not { SupportsSynchronousReads: false }
        && RuntimeShaderServices.Current?.SupportsSynchronousShaderWork != false;

    internal static bool CanAccessHostShaderFiles => CaptureOwner().HostFileAccess;

    internal static ShaderSourceProviderOwner CaptureOwner()
    {
        bool admitted = HostShaderFilesAdmitted;
        ShaderSourceFileBackendServices.TryCapture(out IShaderSourceFileBackend? backend, out int backendGeneration);
        AssetFileSystemServices.TryCapture(out IAssetFileSystem? fileSystem, out long fileSystemGeneration);
        ShaderSourceProviderOwner owner = new(backend, backendGeneration, fileSystem, fileSystemGeneration,
            admitted && backend is not null && fileSystem is not null);
        lock (CacheOwnerGate)
        {
            if ((!_hasCacheOwner || !_cacheOwner.Equals(owner)) && IsOwnerInstallationCurrent(owner))
            {
                ClearCaches();
                _cacheOwner = owner;
                _hasCacheOwner = true;
            }
        }
        return owner;
    }

    internal static bool IsOwnerCurrent(ShaderSourceProviderOwner owner)
        => owner.HostFileAccess == (HostShaderFilesAdmitted && owner.FileBackend is not null && owner.FileSystem is not null)
        && IsOwnerInstallationCurrent(owner);

    internal static bool IsOwnerInstallationCurrent(ShaderSourceProviderOwner owner)
        => ShaderSourceFileBackendServices.IsCurrent(owner.FileBackend, owner.FileBackendGeneration)
        && AssetFileSystemServices.IsCurrent(owner.FileSystem, owner.FileSystemGeneration);

    // Default-disabled. See ExpandIncludesRecursive for rationale.
    private static bool EmitIncludeDceMarkers
        => XREnvironment.IsEnabled(XREngineEnvironmentVariables.GlslDceIncludes);

    // Matches `#include "path"` or `#include <path>` optionally followed by whitespace and/or a `//` line comment.
    [GeneratedRegex(@"^\s*#\s*include\s+[""<](?<path>[^"">]+)["">]\s*(?://.*)?$", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex IncludeRegex();

    [GeneratedRegex(@"#pragma\s+snippet\s+[""<](?<name>[^"">]+)["">]", RegexOptions.Compiled)]
    private static partial Regex SnippetDirectiveRegex();

    private readonly record struct IncludeExpansionCacheKey(string Path, bool AnnotateIncludes, bool EmitDeadCodeMarkers, string SearchRootsKey, ShaderSourceProviderOwner Owner);
    private readonly record struct SnippetResolutionCacheKey(
        string ExpandedSource,
        string SearchRootsKey,
        bool HostFileAccess,
        long RegisteredSnippetVersion,
        bool EnableDeadCodeElimination,
        ShaderSourceProviderOwner Owner);

    private sealed class SearchContext
    {
        public SearchContext(string? sourceDirectory, string[] shaderRoots, string searchRootsKey, Action<string>? warningLogger,
            ShaderSourceProviderOwner owner, IReadOnlyDictionary<string, string>? canonicalSnippets = null)
        {
            SourceDirectory = sourceDirectory;
            ShaderRoots = shaderRoots;
            SearchRootsKey = searchRootsKey;
            WarningLogger = warningLogger;
            CanonicalSnippets = canonicalSnippets;
            Owner = owner;
        }

        public string? SourceDirectory { get; }
        public string[] ShaderRoots { get; }
        public string SearchRootsKey { get; }
        public Action<string>? WarningLogger { get; }
        public IReadOnlyDictionary<string, string>? CanonicalSnippets { get; }
        public ShaderSourceProviderOwner Owner { get; }
        public bool HostFileAccess => Owner.HostFileAccess;
        public IShaderSourceFileBackend FileBackend => Owner.FileBackend!;
        public IAssetFileSystem FileSystem => Owner.FileSystem!;
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
            ShaderSourceDirectoryDependency[] searchRootDependencies)
        {
            ExpandedSource = expandedSource;
            ResolvedPaths = resolvedPaths;
            FileDependencies = fileDependencies;
            SearchRootDependencies = searchRootDependencies;
        }

        public string ExpandedSource { get; }
        public string[] ResolvedPaths { get; }
        public ShaderSourceFileDependency[] FileDependencies { get; }
        public ShaderSourceDirectoryDependency[] SearchRootDependencies { get; }
    }

    private sealed class SnippetResolutionCacheEntry
    {
        public SnippetResolutionCacheEntry(
            string resolvedSource,
            ShaderSourceFileDependency[] fileDependencies,
            ShaderSourceDirectoryDependency[] searchRootDependencies)
        {
            ResolvedSource = resolvedSource;
            FileDependencies = fileDependencies;
            SearchRootDependencies = searchRootDependencies;
        }

        public string ResolvedSource { get; }
        public ShaderSourceFileDependency[] FileDependencies { get; }
        public ShaderSourceDirectoryDependency[] SearchRootDependencies { get; }
    }

    private sealed class FileIndexCacheEntry
    {
        public FileIndexCacheEntry(Dictionary<string, string> pathsByName, ShaderSourceDirectoryDependency[] directoryDependencies)
        {
            PathsByName = pathsByName;
            DirectoryDependencies = directoryDependencies;
        }

        public Dictionary<string, string> PathsByName { get; }
        public ShaderSourceDirectoryDependency[] DirectoryDependencies { get; }
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
            return new(source, [], [], [], CaptureOwner());

        SearchContext context = CreateSearchContext(sourcePath, options);

        if (!context.HostFileAccess && IncludeRegex().IsMatch(source))
            throw new NotSupportedException("ShaderSource.HostFileIncludeUnavailable: shader includes require host files; use already resolved source or an explicit cooked shader artifact on this runtime.");

        List<string> resolvedPaths = [];
        Dictionary<string, ShaderSourceFileDependency> fileDependencies = new(StringComparer.OrdinalIgnoreCase);

        string resolvedIncludes = ExpandIncludesRecursive(
            source,
            context.SourceDirectory,
            context,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            resolvedPaths,
            fileDependencies,
            annotateIncludes,
            options?.EmitIncludeDeadCodeMarkers == true);

        SnippetResolutionCacheEntry resolvedSnippets = ResolveSnippetsCached(
            resolvedIncludes,
            context,
            options?.EnableSnippetDeadCodeElimination == true);
        MergeDependencies(fileDependencies, resolvedSnippets.FileDependencies);

        return new(resolvedSnippets.ResolvedSource, [.. resolvedPaths], [.. fileDependencies.Values],
            resolvedSnippets.SearchRootDependencies, context.Owner);
    }

    internal static ResolvedShaderSource ResolveSourcePayload(
        string source,
        string? sourcePath,
        ShaderSourceResolverOptions? options = null,
        bool annotateIncludes = false)
        => ResolveSourcePayload(source, sourcePath, out _, options, annotateIncludes);

    internal static ResolvedShaderSource ResolveSourcePayload(
        string source,
        string? sourcePath,
        out ShaderSourceProviderOwner owner,
        ShaderSourceResolverOptions? options = null,
        bool annotateIncludes = false)
    {
        ShaderSourceResolutionResult result = ResolveSourceDetailed(source, sourcePath, options, annotateIncludes);
        owner = result.Owner;
        return ResolvedShaderSource.Create(sourcePath, source, result);
    }

    internal static bool AreDependenciesCurrent(IReadOnlyList<ShaderSourceFileDependency>? dependencies)
        => AreDependenciesCurrent(dependencies, CaptureOwner());

    internal static bool AreDependenciesCurrent(IReadOnlyList<ShaderSourceFileDependency>? dependencies, ShaderSourceProviderOwner owner)
        => AreDependenciesCurrent(dependencies, null, owner);

    internal static bool AreDependenciesCurrent(
        IReadOnlyList<ShaderSourceFileDependency>? dependencies,
        IReadOnlyList<ShaderSourceDirectoryDependency>? directoryDependencies,
        ShaderSourceProviderOwner owner)
    {
        if (!IsOwnerCurrent(owner))
            return false;
        if (dependencies is null)
            return directoryDependencies is null || AreDirectoriesCurrent(directoryDependencies, owner);

        // A file-expanded cache from a prior owner cannot be validated against
        // virtual catalog paths or a host without synchronous file access.
        if (dependencies.Count != 0 && !owner.HostFileAccess)
            return false;

        for (int i = 0; i < dependencies.Count; i++)
        {
            ShaderSourceFileDependency dependency = dependencies[i];
            if (!TryGetCurrentFileDependency(dependency.Path, owner, out ShaderSourceFileDependency currentDependency))
                return false;

            if (currentDependency.LastWriteTimeUtcTicks != dependency.LastWriteTimeUtcTicks ||
                currentDependency.Length != dependency.Length)
            {
                return false;
            }
        }

        return (directoryDependencies is null || AreDirectoriesCurrent(directoryDependencies, owner)) && IsOwnerCurrent(owner);
    }

    internal static void RegisterSnippet(string snippetName, string snippetSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snippetName);
        ArgumentNullException.ThrowIfNull(snippetSource);

        RegisteredSnippets[snippetName] = snippetSource;
        Interlocked.Increment(ref _registeredSnippetVersion);
        ShaderSourceDependencyIndex.InvalidateAll($"registered shader snippet '{snippetName}' changed");
    }

    internal static bool UnregisterSnippet(string snippetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snippetName);

        bool removed = RegisteredSnippets.TryRemove(snippetName, out _);
        if (removed)
        {
            Interlocked.Increment(ref _registeredSnippetVersion);
            ShaderSourceDependencyIndex.InvalidateAll($"registered shader snippet '{snippetName}' was removed");
        }

        return removed;
    }

    internal static bool TryGetSnippetSource(string snippetName, ShaderSourceResolverOptions? options, out string? snippetSource)
    {
        SearchContext context = CreateSearchContext(sourcePath: null, options);
        return TryLoadSnippet(context, snippetName, out snippetSource, out _);
    }

    internal static string ResolveCanonicalSnippetDirectives(string source, IReadOnlyDictionary<string, string> snippets)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(snippets);
        if (IncludeRegex().IsMatch(source))
            throw new InvalidDataException("ShaderSource.CanonicalIncludeUnsupported: canonical snippet expansion does not resolve files.");
        Dictionary<string, string> snapshot = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string text) in snippets)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(text);
            if (IncludeRegex().IsMatch(text))
                throw new InvalidDataException($"ShaderSource.CanonicalIncludeUnsupported: snippet '{name}' contains a file include.");
            snapshot.Add(name, text);
        }
        // Share the desktop expansion algorithm, including annotations and duplicate
        // directives, but neither its search providers nor its global resolution cache.
        SearchContext context = new(null, [], string.Empty, null, CaptureOwner(), snapshot);
        return ResolveSnippetsRecursive(source, context, new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
    }

    internal static IEnumerable<string> GetAvailableSnippetNames(ShaderSourceResolverOptions? options)
    {
        SearchContext context = CreateSearchContext(sourcePath: null, options);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (string registeredSnippet in RegisteredSnippets.Keys)
            names.Add(registeredSnippet);

        foreach (string shaderRoot in context.ShaderRoots)
        {
            if (!TryGetSnippetFileIndex(context, shaderRoot, out FileIndexCacheEntry? snippetIndex) || snippetIndex is null)
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
        return ResolveSnippetsCached(
            source,
            context,
            options?.EnableSnippetDeadCodeElimination == true).ResolvedSource;
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
        ShaderSourceProviderOwner owner = CaptureOwner();
        if (!owner.HostFileAccess)
        {
            Action<string>? nonFileWarningLogger = options?.WarningLogger;
            if (nonFileWarningLogger is null && RuntimeShaderServices.Current is IRuntimeShaderServices nonFileShaderServices)
                nonFileWarningLogger = nonFileShaderServices.LogWarning;
            return new(null, [], string.Empty, nonFileWarningLogger, owner);
        }

        string? sourceDirectory = string.IsNullOrWhiteSpace(sourcePath)
            ? null
            : Path.GetDirectoryName(sourcePath);

        List<string> shaderRoots = [];

        AddShaderRoot(shaderRoots, FindShaderRoot(sourcePath, sourceDirectory, owner.FileBackend!), owner.FileBackend!);
        if (options?.AdditionalShaderRoots is not null)
        {
            foreach (string shaderRoot in options.AdditionalShaderRoots)
                AddShaderRoot(shaderRoots, shaderRoot, owner.FileBackend!);
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
            warningLogger,
            owner);
    }

    private static void AddShaderRoot(List<string> shaderRoots, string? shaderRoot, IShaderSourceFileBackend backend)
    {
        if (string.IsNullOrWhiteSpace(shaderRoot) || !backend.DirectoryExists(shaderRoot))
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
        bool annotateIncludes,
        bool emitIncludeDeadCodeMarkers)
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

            IncludeExpansionCacheEntry expandedInclude = ExpandIncludeFile(resolvedPath, context, includeStack, annotateIncludes, emitIncludeDeadCodeMarkers);
            resolvedPaths.AddRange(expandedInclude.ResolvedPaths);
            MergeDependencies(fileDependencies, expandedInclude.FileDependencies);

            // Wrap the inlined content in machine-readable BEGIN/END INCLUDE
            // markers so the downstream dead-code eliminator can identify the
            // include region and trim unreferenced top-level declarations.
            // The markers are GLSL line comments, so they are inert at compile
            // time; humans benefit from them too as origin annotations.
            //
            // Disabled by default: include DCE has been observed to produce
            // GLSL the front-end accepts (linkStatus=1) but the NVIDIA back-end
            // crashes on first GPU use (TDR + nvoglv64.dll AV, 2026-05-07).
            // Enable with environment variable XRE_GLSL_DCE_INCLUDES=1 to opt
            // back into include DCE globally; Uber variant generation opts in
            // locally after feature/static pruning has made the source smaller.
            if (emitIncludeDeadCodeMarkers || EmitIncludeDceMarkers)
                output.AppendLine($"// ===== BEGIN INCLUDE: {includePath} =====");
            if (annotateIncludes)
                output.AppendLine($"// begin include {includePath}");
            output.AppendLine(expandedInclude.ExpandedSource);
            if (annotateIncludes)
                output.AppendLine($"// end include {includePath}");
            if (emitIncludeDeadCodeMarkers || EmitIncludeDceMarkers)
                output.AppendLine($"// ===== END INCLUDE: {includePath} =====");
        }

        return output.ToString();
    }

    private static IncludeExpansionCacheEntry ExpandIncludeFile(
        string includePath,
        SearchContext context,
        HashSet<string> includeStack,
        bool annotateIncludes,
        bool emitIncludeDeadCodeMarkers)
    {
        string normalizedPath = Path.GetFullPath(includePath);
        if (!includeStack.Add(normalizedPath))
            throw new InvalidOperationException($"Recursive shader include detected for '{normalizedPath}'.");

        try
        {
            IncludeExpansionCacheKey cacheKey = new(normalizedPath, annotateIncludes, emitIncludeDeadCodeMarkers, context.SearchRootsKey, context.Owner);
            if (IncludeExpansionCache.TryGetValue(cacheKey, out IncludeExpansionCacheEntry? cachedEntry) &&
                cachedEntry is not null &&
                AreDependenciesCurrent(cachedEntry.FileDependencies, context.Owner) &&
                AreDirectoriesCurrent(cachedEntry.SearchRootDependencies, context))
            {
                EnsureNoRecursiveDependency(normalizedPath, cachedEntry.FileDependencies, includeStack);
                return cachedEntry;
            }

            string includedSource = ReadTextFile(normalizedPath, context, out ShaderSourceFileDependency sourceDependency);
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
                annotateIncludes,
                emitIncludeDeadCodeMarkers);

            IncludeExpansionCacheEntry includeEntry = new(
                expandedSource,
                [.. resolvedPaths],
                [.. fileDependencies.Values],
                CaptureSearchRootDependencies(context));
            if (IsOwnerCurrent(context.Owner))
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

    private static SnippetResolutionCacheEntry ResolveSnippetsCached(
        string source,
        SearchContext context,
        bool enableDeadCodeElimination)
    {
        if (string.IsNullOrEmpty(source))
            return new(source, [], CaptureSearchRootDependencies(context));

        MatchCollection matches = SnippetDirectiveRegex().Matches(source);
        if (matches.Count == 0)
            return new(source, [], CaptureSearchRootDependencies(context));

        long registeredSnippetVersion = Volatile.Read(ref _registeredSnippetVersion);
        SnippetResolutionCacheKey cacheKey = new(
            source,
            context.SearchRootsKey,
            context.HostFileAccess,
            registeredSnippetVersion,
            enableDeadCodeElimination,
            context.Owner);
        if (SnippetResolutionCache.TryGetValue(cacheKey, out SnippetResolutionCacheEntry? cachedEntry) &&
            cachedEntry is not null &&
            AreDependenciesCurrent(cachedEntry.FileDependencies, context.Owner) &&
            AreDirectoriesCurrent(cachedEntry.SearchRootDependencies, context))
        {
            return cachedEntry;
        }

        Dictionary<string, ShaderSourceFileDependency> fileDependencies = new(StringComparer.OrdinalIgnoreCase);
        string resolvedSource = ResolveSnippetsRecursive(source, context, new HashSet<string>(StringComparer.OrdinalIgnoreCase), fileDependencies);
        if (enableDeadCodeElimination)
        {
            // Strip unreferenced top-level declarations from inlined snippet regions
            // for generated variants. Runtime shaders keep full snippets because shared
            // GLSL snippets can expose globals used by functions selected later by GL.
            resolvedSource = GlslSnippetDeadCodeEliminator.Trim(resolvedSource);
        }
        SnippetResolutionCacheEntry resolvedEntry = new(
            resolvedSource,
            [.. fileDependencies.Values],
            CaptureSearchRootDependencies(context));
        if (IsOwnerCurrent(context.Owner))
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
        if (context.CanonicalSnippets is not null)
        {
            fileDependency = default;
            if (!context.CanonicalSnippets.TryGetValue(snippetName, out snippetSource))
                throw new InvalidDataException($"ShaderSource.CanonicalSnippetMissing: '{snippetName}' is outside the supplied canonical dependency set.");
            return true;
        }
        if (RegisteredSnippets.TryGetValue(snippetName, out string? registeredSnippetSource))
        {
            if (!context.HostFileAccess && IncludeRegex().IsMatch(registeredSnippetSource))
                throw new NotSupportedException($"ShaderSource.HostFileIncludeUnavailable: registered snippet '{snippetName}' contains a file include on this runtime.");
            snippetSource = registeredSnippetSource;
            fileDependency = default;
            return true;
        }

        foreach (string shaderRoot in context.ShaderRoots)
        {
            if (!TryResolveSnippetPath(context, shaderRoot, snippetName, out string? snippetPath) || snippetPath is null)
                continue;

            snippetSource = ReadTextFile(snippetPath, context, out fileDependency);
            return true;
        }

        if (!context.HostFileAccess)
            throw new NotSupportedException($"ShaderSource.HostFileSnippetUnavailable: snippet '{snippetName}' is not registered in memory; file-backed snippets are unavailable on this runtime.");

        snippetSource = null;
        fileDependency = default;
        return false;
    }

    private static bool TryResolveSnippetPath(SearchContext context, string shaderRoot, string snippetName, out string? snippetPath)
    {
        snippetPath = null;
        if (!TryGetSnippetFileIndex(context, shaderRoot, out FileIndexCacheEntry? snippetIndex) || snippetIndex is null)
            return false;

        return snippetIndex.PathsByName.TryGetValue(snippetName, out snippetPath);
    }

    private static bool TryGetSnippetFileIndex(SearchContext context, string shaderRoot, out FileIndexCacheEntry? snippetIndex)
    {
        snippetIndex = null;
        if (string.IsNullOrWhiteSpace(shaderRoot))
            return false;

        string snippetsDirectory = Path.Combine(shaderRoot, "Snippets");
        if (!context.FileBackend.DirectoryExists(snippetsDirectory))
            return false;

        string normalizedDirectory = Path.GetFullPath(snippetsDirectory);
        if (SnippetFileIndexCache.TryGetValue((normalizedDirectory, context.Owner), out FileIndexCacheEntry? cachedIndex) &&
            cachedIndex is not null &&
            AreDirectoriesCurrent(cachedIndex.DirectoryDependencies, context))
        {
            snippetIndex = cachedIndex;
            return true;
        }

        Dictionary<string, string> pathsByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (string extension in SupportedSnippetExtensions)
        {
            foreach (string filePath in context.FileSystem.EnumerateFiles(normalizedDirectory, "*" + extension, SearchOption.TopDirectoryOnly))
                pathsByName.TryAdd(Path.GetFileNameWithoutExtension(filePath), Path.GetFullPath(filePath));

            foreach (string filePath in context.FileSystem.EnumerateFiles(normalizedDirectory, "*" + extension, SearchOption.AllDirectories))
                pathsByName.TryAdd(Path.GetFileNameWithoutExtension(filePath), Path.GetFullPath(filePath));
        }

        FileIndexCacheEntry rebuiltIndex = new(pathsByName, CaptureDirectoryDependencies(normalizedDirectory, context));
        if (IsOwnerCurrent(context.Owner))
            SnippetFileIndexCache[(normalizedDirectory, context.Owner)] = rebuiltIndex;
        snippetIndex = rebuiltIndex;
        return true;
    }

    private static string ReadTextFile(string normalizedPath, SearchContext context, out ShaderSourceFileDependency dependency)
    {
        if (!TryGetCurrentFileDependency(normalizedPath, context.Owner, out dependency))
            throw new FileNotFoundException($"Shader source file '{normalizedPath}' does not exist.", normalizedPath);

        if (TextFileCache.TryGetValue((normalizedPath, context.Owner), out CachedTextFile? cachedFile) &&
            cachedFile is not null &&
            cachedFile.Dependency.LastWriteTimeUtcTicks == dependency.LastWriteTimeUtcTicks &&
            cachedFile.Dependency.Length == dependency.Length)
        {
            return cachedFile.Text;
        }

        string text = context.FileBackend.ReadAllText(normalizedPath);
        if (IsOwnerCurrent(context.Owner))
            TextFileCache[(normalizedPath, context.Owner)] = new(text, dependency);
        return text;
    }

    private static bool TryGetCurrentFileDependency(string path, ShaderSourceProviderOwner owner, out ShaderSourceFileDependency dependency)
    {
        dependency = default;
        if (!owner.HostFileAccess || string.IsNullOrWhiteSpace(path) || !owner.FileBackend!.FileExists(path))
            return false;

        string normalizedPath = Path.GetFullPath(path);
        if (!owner.FileBackend.TryGetFileMetadata(normalizedPath, out long ticks, out long length))
            return false;

        dependency = new(normalizedPath, ticks, length);
        return true;
    }

    private static bool AreDirectoriesCurrent(IReadOnlyList<ShaderSourceDirectoryDependency> directoryDependencies, SearchContext context)
        => AreDirectoriesCurrent(directoryDependencies, context.Owner);

    private static bool AreDirectoriesCurrent(IReadOnlyList<ShaderSourceDirectoryDependency> directoryDependencies, ShaderSourceProviderOwner owner)
    {
        if (!IsOwnerCurrent(owner) || (directoryDependencies.Count != 0 && !owner.HostFileAccess))
            return false;
        for (int i = 0; i < directoryDependencies.Count; i++)
        {
            ShaderSourceDirectoryDependency dependency = directoryDependencies[i];
            if (!owner.FileBackend!.TryGetDirectoryLastWriteTimeUtcTicks(dependency.Path, out long currentTicks))
                return false;

            if (currentTicks != dependency.LastWriteTimeUtcTicks)
                return false;
        }

        return IsOwnerCurrent(owner);
    }

    private static ShaderSourceDirectoryDependency[] CaptureDirectoryDependencies(string rootDirectory, SearchContext context)
    {
        List<ShaderSourceDirectoryDependency> dependencies = [];
        foreach (string directory in context.FileSystem.EnumerateDirectories(rootDirectory, "*", SearchOption.AllDirectories))
        {
            context.FileBackend.TryGetDirectoryLastWriteTimeUtcTicks(directory, out long ticks);
            dependencies.Add(new(Path.GetFullPath(directory), ticks));
        }

        context.FileBackend.TryGetDirectoryLastWriteTimeUtcTicks(rootDirectory, out long rootTicks);
        dependencies.Add(new(Path.GetFullPath(rootDirectory), rootTicks));
        return [.. dependencies];
    }

    private static ShaderSourceDirectoryDependency[] CaptureSearchRootDependencies(SearchContext context)
    {
        Dictionary<string, ShaderSourceDirectoryDependency> dependencies = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < context.ShaderRoots.Length; i++)
        {
            foreach (ShaderSourceDirectoryDependency dependency in CaptureDirectoryDependencies(context.ShaderRoots[i], context))
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
            if (context.FileBackend.FileExists(absolutePath))
                return absolutePath;
        }

        if (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            string fromCurrentDirectory = Path.GetFullPath(Path.Combine(currentDirectory, includePath));
            if (context.FileBackend.FileExists(fromCurrentDirectory))
                return fromCurrentDirectory;
        }

        foreach (string shaderRoot in context.ShaderRoots)
        {
            string fromRoot = Path.GetFullPath(Path.Combine(shaderRoot, includePath));
            if (context.FileBackend.FileExists(fromRoot))
                return fromRoot;
        }

        if (includePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return null;

        foreach (string shaderRoot in context.ShaderRoots)
        {
            if (TryResolveIndexedShaderFile(context, shaderRoot, includePath, out string? indexedPath))
                return indexedPath;
        }

        return null;
    }

    private static bool TryResolveIndexedShaderFile(SearchContext context, string shaderRoot, string fileName, out string? resolvedPath)
    {
        resolvedPath = null;
        if (!TryGetShaderRootFileIndex(context, shaderRoot, out FileIndexCacheEntry? fileIndex) || fileIndex is null)
            return false;

        return fileIndex.PathsByName.TryGetValue(fileName, out resolvedPath);
    }

    private static bool TryGetShaderRootFileIndex(SearchContext context, string shaderRoot, out FileIndexCacheEntry? fileIndex)
    {
        fileIndex = null;
        if (string.IsNullOrWhiteSpace(shaderRoot) || !context.FileBackend.DirectoryExists(shaderRoot))
            return false;

        string normalizedRoot = Path.GetFullPath(shaderRoot);
        if (ShaderRootFileIndexCache.TryGetValue((normalizedRoot, context.Owner), out FileIndexCacheEntry? cachedIndex) &&
            cachedIndex is not null &&
            AreDirectoriesCurrent(cachedIndex.DirectoryDependencies, context))
        {
            fileIndex = cachedIndex;
            return true;
        }

        Dictionary<string, string> pathsByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (string filePath in context.FileSystem.EnumerateFiles(normalizedRoot, "*", SearchOption.AllDirectories))
            pathsByName.TryAdd(Path.GetFileName(filePath), Path.GetFullPath(filePath));

        FileIndexCacheEntry rebuiltIndex = new(pathsByName, CaptureDirectoryDependencies(normalizedRoot, context));
        if (IsOwnerCurrent(context.Owner))
            ShaderRootFileIndexCache[(normalizedRoot, context.Owner)] = rebuiltIndex;
        fileIndex = rebuiltIndex;
        return true;
    }

    private static string? FindShaderRoot(string? sourcePath, string? sourceDirectory, IShaderSourceFileBackend backend)
    {
        IEnumerable<string?> candidates = [sourcePath, sourceDirectory, AppContext.BaseDirectory];
        foreach (string? candidate in candidates)
        {
            string? root = WalkForShaderRoot(candidate, backend);
            if (!string.IsNullOrWhiteSpace(root))
                return root;
        }

        return null;
    }

    private static string? WalkForShaderRoot(string? startPath, IShaderSourceFileBackend backend)
    {
        if (string.IsNullOrWhiteSpace(startPath))
            return null;

        string fullPath = Path.GetFullPath(startPath);
        string? directory = backend.DirectoryExists(startPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath);

        while (directory is not null)
        {
            string trimmedDirectory = Path.TrimEndingDirectorySeparator(directory);
            if (string.Equals(Path.GetFileName(trimmedDirectory), "Shaders", StringComparison.OrdinalIgnoreCase))
                return directory;

            string buildCommonAssetsShaders = Path.Combine(directory, "Build", "CommonAssets", "Shaders");
            if (backend.DirectoryExists(buildCommonAssetsShaders))
                return buildCommonAssetsShaders;

            directory = Path.GetDirectoryName(trimmedDirectory);
        }

        return null;
    }
}
