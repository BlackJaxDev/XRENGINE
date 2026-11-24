using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.ShaderTranslation.Preprocessing;

/// <summary>
/// Replaces <c>#include</c> directives with the text of the included files.
/// </summary>
/// <remarks>
/// <para>
/// Source analysis such as <see cref="GlobalWriteRewriter"/> must see the declarations in included files,
/// so the translator expands includes before it compiles. Each inserted file starts with a <c>#line</c>
/// directive, and the text after it continues with a <c>#line</c> directive for the including file.
/// Compiler diagnostics therefore keep the original file names and line numbers.
/// </para>
/// <para>
/// The expander does not evaluate conditions. An include in an inactive <c>#if</c> branch is expanded
/// in place and stays inactive. A <c>#pragma once</c> file gets a generated include guard instead of
/// a skip list, so this stays correct. A path that does not resolve keeps its directive, and the compiler
/// reports it only when the branch is active.
/// </para>
/// <para>
/// Include files can include each other, for example <c>UnityShaderVariables.cginc</c> and
/// <c>UnityShaderUtilities.cginc</c>. When a file includes a file that is still being expanded higher in the
/// include chain, and that file has an include guard or <c>#pragma once</c>, the expander leaves the include out.
/// The guard is defined at that point, so the preprocessor would skip the text too. A recursive include of
/// a file with no guard is an error.
/// </para>
/// <para>
/// A file resolves from the directory of the including file first, then from the include directories in order.
/// Unity resolves project paths such as <c>Assets/...</c> and <c>Packages/...</c> from the project root,
/// so callers put the project root in the include directories.
/// </para>
/// </remarks>
public sealed partial class ShaderIncludeExpander(IReadOnlyList<string> includeDirectories)
{
    /// <summary>Gets the maximum include depth. Deeper nesting is almost always a recursive include.</summary>
    public const int MaxDepth = 64;

    /// <summary>Gets the maximum size of the expanded text in characters.</summary>
    public const int MaxExpandedLength = 64 * 1024 * 1024;

    private readonly IReadOnlyList<string> _includeDirectories = includeDirectories ?? throw new ArgumentNullException(nameof(includeDirectories));
    private readonly Dictionary<string, string> _fileCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeFiles = new(StringComparer.Ordinal);

    /// <summary>Gets the files that the last expansion inserted, in first-use order.</summary>
    public List<string> IncludedFiles { get; } = [];

    /// <summary>Expands the includes in a translation unit.</summary>
    /// <param name="text">The translation unit. It can contain <c>#line</c> directives.</param>
    /// <param name="sourceDirectory">The directory that resolves relative includes of the unit itself.</param>
    /// <param name="sourcePath">The file name of the unit for <c>#line</c> directives before the first one in the text.</param>
    public string Expand(string text, string sourceDirectory, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(text);
        IncludedFiles.Clear();
        _activeFiles.Clear();
        StringBuilder builder = new(text.Length * 2);
        Append(builder, text, sourceDirectory, sourcePath, 0);
        return builder.ToString();
    }

