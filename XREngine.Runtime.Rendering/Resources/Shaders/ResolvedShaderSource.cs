using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Rendering;

public sealed class ResolvedShaderSource
{
    public ResolvedShaderSource(
        string? originalPath,
        string originalSource,
        string resolvedSource,
        IReadOnlyList<string>? resolvedPaths,
        IReadOnlyList<ShaderSourceFileDependency>? fileDependencies,
        ShaderSourceMacroSummary macroSummary,
        IReadOnlyList<ShaderSourceMapSpan>? sourceMapSpans = null)
    {
        OriginalPath = originalPath;
        OriginalSource = originalSource;
        ResolvedSource = resolvedSource;
        ResolvedPaths = resolvedPaths is null ? [] : [.. resolvedPaths];
        FileDependencies = fileDependencies is null ? [] : [.. fileDependencies];
        MacroSummary = macroSummary;
        SourceMapSpans = sourceMapSpans is null ? [] : [.. sourceMapSpans];
        SourceIdentity = ComputeIdentity();
    }

    public string? OriginalPath { get; }
    public string OriginalSource { get; }
    public string ResolvedSource { get; }
    public string[] ResolvedPaths { get; }
    public ShaderSourceFileDependency[] FileDependencies { get; }
    public ShaderSourceMacroSummary MacroSummary { get; }
    public ShaderSourceMapSpan[] SourceMapSpans { get; }
    public string SourceIdentity { get; }
    public int OriginalByteCount => Encoding.UTF8.GetByteCount(OriginalSource);
    public int ResolvedByteCount => Encoding.UTF8.GetByteCount(ResolvedSource);

    internal static ResolvedShaderSource Create(
        string? originalPath,
        string originalSource,
        ShaderSourceResolutionResult resolution)
        => new(
            originalPath,
            originalSource,
            resolution.Source,
            resolution.ResolvedPaths,
            resolution.FileDependencies,
            ShaderSourceMacroSummary.Scan(resolution.Source));

    private string ComputeIdentity()
    {
        var builder = new StringBuilder(256);
        builder.Append("path=").Append(OriginalPath ?? string.Empty).Append('\n');
        builder.Append("resolved=").Append(ComputeSha256Hex(ResolvedSource)).Append('\n');
        builder.Append("defines=").AppendJoin('|', MacroSummary.Defines).Append('\n');
        builder.Append("undefs=").AppendJoin('|', MacroSummary.Undefines).Append('\n');
        builder.Append("pragmas=").AppendJoin('|', MacroSummary.Pragmas).Append('\n');

        foreach (ShaderSourceFileDependency dependency in FileDependencies.OrderBy(static x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            builder
                .Append("dep=")
                .Append(dependency.Path)
                .Append('|')
                .Append(dependency.LastWriteTimeUtcTicks)
                .Append('|')
                .Append(dependency.Length)
                .Append('\n');
        }

        return ComputeSha256Hex(builder.ToString());
    }

    internal static string ComputeSha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

public sealed class OptimizedShaderSource
{
    public OptimizedShaderSource(
        ResolvedShaderSource resolvedSource,
        ResolvedShaderSourceOptimizationResult optimization,
        string optimizerIdentity)
    {
        ResolvedSource = resolvedSource;
        Optimization = optimization;
        Source = optimization.Source;
        OptimizerIdentity = optimizerIdentity;
        SourceIdentity = ResolvedShaderSource.ComputeSha256Hex(string.Concat(
            resolvedSource.SourceIdentity,
            "\noptimizer=",
            optimizerIdentity,
            "\nsource=",
            ResolvedShaderSource.ComputeSha256Hex(Source)));
    }

    public ResolvedShaderSource ResolvedSource { get; }
    public ResolvedShaderSourceOptimizationResult Optimization { get; }
    public string Source { get; }
    public string OptimizerIdentity { get; }
    public string SourceIdentity { get; }
}

public readonly record struct ShaderSourceMapSpan(
    string? SourcePath,
    int SourceStartLine,
    int SourceLineCount,
    int ResolvedStartLine,
    int ResolvedLineCount);

public readonly partial record struct ShaderSourceMacroSummary(
    string[] Defines,
    string[] Undefines,
    string[] Pragmas)
{
    [GeneratedRegex(@"^\s*#\s*define\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b(?<value>[^\r\n]*)", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex DefineRegex();

    [GeneratedRegex(@"^\s*#\s*undef\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex UndefRegex();

    [GeneratedRegex(@"^\s*#\s*pragma\s+(?<value>[^\r\n]*)", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex PragmaRegex();

    public static ShaderSourceMacroSummary Empty { get; } = new([], [], []);

    public static ShaderSourceMacroSummary Scan(string source)
    {
        if (string.IsNullOrEmpty(source))
            return Empty;

        return new(
            ScanDefines(source),
            ScanNames(source, UndefRegex()),
            ScanPragmas(source));
    }

    private static string[] ScanDefines(string source)
    {
        HashSet<string> values = new(StringComparer.Ordinal);
        foreach (Match match in DefineRegex().Matches(source))
        {
            string name = match.Groups["name"].Value;
            string value = match.Groups["value"].Value.Trim();
            values.Add(string.IsNullOrEmpty(value) ? name : string.Concat(name, "=", value));
        }

        return [.. values.OrderBy(static x => x, StringComparer.Ordinal)];
    }

    private static string[] ScanNames(string source, Regex regex)
    {
        HashSet<string> values = new(StringComparer.Ordinal);
        foreach (Match match in regex.Matches(source))
            values.Add(match.Groups["name"].Value);

        return [.. values.OrderBy(static x => x, StringComparer.Ordinal)];
    }

    private static string[] ScanPragmas(string source)
    {
        HashSet<string> values = new(StringComparer.Ordinal);
        foreach (Match match in PragmaRegex().Matches(source))
            values.Add(match.Groups["value"].Value.Trim());

        return [.. values.OrderBy(static x => x, StringComparer.Ordinal)];
    }
}
