using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using XREngine.Data.Core;

namespace XREngine.Rendering.Shaders;

/// <summary>
/// Manages engine shader snippets that can be included in user shaders.
/// Snippets are included via #pragma snippet "SnippetName" directives.
/// 
/// Snippets are lazy-loaded from the Snippets directory on first request.
/// 
/// Example usage in GLSL:
///   #pragma snippet "ForwardLighting"
///   #pragma snippet "DepthUtils"
///   #pragma snippet "ColorConversion"
/// </summary>
public static partial class ShaderSnippets
{
    /// <summary>
    /// Snippet directive pattern: #pragma snippet "name" or #pragma snippet &lt;name&gt;
    /// </summary>
    [GeneratedRegex(@"#pragma\s+snippet\s+[""<]([^"">]+)["">]", RegexOptions.Compiled)]
    private static partial Regex SnippetDirectiveRegex();

    /// <summary>
    /// Cache of loaded snippets.
    /// Key is the snippet name (case-insensitive), value is the GLSL source code.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> _snippetCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Set of snippet names that have been attempted to load (to avoid re-trying failed loads).
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> _loadAttempted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Lock object for directory scanning.
    /// </summary>
    private static readonly object _scanLock = new();

    /// <summary>
    /// Whether the snippets directory has been scanned for available snippets.
    /// </summary>
    private static bool _directoryScanned;

    /// <summary>
    /// Available snippet names discovered from the directory (without loading content).
    /// </summary>
    private static readonly HashSet<string> _availableSnippets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Path to the engine snippets directory.
    /// </summary>
    private static string? _snippetsDirectory;

    /// <summary>
    /// Supported snippet file extensions.
    /// </summary>
    private static readonly string[] SupportedExtensions = [".glsl", ".snip", ".frag", ".vert", ".fs", ".vs"];

    /// <summary>
    /// Gets the snippets directory path, initializing it if needed.
    /// </summary>
    private static string SnippetsDirectory
    {
        get
        {
            _snippetsDirectory ??= Path.Combine(Engine.Assets.EngineAssetsPath ?? ".", "Shaders", "Snippets");
            return _snippetsDirectory;
        }
    }

    /// <summary>
    /// Register a named snippet programmatically (for runtime-defined snippets).
    /// </summary>
    /// <param name="name">The snippet name (used in #pragma snippet "name")</param>
    /// <param name="source">The GLSL source code</param>
    public static void Register(string name, string source)
    {
        _snippetCache[name] = source;
        _loadAttempted[name] = true;
        lock (_scanLock)
        {
            _availableSnippets.Add(name);
        }
    }

    /// <summary>
    /// Unregister a snippet by name.
    /// </summary>
    /// <param name="name">The snippet name</param>
    /// <returns>True if the snippet was found and removed</returns>
    public static bool Unregister(string name)
    {
        _loadAttempted.TryRemove(name, out _);
        lock (_scanLock)
        {
            _availableSnippets.Remove(name);
        }
        return _snippetCache.TryRemove(name, out _);
    }

    /// <summary>
    /// Try to get a snippet by name, lazy-loading from disk if not already cached.
    /// </summary>
    /// <param name="name">The snippet name</param>
    /// <param name="source">The snippet source code if found</param>
    /// <returns>True if the snippet was found</returns>
    public static bool TryGet(string name, out string? source)
    {
        // Check cache first
        if (_snippetCache.TryGetValue(name, out source))
            return true;

        // Try to lazy-load from disk
        source = LazyLoadSnippet(name);
        return source != null;
    }

    /// <summary>
    /// Get all available snippet names (scans directory if not already done).
    /// </summary>
    public static IEnumerable<string> GetAllNames()
    {
        EnsureDirectoryScanned();
        lock (_scanLock)
        {
            return [.. _availableSnippets];
        }
    }

    /// <summary>
    /// Clears the snippet cache, forcing snippets to be reloaded on next access.
    /// </summary>
    public static void ClearCache()
    {
        _snippetCache.Clear();
        _loadAttempted.Clear();
        lock (_scanLock)
        {
            _directoryScanned = false;
            _availableSnippets.Clear();
        }
    }