    private void Append(StringBuilder builder, string text, string directory, string logicalPath, int depth)
    {
        int logicalLine = 1;
        bool inBlockComment = false;
        // A final line break ends the last line. It does not start an empty line.
        ReadOnlySpan<char> lines = text.EndsWith('\n') ? text.AsSpan(0, text.Length - 1) : text.AsSpan();
        foreach (ReadOnlySpan<char> rawLine in lines.EnumerateLines())
        {
            string line = rawLine.ToString();
            Match lineDirective = LineDirectivePattern().Match(line);
            Match include = inBlockComment ? Match.Empty : IncludePattern().Match(line);
            inBlockComment = UpdateBlockComment(line, inBlockComment);
            if (lineDirective.Success)
            {
                builder.Append(line).Append('\n');
                logicalLine = int.Parse(lineDirective.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (lineDirective.Groups["file"].Success)
                    logicalPath = lineDirective.Groups["file"].Value;
                continue;
            }

            string? resolved = include.Success ? Resolve(include.Groups["path"].Value, directory) : null;
            if (resolved is null && !inBlockComment && PragmaOncePattern().IsMatch(line))
            {
                // The generated guard replaces the directive. Keep the line count.
                builder.Append('\n');
                logicalLine++;
                continue;
            }

            if (resolved is null)
            {
                builder.Append(line).Append('\n');
                logicalLine++;
                continue;
            }

            if (depth >= MaxDepth)
                throw new InvalidOperationException($"Include depth is more than {MaxDepth} at '{resolved}'. The includes are probably recursive.");

            string content = ReadFile(resolved);
            bool once = PragmaOncePattern().IsMatch(content);
            if (_activeFiles.Contains(resolved) && (once || IncludeGuardPattern().IsMatch(content)))
            {
                // The guard of the file is already defined here. Keep the line count.
                builder.Append('\n');
                logicalLine++;
                continue;
            }

            if (!IncludedFiles.Contains(resolved))
                IncludedFiles.Add(resolved);

            string displayPath = resolved.Replace('\\', '/');
            string guard = once ? "XR_INCLUDE_ONCE_" + StableHash(resolved) : string.Empty;
            if (once)
                builder.Append("#ifndef ").Append(guard).Append("\n#define ").Append(guard).Append('\n');

            builder.Append("#line 1 \"").Append(displayPath).Append("\"\n");
            bool added = _activeFiles.Add(resolved);
            Append(builder, content, Path.GetDirectoryName(resolved)!, displayPath, depth + 1);
            if (added)
                _activeFiles.Remove(resolved);
            if (once)
                builder.Append("#endif\n");

            logicalLine++;
            builder.Append("#line ").Append(logicalLine).Append(" \"").Append(logicalPath).Append("\"\n");
            if (builder.Length > MaxExpandedLength)
                throw new InvalidOperationException($"The expanded program is larger than {MaxExpandedLength} characters.");
        }
    }

    private string? Resolve(string path, string directory)
    {
        if (Path.IsPathRooted(path))
            return File.Exists(path) ? Path.GetFullPath(path) : null;

        string candidate = Path.GetFullPath(Path.Combine(directory, path));
        if (File.Exists(candidate))
            return candidate;

        foreach (string includeDirectory in _includeDirectories)
        {
            candidate = Path.GetFullPath(Path.Combine(includeDirectory, path));
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private string ReadFile(string path)
    {
        if (!_fileCache.TryGetValue(path, out string? content))
        {
            content = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            _fileCache[path] = content;
        }

        return content;
    }

    private static bool UpdateBlockComment(string line, bool inBlockComment)
    {
        int index = 0;
        while (index < line.Length)
        {
            if (inBlockComment)
            {
                int close = line.IndexOf("*/", index, StringComparison.Ordinal);
                if (close < 0)
                    return true;
                inBlockComment = false;
                index = close + 2;
                continue;
            }

            int lineComment = line.IndexOf("//", index, StringComparison.Ordinal);
            int open = line.IndexOf("/*", index, StringComparison.Ordinal);
            if (open < 0 || (lineComment >= 0 && lineComment < open))
                return false;
            inBlockComment = true;
            index = open + 2;
        }

        return inBlockComment;
    }

    private static string StableHash(string value)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char character in value)
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^\s*#\s*include\s*[""<](?<path>[^"">]+)["">]", RegexOptions.CultureInvariant)]
    private static partial Regex IncludePattern();

    [GeneratedRegex(@"^\s*#\s*line\s+(?<line>\d+)(?:\s+""(?<file>[^""]*)"")?", RegexOptions.CultureInvariant)]
    private static partial Regex LineDirectivePattern();

    [GeneratedRegex(@"^\s*#\s*pragma\s+once\b", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex PragmaOncePattern();

    // The first directive of the file, after comments and blank lines, is #ifndef NAME or #if !defined(NAME), and the next one is #define NAME.
    [GeneratedRegex(@"\A(?:\s+|//[^\n]*|/\*.*?\*/)*#\s*(?:ifndef\s+(?<name>\w+)|if\s+!\s*defined\s*\(\s*(?<name>\w+)\s*\))\s*\n\s*#\s*define\s+\k<name>\b", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex IncludeGuardPattern();
}
