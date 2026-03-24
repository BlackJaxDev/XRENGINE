using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Rendering;

internal static partial class ShaderSourceResolver
{
    private static readonly string[] SupportedSnippetExtensions = [".glsl", ".snip", ".frag", ".vert", ".fs", ".vs"];

    [GeneratedRegex(@"^\s*#\s*include\s+[""<](?<path>[^"">]+)["">]\s*$", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex IncludeRegex();

    [GeneratedRegex(@"#pragma\s+snippet\s+[""<](?<name>[^"">]+)["">]", RegexOptions.Compiled)]
    private static partial Regex SnippetDirectiveRegex();

    public static string ResolveSource(string source, string? sourcePath, bool annotateIncludes = false)
        => ResolveSource(source, sourcePath, out _, annotateIncludes);

    public static string ResolveSource(string source, string? sourcePath, out List<string> resolvedPaths, bool annotateIncludes = false)
    {
        resolvedPaths = [];

        if (string.IsNullOrWhiteSpace(source))
            return source;

        string? sourceDirectory = string.IsNullOrWhiteSpace(sourcePath)
            ? null
            : Path.GetDirectoryName(sourcePath);
        string? shaderRoot = FindShaderRoot(sourcePath, sourceDirectory);

        string resolved = ExpandIncludesRecursive(
            source,
            sourceDirectory,
            shaderRoot,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            resolvedPaths,
            annotateIncludes);

        return ResolveSnippetsRecursive(resolved, shaderRoot, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static string ExpandIncludesRecursive(
        string source,
        string? currentDirectory,
        string? shaderRoot,
        HashSet<string> includeStack,
        List<string> resolvedPaths,
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
            string resolvedPath = ResolveIncludePath(currentDirectory, shaderRoot, includePath)
                ?? throw new InvalidOperationException($"Failed to resolve shader include '{includePath}'.");

            string normalizedPath = Path.GetFullPath(resolvedPath);
            if (!includeStack.Add(normalizedPath))
                throw new InvalidOperationException($"Recursive shader include detected for '{normalizedPath}'.");

            resolvedPaths.Add(normalizedPath);

            string includedSource = File.ReadAllText(normalizedPath);
            string expandedInclude = ExpandIncludesRecursive(
                includedSource,
                Path.GetDirectoryName(normalizedPath),
                shaderRoot,
                includeStack,
                resolvedPaths,
                annotateIncludes);

            includeStack.Remove(normalizedPath);

            if (annotateIncludes)
            {
                output.AppendLine($"// begin include {includePath}");
                output.AppendLine(expandedInclude);
                output.AppendLine($"// end include {includePath}");
            }
            else
            {
                output.AppendLine(expandedInclude);
            }
        }

        return output.ToString();
    }

    private static string ResolveSnippetsRecursive(string source, string? shaderRoot, HashSet<string> resolvedSnippets)
    {
        if (string.IsNullOrEmpty(source))
            return source;

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
            else if (TryLoadSnippet(shaderRoot, snippetName, out string? snippetSource) && snippetSource is not null)
            {
                resolvedSnippets.Add(snippetName);
                result.AppendLine($"// ===== BEGIN SNIPPET: {snippetName} =====");
                result.Append(ResolveSnippetsRecursive(snippetSource, shaderRoot, resolvedSnippets));
                result.AppendLine();
                result.AppendLine($"// ===== END SNIPPET: {snippetName} =====");
            }
            else
            {
                RuntimeShaderServices.Current?.LogWarning($"Shader snippet '{snippetName}' not found.");
                result.AppendLine($"// [WARNING: Snippet '{snippetName}' not found]");
            }

            lastIndex = match.Index + match.Length;
        }

        result.Append(source, lastIndex, source.Length - lastIndex);
        return result.ToString();
    }

    private static bool TryLoadSnippet(string? shaderRoot, string snippetName, out string? snippetSource)
    {
        snippetSource = null;
        if (string.IsNullOrWhiteSpace(shaderRoot))
            return false;

        string snippetsDir = Path.Combine(shaderRoot, "Snippets");
        if (!Directory.Exists(snippetsDir))
            return false;

        foreach (string extension in SupportedSnippetExtensions)
        {
            string directPath = Path.Combine(snippetsDir, snippetName + extension);
            if (File.Exists(directPath))
            {
                snippetSource = File.ReadAllText(directPath);
                return true;
            }

            try
            {
                string? nested = Directory.EnumerateFiles(snippetsDir, snippetName + extension, SearchOption.AllDirectories).FirstOrDefault();
                if (nested is not null)
                {
                    snippetSource = File.ReadAllText(nested);
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private static string? ResolveIncludePath(string? currentDirectory, string? shaderRoot, string includePath)
    {
        if (string.IsNullOrWhiteSpace(includePath))
            return null;

        if (Path.IsPathRooted(includePath) && File.Exists(includePath))
            return includePath;

        if (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            string fromCurrent = Path.GetFullPath(Path.Combine(currentDirectory, includePath));
            if (File.Exists(fromCurrent))
                return fromCurrent;
        }

        if (!string.IsNullOrWhiteSpace(shaderRoot))
        {
            string fromRoot = Path.GetFullPath(Path.Combine(shaderRoot, includePath));
            if (File.Exists(fromRoot))
                return fromRoot;

            if (includePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0)
            {
                try
                {
                    return Directory.EnumerateFiles(shaderRoot, includePath, SearchOption.AllDirectories).FirstOrDefault();
                }
                catch
                {
                }
            }
        }

        return null;
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