    /// <summary>
    /// Process shader source and resolve all #pragma snippet directives.
    /// </summary>
    /// <param name="source">The shader source code</param>
    /// <param name="resolvedSnippets">Set of already-resolved snippet names to prevent infinite recursion</param>
    /// <returns>The processed source with snippets inlined</returns>
    public static string ResolveSnippets(string source, HashSet<string>? resolvedSnippets = null)
    {
        if (string.IsNullOrEmpty(source))
            return source;

        resolvedSnippets ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var regex = SnippetDirectiveRegex();
        var matches = regex.Matches(source);

        if (matches.Count == 0)
            return source;

        var result = new StringBuilder(source.Length * 2);
        int lastIndex = 0;

        foreach (Match match in matches)
        {
            // Append text before this match
            result.Append(source, lastIndex, match.Index - lastIndex);

            string snippetName = match.Groups[1].Value;

            if (resolvedSnippets.Contains(snippetName))
            {
                // Already included this snippet - skip to avoid duplicates/recursion
                result.AppendLine($"// [Snippet '{snippetName}' already included]");
            }
            else if (TryGet(snippetName, out string? snippetSource) && snippetSource != null)
            {
                resolvedSnippets.Add(snippetName);

                // Add a comment marker for debugging
                result.AppendLine($"// ===== BEGIN SNIPPET: {snippetName} =====");

                // Recursively resolve any snippets within this snippet
                string resolvedSnippet = ResolveSnippets(snippetSource, resolvedSnippets);
                result.Append(resolvedSnippet);

                result.AppendLine();
                result.AppendLine($"// ===== END SNIPPET: {snippetName} =====");
            }
            else
            {
                // Snippet not found - emit warning comment
                result.AppendLine($"// [WARNING: Snippet '{snippetName}' not found]");
                Debug.LogWarning($"Shader snippet '{snippetName}' not found. Available snippets: {string.Join(", ", GetAllNames())}");
            }

            lastIndex = match.Index + match.Length;
        }

        // Append remaining text after last match
        result.Append(source, lastIndex, source.Length - lastIndex);

        return result.ToString();
    }

    /// <summary>
    /// Lazy-loads a snippet from disk by name.
    /// </summary>
    /// <param name="name">The snippet name (filename without extension)</param>
    /// <returns>The snippet source, or null if not found</returns>
    private static string? LazyLoadSnippet(string name)
    {
        // Check if we've already tried to load this snippet
        if (_loadAttempted.TryGetValue(name, out bool attempted) && attempted)
            return null;

        // Mark as attempted
        _loadAttempted[name] = true;

        string dir = SnippetsDirectory;
        if (!Directory.Exists(dir))
        {
            Debug.Out($"Shader snippets directory not found: {dir}");
            return null;
        }

        // Try each supported extension
        foreach (string ext in SupportedExtensions)
        {
            string filePath = Path.Combine(dir, name + ext);
            if (File.Exists(filePath))
            {
                return LoadSnippetFromFile(name, filePath);
            }

            // Also search subdirectories
            string? foundPath = FindSnippetFile(dir, name, ext);
            if (foundPath != null)
            {
                return LoadSnippetFromFile(name, foundPath);
            }
        }

        Debug.Out($"Shader snippet '{name}' not found in {dir}");
        return null;
    }

    /// <summary>
    /// Finds a snippet file in the directory tree.
    /// </summary>
    private static string? FindSnippetFile(string directory, string name, string extension)
    {
        try
        {
            string pattern = name + extension;
            string[] files = Directory.GetFiles(directory, pattern, SearchOption.AllDirectories);
            return files.Length > 0 ? files[0] : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Loads a snippet from a file and caches it.
    /// </summary>
    private static string? LoadSnippetFromFile(string name, string filePath)
    {
        try
        {
            string source = File.ReadAllText(filePath);
            _snippetCache[name] = source;
            
            lock (_scanLock)
            {
                _availableSnippets.Add(name);
            }

            Debug.Out($"Lazy-loaded shader snippet: {name} from {filePath}");
            return source;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to load shader snippet '{name}' from {filePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Scans the snippets directory to discover available snippet names (without loading content).
    /// </summary>
    private static void EnsureDirectoryScanned()
    {
        if (_directoryScanned)
            return;

        lock (_scanLock)
        {
            if (_directoryScanned)
                return;

            _directoryScanned = true;

            string dir = SnippetsDirectory;
            if (!Directory.Exists(dir))
            {
                Debug.Out($"Shader snippets directory not found: {dir}");
                return;
            }

            try
            {
                foreach (string ext in SupportedExtensions)
                {
                    string pattern = "*" + ext;
                    foreach (string file in Directory.GetFiles(dir, pattern, SearchOption.AllDirectories))
                    {
                        string snippetName = Path.GetFileNameWithoutExtension(file);
                        _availableSnippets.Add(snippetName);
                    }
                }

                Debug.Out($"Discovered {_availableSnippets.Count} shader snippets in {dir}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to scan shader snippets directory: {ex.Message}");
            }
        }
    }
}
