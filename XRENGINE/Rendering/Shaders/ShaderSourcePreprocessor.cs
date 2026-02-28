using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Extensions;

namespace XREngine.Rendering.Shaders;

public static class ShaderSourcePreprocessor
{
    private static readonly Regex IncludeRegex = new(
        @"^\s*#\s*include\s+[""<](?<path>[^"">]+)["">]\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

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

        string resolved = ExpandIncludesRecursive(
            source,
            sourceDirectory,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            resolvedPaths,
            annotateIncludes);

        return ShaderSnippets.ResolveSnippets(resolved);
    }

    private static string ExpandIncludesRecursive(
        string source,
        string? currentDirectory,
        HashSet<string> includeStack,
        List<string> resolvedPaths,
        bool annotateIncludes)
    {
        StringBuilder output = new(source.Length + 128);
        using StringReader reader = new(source);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            Match includeMatch = IncludeRegex.Match(line);
            if (!includeMatch.Success)
            {
                output.AppendLine(line);
                continue;
            }

            string includePath = includeMatch.Groups["path"].Value.Trim();
            string resolvedPath = ResolveIncludePath(currentDirectory, includePath)
                ?? throw new InvalidOperationException($"Failed to resolve shader include '{includePath}'.");

            string normalizedPath = Path.GetFullPath(resolvedPath);
            if (!includeStack.Add(normalizedPath))
                throw new InvalidOperationException($"Recursive shader include detected for '{normalizedPath}'.");

            resolvedPaths.Add(normalizedPath);

            string includedSource = File.ReadAllText(normalizedPath);
            string expandedInclude = ExpandIncludesRecursive(
                includedSource,
                Path.GetDirectoryName(normalizedPath),
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

    private static string? ResolveIncludePath(string? currentDirectory, string includePath)
    {
        if (string.IsNullOrWhiteSpace(includePath))
            return null;

        // 1) obvious absolute path (e.g. C:\\... or C://...)
        if (IsObviousAbsolutePath(includePath))
        {
            string normalizedAbsolute = includePath.Replace('/', Path.DirectorySeparatorChar);
            if (File.Exists(normalizedAbsolute))
                return normalizedAbsolute;
        }

        // 2) relative to the including shader file directory
        if (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            string fromCurrentDirectory = Path.GetFullPath(Path.Combine(currentDirectory, includePath));
            if (File.Exists(fromCurrentDirectory))
                return fromCurrentDirectory;
        }

        // 3) absolute fallback from shader roots
        string?[] roots = [GetEngineShaderRoot(), GetGameShaderRoot()];
        foreach (string? root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            string fromRoot = Path.GetFullPath(Path.Combine(root, includePath));
            if (File.Exists(fromRoot))
                return fromRoot;
        }

        return FindIncludeInShaderRoots(includePath);
    }

    private static bool IsObviousAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.IsAbsolutePath())
            return true;

        if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
            return true;

        return false;
    }

    private static string? GetEngineShaderRoot()
        => string.IsNullOrWhiteSpace(Engine.Assets?.EngineAssetsPath)
            ? null
            : Path.Combine(Engine.Assets!.EngineAssetsPath, "Shaders");

    private static string? GetGameShaderRoot()
        => string.IsNullOrWhiteSpace(Engine.Assets?.GameAssetsPath)
            ? null
            : Path.Combine(Engine.Assets!.GameAssetsPath, "Shaders");

    private static string? FindIncludeInShaderRoots(string includePath)
    {
        if (string.IsNullOrWhiteSpace(includePath))
            return null;

        bool hasDirectory = includePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0;
        string?[] roots = [GetEngineShaderRoot(), GetGameShaderRoot()];

        foreach (string? root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            if (hasDirectory)
            {
                string candidate = Path.Combine(root, includePath);
                if (File.Exists(candidate))
                    return candidate;
                continue;
            }

            try
            {
                foreach (string candidate in Directory.EnumerateFiles(root, includePath, SearchOption.AllDirectories))
                    return candidate;
            }
            catch
            {
                // Ignore IO errors and continue trying remaining roots.
            }
        }

        return null;
    }
}
