using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Rendering;

public sealed class ResolvedShaderSourceOptimizationOptions
{
    public static ResolvedShaderSourceOptimizationOptions Default { get; } = new();

    public IReadOnlyDictionary<string, string>? StaticLiterals { get; init; }
    public bool FailsOpen { get; init; } = true;
    public bool StripStaticUniformDeclarations { get; init; } = true;
    public bool InlineStaticLiterals { get; init; } = true;
    public bool PruneSnippetRegions { get; init; } = true;
    public bool PruneWholeSource { get; init; } = true;
    public bool KeepStageInterfaces { get; init; } = true;
    public bool KeepLayoutBoundResources { get; init; } = true;
    public bool KeepAnnotatedSymbols { get; init; } = true;
    public bool StripRegionMarkers { get; init; } = true;
    public bool CollapseBlankLines { get; init; } = true;
    public IReadOnlyCollection<string>? RootNames { get; init; }
    public string? DiagnosticLabel { get; init; }
}

public sealed class ResolvedShaderSourceOptimizationResult
{
    public ResolvedShaderSourceOptimizationResult(
        string source,
        int originalByteCount,
        int optimizedByteCount,
        int originalLineCount,
        int optimizedLineCount,
        int foldedStaticLiteralCount,
        IReadOnlyList<string>? enabledPasses = null,
        IReadOnlyList<string>? rootNames = null,
        string? optimizerIdentity = null)
    {
        Source = source;
        OriginalByteCount = originalByteCount;
        OptimizedByteCount = optimizedByteCount;
        OriginalLineCount = originalLineCount;
        OptimizedLineCount = optimizedLineCount;
        FoldedStaticLiteralCount = foldedStaticLiteralCount;
        EnabledPasses = enabledPasses is null ? [] : [.. enabledPasses];
        RootNames = rootNames is null ? [] : [.. rootNames];
        OptimizerIdentity = optimizerIdentity ?? string.Empty;
    }

    public string Source { get; }
    public int OriginalByteCount { get; }
    public int OptimizedByteCount { get; }
    public int OriginalLineCount { get; }
    public int OptimizedLineCount { get; }
    public int FoldedStaticLiteralCount { get; }
    public string[] EnabledPasses { get; }
    public string[] RootNames { get; }
    public string OptimizerIdentity { get; }
    public int RemovedByteCount => Math.Max(0, OriginalByteCount - OptimizedByteCount);
    public int RemovedLineCount => Math.Max(0, OriginalLineCount - OptimizedLineCount);
    public bool Changed => RemovedByteCount != 0 || FoldedStaticLiteralCount != 0;
}

