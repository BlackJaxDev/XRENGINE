using System.Text;
using System.Text.RegularExpressions;
using XREngine.Data.Core;
using RuntimeResolverOptions = XREngine.Rendering.ShaderSourceResolverOptions;

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
    /// Register a named snippet programmatically (for runtime-defined snippets).
    /// </summary>
    /// <param name="name">The snippet name (used in #pragma snippet "name")</param>
    /// <param name="source">The GLSL source code</param>
    public static void Register(string name, string source)
        => global::XREngine.Rendering.ShaderSourceResolver.RegisterSnippet(name, source);

    /// <summary>
    /// Unregister a snippet by name.
    /// </summary>
    /// <param name="name">The snippet name</param>
    /// <returns>True if the snippet was found and removed</returns>
    public static bool Unregister(string name)
        => global::XREngine.Rendering.ShaderSourceResolver.UnregisterSnippet(name);

    /// <summary>
    /// Try to get a snippet by name, lazy-loading from disk if not already cached.
    /// </summary>
    /// <param name="name">The snippet name</param>
    /// <param name="source">The snippet source code if found</param>
    /// <returns>True if the snippet was found</returns>
    public static bool TryGet(string name, out string? source)
        => global::XREngine.Rendering.ShaderSourceResolver.TryGetSnippetSource(name, CreateResolverOptions(), out source);

    /// <summary>
    /// Get all available snippet names (scans directory if not already done).
    /// </summary>
    public static IEnumerable<string> GetAllNames()
        => global::XREngine.Rendering.ShaderSourceResolver.GetAvailableSnippetNames(CreateResolverOptions());

    /// <summary>
    /// Clears the snippet cache, forcing snippets to be reloaded on next access.
    /// </summary>
    public static void ClearCache()
        => global::XREngine.Rendering.ShaderSourceResolver.ClearCaches(clearRegisteredSnippets: true);

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

    private static RuntimeResolverOptions CreateResolverOptions()
    {
        List<string> additionalRoots = [];
        if (!string.IsNullOrWhiteSpace(Engine.Assets?.EngineAssetsPath))
            additionalRoots.Add(Path.Combine(Engine.Assets.EngineAssetsPath, "Shaders"));
        if (!string.IsNullOrWhiteSpace(Engine.Assets?.GameAssetsPath))
            additionalRoots.Add(Path.Combine(Engine.Assets.GameAssetsPath, "Shaders"));

        return new RuntimeResolverOptions
        {
            AdditionalShaderRoots = additionalRoots,
            WarningLogger = message => Debug.LogWarning(message),
        };
    }
}