public static partial class ResolvedShaderSourceOptimizer
{
    public const int Version = 1;

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.Compiled)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"^\s*(?:layout\s*\([^)]*\)\s*)?(?<qualifier>in|out)\s+[^;{]+?\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*\[[^\]]*\])?\s*;", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex StageInterfaceRegex();

    [GeneratedRegex(@"^\s*(?://\s*@keep|//\s*@shader-interface|#\s*pragma\s+xre_keep)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex KeepAnnotationRegex();

    [GeneratedRegex(@"\bvoid\s+main\s*\(", RegexOptions.Compiled)]
    private static partial Regex MainFunctionRegex();

    [GeneratedRegex(@"^\s*layout\s*\((?=[^)]*\bbinding\s*=)[^)]*\)\s*(?:[A-Za-z_][A-Za-z0-9_]*\s+)*(?:uniform|buffer)\s+(?:(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{|[^;{]+?\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*\[[^\]]*\])?\s*;)", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex LayoutBoundResourceRegex();

    [GeneratedRegex(@"^\s*(?:layout\s*\([^)]*\)\s*)?uniform\s+[^;{]+?\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*\[[^\]]*\])?\s*;", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex UniformDeclarationRegex();

    public static string BuildIdentitySegment(ResolvedShaderSourceOptimizationOptions? options = null)
    {
        options ??= ResolvedShaderSourceOptimizationOptions.Default;

        var builder = new StringBuilder(160);
        builder.Append("optimizer=v").Append(Version);
        builder.Append(";enabled=").Append(RenderDiagnosticsFlags.ShaderSourceOptimizerEnabled ? '1' : '0');
        builder.Append(";failsOpen=").Append(options.FailsOpen ? '1' : '0');
        builder.Append(";passes=").AppendJoin(',', BuildEnabledPasses(options));

        if (options.StaticLiterals is { Count: > 0 } staticLiterals)
        {
            builder.Append(";static=");
            bool first = true;
            foreach (var pair in staticLiterals.OrderBy(static x => x.Key, StringComparer.Ordinal))
            {
                if (!first)
                    builder.Append('|');
                builder.Append(pair.Key).Append('=').Append(pair.Value);
                first = false;
            }
        }

        if (options.RootNames is { Count: > 0 } roots)
        {
            builder.Append(";roots=");
            builder.AppendJoin('|', roots.Where(static x => !string.IsNullOrWhiteSpace(x)).OrderBy(static x => x, StringComparer.Ordinal));
        }

        return builder.ToString();
    }

    public static OptimizedShaderSource Optimize(
        ResolvedShaderSource resolvedSource,
        ResolvedShaderSourceOptimizationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(resolvedSource);

        ResolvedShaderSourceOptimizationResult result = Optimize(resolvedSource.ResolvedSource, options);
        return new OptimizedShaderSource(resolvedSource, result, result.OptimizerIdentity);
    }

    public static ResolvedShaderSourceOptimizationResult Optimize(
        string source,
        ResolvedShaderSourceOptimizationOptions? options = null)
    {
        options ??= ResolvedShaderSourceOptimizationOptions.Default;
        if (string.IsNullOrEmpty(source))
            return new(source, 0, 0, 0, 0, 0);

        string optimizerIdentity = BuildIdentitySegment(options);
        string[] enabledPasses = BuildEnabledPasses(options);

        if (!RenderDiagnosticsFlags.ShaderSourceOptimizerEnabled)
        {
            int disabledBytes = Encoding.UTF8.GetByteCount(source);
            int disabledLines = CountLines(source);
            return new(source, disabledBytes, disabledBytes, disabledLines, disabledLines, 0, enabledPasses, [], optimizerIdentity);
        }

        string current = source;
        int originalBytes = Encoding.UTF8.GetByteCount(source);
        int originalLines = CountLines(source);
        int foldedLiterals = 0;

        IReadOnlyDictionary<string, string>? staticLiterals = options.StaticLiterals;
        if (staticLiterals is { Count: > 0 })
        {
            if (options.StripStaticUniformDeclarations)
                current = StripStaticUniformDeclarations(current, staticLiterals.Keys);
            if (options.InlineStaticLiterals)
                current = InlineStaticLiteralsInSource(current, staticLiterals, out foldedLiterals);
        }

        if (options.PruneSnippetRegions)
            current = GlslSnippetDeadCodeEliminator.Trim(current);

        if (options.PruneWholeSource)
        {
            string[] roots = BuildWholeSourceRoots(current, options);
            current = GlslSnippetDeadCodeEliminator.TrimWholeSource(current, roots);
            if (!options.FailsOpen || !LooksCatastrophicallyPruned(source, current))
            {
                if (options.StripRegionMarkers)
                    current = GlslSnippetDeadCodeEliminator.StripRegionMarkers(current);

                if (options.CollapseBlankLines)
                    current = CollapseBlankLineRuns(current);

                int prunedBytes = Encoding.UTF8.GetByteCount(current);
                int prunedLines = CountLines(current);
                return new(current, originalBytes, prunedBytes, originalLines, prunedLines, foldedLiterals, enabledPasses, roots, optimizerIdentity);
            }

            current = source;
            foldedLiterals = 0;
        }

        if (options.StripRegionMarkers)
            current = GlslSnippetDeadCodeEliminator.StripRegionMarkers(current);

        if (options.CollapseBlankLines)
            current = CollapseBlankLineRuns(current);

        int optimizedBytes = Encoding.UTF8.GetByteCount(current);
        int optimizedLines = CountLines(current);
        return new(current, originalBytes, optimizedBytes, originalLines, optimizedLines, foldedLiterals, enabledPasses, [], optimizerIdentity);
    }

    private static string[] BuildEnabledPasses(ResolvedShaderSourceOptimizationOptions options)
    {
        List<string> passes = [];
        if (options.StripStaticUniformDeclarations)
            passes.Add("strip-static-uniforms");
        if (options.InlineStaticLiterals)
            passes.Add("inline-static-literals");
        if (options.PruneSnippetRegions)
            passes.Add("prune-snippet-regions");
        if (options.PruneWholeSource)
            passes.Add("prune-whole-source");
        if (options.KeepStageInterfaces)
            passes.Add("keep-stage-interfaces");
        if (options.KeepLayoutBoundResources)
            passes.Add("keep-layout-bound-resources");
        if (options.KeepAnnotatedSymbols)
            passes.Add("keep-annotations");
        if (options.StripRegionMarkers)
            passes.Add("strip-region-markers");
        if (options.CollapseBlankLines)
            passes.Add("collapse-blank-lines");

        return [.. passes];
    }

    private static string[] BuildWholeSourceRoots(string source, ResolvedShaderSourceOptimizationOptions options)
    {
        bool hasExplicitRoots = options.RootNames?.Any(static root => !string.IsNullOrWhiteSpace(root)) == true;
        if (!hasExplicitRoots && !MainFunctionRegex().IsMatch(source))
            return [];

        HashSet<string> roots = new(StringComparer.Ordinal)
        {
            "main",
        };

        if (options.RootNames is not null)
        {
            foreach (string root in options.RootNames)
            {
                if (!string.IsNullOrWhiteSpace(root))
                    roots.Add(root);
            }
        }

        if (options.KeepStageInterfaces)
        {
            foreach (Match match in StageInterfaceRegex().Matches(source))
            {
                string name = match.Groups["name"].Value;
                if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith("gl_", StringComparison.Ordinal))
                    roots.Add(name);
            }
        }

        if (options.KeepLayoutBoundResources)
        {
            foreach (Match match in LayoutBoundResourceRegex().Matches(source))
            {
                string name = match.Groups["name"].Value;
                if (!string.IsNullOrWhiteSpace(name))
                    roots.Add(name);
            }
        }

        if (options.KeepAnnotatedSymbols)
        {
            foreach (Match match in KeepAnnotationRegex().Matches(source))
            {
                string name = match.Groups["name"].Value;
                if (!string.IsNullOrWhiteSpace(name))
                    roots.Add(name);
            }
        }

        return [.. roots];
    }

    private static bool LooksCatastrophicallyPruned(string original, string optimized)
    {
        if (string.IsNullOrWhiteSpace(optimized))
            return true;

        if (original.Contains("void main", StringComparison.Ordinal) &&
            !optimized.Contains("void main", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string StripStaticUniformDeclarations(string source, IEnumerable<string> staticPropertyNames)
    {
        HashSet<string> names = new(staticPropertyNames, StringComparer.Ordinal);
        if (names.Count == 0 || string.IsNullOrEmpty(source))
            return source;

        return UniformDeclarationRegex().Replace(source, match =>
        {
            string name = match.Groups["name"].Value;
            return names.Contains(name) ? string.Empty : match.Value;
        });
    }

    private static string InlineStaticLiteralsInSource(
        string source,
        IReadOnlyDictionary<string, string> staticLiterals,
        out int replacementCount)
    {
        replacementCount = 0;
        if (staticLiterals.Count == 0 || string.IsNullOrEmpty(source))
            return source;

        StringBuilder builder = new(source.Length);
        bool inBlockComment = false;
        bool pendingStructDeclaration = false;
        int structDeclarationDepth = 0;

        foreach (string line in SplitLinesPreservingNewlines(source))
        {
            int firstNonWhitespace = 0;
            while (firstNonWhitespace < line.Length &&
                   char.IsWhiteSpace(line[firstNonWhitespace]) &&
                   line[firstNonWhitespace] is not '\r' and not '\n')
            {
                firstNonWhitespace++;
            }

            if (firstNonWhitespace < line.Length && line[firstNonWhitespace] == '#')
            {
                builder.Append(line);
                continue;
            }

            if (!inBlockComment &&
                ShouldSkipStaticLiteralInliningForStructLine(line, ref pendingStructDeclaration, ref structDeclarationDepth))
            {
                builder.Append(line);
                continue;
            }

            InlineStaticLiteralsInLine(line, staticLiterals, builder, ref inBlockComment, ref replacementCount);
        }

        return builder.ToString();
    }

    private static bool ShouldSkipStaticLiteralInliningForStructLine(
        string line,
        ref bool pendingStructDeclaration,
        ref int structDeclarationDepth)
    {
        bool startsStruct = StartsWithStructKeyword(line);
        bool shouldSkip = pendingStructDeclaration || structDeclarationDepth > 0 || startsStruct;
        if (!shouldSkip)
            return false;

        if (startsStruct)
            pendingStructDeclaration = true;

        bool inLineComment = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inLineComment)
                break;

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (c == '{')
            {
                structDeclarationDepth++;
                pendingStructDeclaration = false;
                continue;
            }

            if (c == '}' && structDeclarationDepth > 0)
            {
                structDeclarationDepth--;
                continue;
            }

            if (c == ';' && structDeclarationDepth == 0)
                pendingStructDeclaration = false;
        }

        return true;
    }

    private static void InlineStaticLiteralsInLine(
        string line,
        IReadOnlyDictionary<string, string> staticLiterals,
        StringBuilder builder,
        ref bool inBlockComment,
        ref int replacementCount)
    {
        int i = 0;
        bool inString = false;
        while (i < line.Length)
        {
            char c = line[i];
            if (inBlockComment)
            {
                builder.Append(c);
                if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    builder.Append('/');
                    i += 2;
                    inBlockComment = false;
                    continue;
                }

                i++;
                continue;
            }

            if (inString)
            {
                builder.Append(c);
                if (c == '\\' && i + 1 < line.Length)
                {
                    builder.Append(line[i + 1]);
                    i += 2;
                    continue;
                }

                if (c == '"')
                    inString = false;
                i++;
                continue;
            }

            if (c == '/' && i + 1 < line.Length)
            {
                if (line[i + 1] == '/')
                {
                    builder.Append(line, i, line.Length - i);
                    return;
                }

                if (line[i + 1] == '*')
                {
                    builder.Append("/*");
                    i += 2;
                    inBlockComment = true;
                    continue;
                }
            }

            if (c == '"')
            {
                builder.Append(c);
                inString = true;
                i++;
                continue;
            }

            if (!IsIdentifierStart(c))
            {
                builder.Append(c);
                i++;
                continue;
            }

            int start = i++;
            while (i < line.Length && IsIdentifierPart(line[i]))
                i++;

            string identifier = line[start..i];
            bool isFieldName = start > 0 && line[start - 1] == '.';
            if (!isFieldName && staticLiterals.TryGetValue(identifier, out string? literal))
            {
                bool hasSuffixAccess = i < line.Length && line[i] == '.';
                builder.Append(hasSuffixAccess ? $"({literal})" : literal);
                replacementCount++;
                continue;
            }

            builder.Append(identifier);
        }
    }

    private static string CollapseBlankLineRuns(string source)
    {
        if (string.IsNullOrEmpty(source))
            return source;

        StringBuilder builder = new(source.Length);
        bool previousBlank = false;
        foreach (string line in SplitLinesPreservingNewlines(source))
        {
            bool blank = IsBlankLine(line);
            if (blank && previousBlank)
                continue;

            builder.Append(line);
            previousBlank = blank;
        }

        return builder.ToString();
    }

    private static List<string> SplitLinesPreservingNewlines(string source)
    {
        List<string> lines = [];
        int start = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] != '\n')
                continue;

            lines.Add(source[start..(i + 1)]);
            start = i + 1;
        }

        if (start < source.Length)
            lines.Add(source[start..]);
        return lines;
    }

    private static bool IsBlankLine(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c is '\r' or '\n')
                continue;
            if (!char.IsWhiteSpace(c))
                return false;
        }

        return true;
    }

    private static bool StartsWithStructKeyword(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i]))
            i++;

        const string keyword = "struct";
        if (line.Length - i < keyword.Length ||
            !line.AsSpan(i, keyword.Length).SequenceEqual(keyword))
        {
            return false;
        }

        int next = i + keyword.Length;
        return next >= line.Length || !IsIdentifierPart(line[next]);
    }

    private static bool IsIdentifierStart(char c)
        => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c)
        => char.IsLetterOrDigit(c) || c == '_';

    private static int CountLines(string source)
    {
        if (string.IsNullOrEmpty(source))
            return 0;

        int count = 1;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '\n')
                count++;
        }

        return count;
    }
}
