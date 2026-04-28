using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SPIRVCross;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders;

namespace XREngine.Editor.UI.Tools;

public enum ShaderEditorDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record ShaderEditorDiagnostic(
    int Line,
    int Column,
    ShaderEditorDiagnosticSeverity Severity,
    string Message,
    string RawText);

public sealed record ShaderEditorCompileResult(
    bool Success,
    string Source,
    string ResolvedSource,
    IReadOnlyList<ShaderEditorDiagnostic> Diagnostics,
    TimeSpan Duration,
    DateTimeOffset CompletedAt,
    int SpirvByteCount,
    bool SourceResolved);

public sealed record ShaderEditorCompletionItem(string Name, string Kind, string Detail);

public enum ShaderEditorVariantPreset
{
    ShadowCaster,
    DepthNormalPrePass,
    WeightedBlendedOit,
    PerPixelLinkedList,
    DepthPeeling,
    OptimizedLocked,
    CustomDefine
}

public sealed record ShaderEditorVariantResult(
    bool Success,
    ShaderEditorVariantPreset Preset,
    string Name,
    string Source,
    string Message);

public sealed record ShaderEditorCrossCompileResult(
    bool Success,
    string Source,
    byte[] SpirvBytes,
    string HlslSource,
    string GlslSource,
    TimeSpan Duration,
    DateTimeOffset CompletedAt,
    string Message);

public sealed record ShaderEditorShaderPreset(
    string DisplayName,
    string RelativePath,
    EShaderType ShaderType);

public enum ShaderEditorIncludeSourceKind
{
    EngineSnippet,
    RelativeInclude,
    AbsoluteInclude
}

public sealed record ShaderEditorIncludeCandidate(
    string DisplayName,
    string IncludePath,
    string ResolvedPath);

public sealed record ShaderEditorIncludePreview(
    bool Success,
    string DisplayName,
    string Directive,
    string ResolvedPath,
    string Source,
    string Message);

public static class ShaderEditorServices
{
    public const string ShadowCasterDefine = "XRENGINE_SHADOW_CASTER_PASS";
    public const string DepthNormalPrePassDefine = "XRENGINE_DEPTH_NORMAL_PREPASS";
    public const string WeightedBlendedOitDefine = "XRENGINE_FORWARD_WEIGHTED_OIT";
    public const string PerPixelLinkedListDefine = "XRENGINE_FORWARD_PPLL";
    public const string DepthPeelingDefine = "XRENGINE_FORWARD_DEPTH_PEEL";

    private static readonly string[] ShaderPreviewExtensions =
    [
        ".glsl", ".shader", ".frag", ".vert", ".geom", ".tesc", ".tese", ".comp", ".task", ".mesh",
        ".fs", ".vs", ".gs", ".tcs", ".tes", ".cs", ".ts", ".ms", ".hlsl", ".hlsli", ".fx", ".snip"
    ];

    private static readonly ShaderEditorShaderPreset[] ShaderPresets =
    [
        new("Colored Deferred", Path.Combine("Common", "ColoredDeferred.fs"), EShaderType.Fragment),
        new("Textured Deferred", Path.Combine("Common", "TexturedDeferred.fs"), EShaderType.Fragment),
        new("Textured Normal Deferred", Path.Combine("Common", "TexturedNormalDeferred.fs"), EShaderType.Fragment),
        new("Textured Normal Spec Deferred", Path.Combine("Common", "TexturedNormalSpecDeferred.fs"), EShaderType.Fragment),
        new("Textured Metallic Roughness Deferred", Path.Combine("Common", "TexturedMetallicRoughnessDeferred.fs"), EShaderType.Fragment),
        new("Lit Colored Forward", Path.Combine("Common", "LitColoredForward.fs"), EShaderType.Fragment),
        new("Lit Textured Forward", Path.Combine("Common", "LitTexturedForward.fs"), EShaderType.Fragment),
        new("Lit Textured Normal Forward", Path.Combine("Common", "LitTexturedNormalForward.fs"), EShaderType.Fragment),
        new("Unlit Colored Forward", Path.Combine("Common", "UnlitColoredForward.fs"), EShaderType.Fragment),
        new("Unlit Textured Forward", Path.Combine("Common", "UnlitTexturedForward.fs"), EShaderType.Fragment),
        new("Uber Forward", Path.Combine("Uber", "UberShader.frag"), EShaderType.Fragment)
    ];

    private static readonly Regex GlslangDiagnosticRegex = new(
        @"^(?<severity>ERROR|WARNING):\s*(?<file>[^:\r\n]*):(?<line>\d+):\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ClangStyleDiagnosticRegex = new(
        @"^(?<file>.*?):(?<line>\d+):(?:(?<column>\d+):)?\s*(?<severity>error|warning):\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DeclarationRegex = new(
        @"\b(?<qualifier>uniform|in|out|attribute|varying|const)\s+(?<type>[A-Za-z_]\w*)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FunctionRegex = new(
        @"\b(?<type>void|float|double|int|uint|bool|vec[234]|dvec[234]|ivec[234]|uvec[234]|bvec[234]|mat[234](?:x[234])?|[A-Za-z_]\w*)\s+(?<name>[A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LocalRegex = new(
        @"\b(?<type>float|double|int|uint|bool|vec[234]|dvec[234]|ivec[234]|uvec[234]|bvec[234]|mat[234](?:x[234])?|sampler\w+|image\w+|[A-Za-z_]\w*)\s+(?<name>[A-Za-z_]\w*)\s*(?:=|;|,|\[)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ControlWords = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "return", "discard", "layout"
    };

    private static readonly string[] GlslKeywords =
    [
        "break", "case", "centroid", "const", "continue", "default", "discard", "do", "else", "flat",
        "for", "highp", "if", "in", "inout", "invariant", "layout", "lowp", "mediump", "noperspective",
        "out", "patch", "precise", "return", "sample", "smooth", "struct", "subroutine", "switch", "uniform",
        "varying", "volatile", "while"
    ];

    private static readonly string[] GlslTypes =
    [
        "bool", "bvec2", "bvec3", "bvec4", "double", "dvec2", "dvec3", "dvec4", "float", "int", "ivec2",
        "ivec3", "ivec4", "mat2", "mat2x3", "mat2x4", "mat3", "mat3x2", "mat3x4", "mat4", "mat4x2",
        "mat4x3", "sampler1D", "sampler2D", "sampler2DArray", "sampler2DShadow", "sampler3D", "samplerCube",
        "samplerCubeArray", "uint", "uvec2", "uvec3", "uvec4", "vec2", "vec3", "vec4", "void"
    ];

    private static readonly string[] GlslBuiltins =
    [
        "abs", "acos", "all", "any", "asin", "atan", "ceil", "clamp", "cos", "cross", "dFdx", "dFdy",
        "distance", "dot", "exp", "exp2", "faceforward", "floor", "fract", "fwidth", "inverse", "inversesqrt",
        "length", "log", "log2", "max", "min", "mix", "mod", "normalize", "pow", "reflect", "refract", "round",
        "sign", "sin", "smoothstep", "sqrt", "step", "tan", "texelFetch", "texture", "textureGrad", "textureLod",
        "transpose"
    ];

    public static ShaderEditorCompileResult CompileGlsl(
        string source,
        string? sourcePath,
        EShaderType shaderType,
        string entryPoint)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string resolvedSource = source;
        bool sourceResolved = true;
        List<ShaderEditorDiagnostic> diagnostics = [];

        try
        {
            var shader = new XRShader(shaderType)
            {
                Source = new TextFile { Text = source, FilePath = sourcePath }
            };

            sourceResolved = shader.TryGetResolvedSource(out resolvedSource, logFailures: false);
            if (!sourceResolved)
            {
                diagnostics.Add(new ShaderEditorDiagnostic(
                    0,
                    0,
                    ShaderEditorDiagnosticSeverity.Warning,
                    "Shader include/snippet resolution failed; compiling raw source.",
                    string.Empty));
                resolvedSource = source;
            }

            byte[] spirv = ShaderCrossCompiler.CompileToSpirv(
                resolvedSource,
                shaderType,
                ShaderSourceLanguage.Glsl,
                string.IsNullOrWhiteSpace(sourcePath) ? "ShaderEditor.glsl" : Path.GetFileName(sourcePath),
                entryPoint);

            stopwatch.Stop();
            return new ShaderEditorCompileResult(
                true,
                source,
                resolvedSource,
                diagnostics,
                stopwatch.Elapsed,
                DateTimeOffset.Now,
                spirv.Length,
                sourceResolved);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            diagnostics.AddRange(ParseCompilerDiagnostics(ex.Message));
            if (diagnostics.Count == 0)
            {
                diagnostics.Add(new ShaderEditorDiagnostic(
                    0,
                    0,
                    ShaderEditorDiagnosticSeverity.Error,
                    ex.Message,
                    ex.Message));
            }

            return new ShaderEditorCompileResult(
                false,
                source,
                resolvedSource,
                diagnostics,
                stopwatch.Elapsed,
                DateTimeOffset.Now,
                0,
                sourceResolved);
        }
    }

    public static IReadOnlyList<ShaderEditorDiagnostic> ParseCompilerDiagnostics(string compilerOutput)
    {
        if (string.IsNullOrWhiteSpace(compilerOutput))
            return [];

        string normalized = compilerOutput.Replace("Shader compilation failed:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        string[] lines = normalized.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<ShaderEditorDiagnostic> diagnostics = [];

        foreach (string line in lines)
        {
            if (TryParseDiagnosticLine(line, out ShaderEditorDiagnostic? diagnostic) && diagnostic is not null)
            {
                diagnostics.Add(diagnostic);
                continue;
            }

            if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("warning", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new ShaderEditorDiagnostic(
                    0,
                    0,
                    line.Contains("warning", StringComparison.OrdinalIgnoreCase)
                        ? ShaderEditorDiagnosticSeverity.Warning
                        : ShaderEditorDiagnosticSeverity.Error,
                    line,
                    line));
            }
        }

        return diagnostics;
    }

    public static IReadOnlyList<ShaderEditorCompletionItem> BuildCompletionItems(string source)
    {
        Dictionary<string, ShaderEditorCompletionItem> items = new(StringComparer.Ordinal);

        foreach (string keyword in GlslKeywords)
            items.TryAdd(keyword, new ShaderEditorCompletionItem(keyword, "keyword", "GLSL keyword"));

        foreach (string typeName in GlslTypes)
            items.TryAdd(typeName, new ShaderEditorCompletionItem(typeName, "type", "GLSL type"));

        foreach (string builtin in GlslBuiltins)
            items.TryAdd(builtin, new ShaderEditorCompletionItem(builtin, "builtin", "GLSL builtin"));

        if (string.IsNullOrWhiteSpace(source))
            return [.. items.Values.OrderBy(item => item.Name, StringComparer.Ordinal)];

        foreach (Match match in DeclarationRegex.Matches(source))
        {
            string name = match.Groups["name"].Value;
            string qualifier = match.Groups["qualifier"].Value;
            string typeName = match.Groups["type"].Value;
            items[name] = new ShaderEditorCompletionItem(name, qualifier, typeName);
        }

        foreach (Match match in FunctionRegex.Matches(source))
        {
            string name = match.Groups["name"].Value;
            if (ControlWords.Contains(name))
                continue;

            string typeName = match.Groups["type"].Value;
            items.TryAdd(name, new ShaderEditorCompletionItem(name, "function", $"returns {typeName}"));
        }

        foreach (Match match in LocalRegex.Matches(source))
        {
            string name = match.Groups["name"].Value;
            if (ControlWords.Contains(name))
                continue;

            string typeName = match.Groups["type"].Value;
            items.TryAdd(name, new ShaderEditorCompletionItem(name, "local", typeName));
        }

        return [.. items.Values.OrderBy(item => item.Name, StringComparer.Ordinal)];
    }

    public static ShaderEditorVariantResult GenerateVariant(
        string source,
        string? sourcePath,
        EShaderType shaderType,
        ShaderEditorVariantPreset preset,
        string? customDefine = null,
        string? lockedVariantSource = null)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return new ShaderEditorVariantResult(
                false,
                preset,
                GetVariantDisplayName(preset),
                string.Empty,
                "No shader source to generate from.");
        }

        switch (preset)
        {
            case ShaderEditorVariantPreset.DepthNormalPrePass:
                if (shaderType == EShaderType.Fragment &&
                    ForwardDepthNormalVariantFactory.TryCreateFragmentVariantSource(source, out string depthNormalSource) &&
                    !string.IsNullOrWhiteSpace(depthNormalSource))
                {
                    return new ShaderEditorVariantResult(
                        true,
                        preset,
                        GetVariantDisplayName(preset),
                        depthNormalSource,
                        "Generated a rewritten depth-normal fragment variant.");
                }

                return CreateDefinedVariantResult(source, sourcePath, shaderType, preset, DepthNormalPrePassDefine,
                    "Generated a define-based depth-normal variant.");

            case ShaderEditorVariantPreset.OptimizedLocked:
                if (string.IsNullOrWhiteSpace(lockedVariantSource))
                {
                    return new ShaderEditorVariantResult(
                        false,
                        preset,
                        GetVariantDisplayName(preset),
                        string.Empty,
                        "No optimized locked variant is available yet.");
                }

                return new ShaderEditorVariantResult(
                    true,
                    preset,
                    GetVariantDisplayName(preset),
                    lockedVariantSource,
                    "Generated an optimized locked variant.");

            case ShaderEditorVariantPreset.CustomDefine:
                string define = NormalizeDefineName(customDefine);
                if (string.IsNullOrWhiteSpace(define))
                {
                    return new ShaderEditorVariantResult(
                        false,
                        preset,
                        GetVariantDisplayName(preset),
                        string.Empty,
                        "Enter a preprocessor define name.");
                }

                return CreateDefinedVariantResult(source, sourcePath, shaderType, preset, define,
                    $"Generated a define-based variant for {define}.");

            case ShaderEditorVariantPreset.ShadowCaster:
                return CreateDefinedVariantResult(source, sourcePath, shaderType, preset, ShadowCasterDefine,
                    "Generated a shadow-caster variant.");

            case ShaderEditorVariantPreset.WeightedBlendedOit:
                return CreateDefinedVariantResult(source, sourcePath, shaderType, preset, WeightedBlendedOitDefine,
                    "Generated a weighted blended OIT variant.");

            case ShaderEditorVariantPreset.PerPixelLinkedList:
                return CreateDefinedVariantResult(source, sourcePath, shaderType, preset, PerPixelLinkedListDefine,
                    "Generated a per-pixel linked-list OIT variant.");

            case ShaderEditorVariantPreset.DepthPeeling:
                return CreateDefinedVariantResult(source, sourcePath, shaderType, preset, DepthPeelingDefine,
                    "Generated a depth-peeling variant.");

            default:
                return new ShaderEditorVariantResult(false, preset, GetVariantDisplayName(preset), string.Empty, "Unknown variant preset.");
        }
    }

    public static IReadOnlyList<ShaderEditorShaderPreset> GetShaderPresets()
        => ShaderPresets;

    public static XRShader LoadShaderPreset(ShaderEditorShaderPreset preset)
        => ShaderHelper.LoadEngineShader(preset.RelativePath, preset.ShaderType);

    public static TextFile CloneShaderSourceForEditing(XRShader sourceShader)
    {
        TextFile? source = sourceShader.Source;
        TextFile clone = new()
        {
            Text = source?.Text ?? string.Empty,
            FilePath = source?.FilePath
        };

        if (source is not null)
            clone.Encoding = source.Encoding;

        return clone;
    }

    public static void ApplyShaderPreset(XRShader target, ShaderEditorShaderPreset preset)
    {
        XRShader presetShader = LoadShaderPreset(preset);
        target.Type = presetShader.Type;
        target.Source = CloneShaderSourceForEditing(presetShader);
    }

    public static IReadOnlyList<string> GetEngineSnippetNames()
        => [.. ShaderSnippets.GetAllNames().OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)];

    public static ShaderEditorIncludePreview PreviewEngineSnippet(string snippetName)
    {
        if (string.IsNullOrWhiteSpace(snippetName))
        {
            return new ShaderEditorIncludePreview(
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "Select an engine snippet.");
        }

        string trimmed = snippetName.Trim();
        string directive = BuildSnippetDirective(trimmed);
        if (ShaderSnippets.TryGet(trimmed, out string? source) && source is not null)
        {
            return new ShaderEditorIncludePreview(
                true,
                trimmed,
                directive,
                trimmed,
                source,
                "Snippet preview ready.");
        }

        return new ShaderEditorIncludePreview(
            false,
            trimmed,
            directive,
            trimmed,
            string.Empty,
            $"Snippet '{trimmed}' was not found.");
    }

    public static IReadOnlyList<ShaderEditorIncludeCandidate> GetRelativeIncludeCandidates(string? sourcePath, int maxCount = 512)
    {
        Dictionary<string, ShaderEditorIncludeCandidate> candidates = new(StringComparer.OrdinalIgnoreCase);

        string? sourceDirectory = GetSourceDirectory(sourcePath);
        if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory))
            AddIncludeCandidates(candidates, sourceDirectory, sourceDirectory, maxCount);

        foreach (string shaderRoot in GetShaderSearchRoots(sourcePath))
            AddIncludeCandidates(candidates, shaderRoot, shaderRoot, maxCount);

        return [.. candidates.Values
            .OrderBy(static candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxCount))];
    }

    public static ShaderEditorIncludePreview PreviewRelativeInclude(string includePath, string? sourcePath)
    {
        string directive = BuildIncludeDirective(includePath, absolute: false);
        if (!TryResolveIncludePath(includePath, sourcePath, out string? resolvedPath) || resolvedPath is null)
        {
            return new ShaderEditorIncludePreview(
                false,
                includePath ?? string.Empty,
                directive,
                string.Empty,
                string.Empty,
                "Include file was not found in the shader search paths.");
        }

        return PreviewIncludeFile(includePath, directive, resolvedPath);
    }

    public static ShaderEditorIncludePreview PreviewAbsoluteInclude(string absolutePath)
    {
        string directive = BuildIncludeDirective(absolutePath, absolute: true);
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return new ShaderEditorIncludePreview(
                false,
                string.Empty,
                directive,
                string.Empty,
                string.Empty,
                "Enter an absolute include path.");
        }

        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(absolutePath);
        }
        catch (Exception ex)
        {
            return new ShaderEditorIncludePreview(
                false,
                absolutePath,
                directive,
                string.Empty,
                string.Empty,
                ex.Message);
        }

        if (!File.Exists(resolvedPath))
        {
            return new ShaderEditorIncludePreview(
                false,
                absolutePath,
                directive,
                resolvedPath,
                string.Empty,
                "Absolute include file does not exist.");
        }

        return PreviewIncludeFile(Path.GetFileName(resolvedPath), directive, resolvedPath);
    }

    public static string BuildSnippetDirective(string snippetName)
        => string.IsNullOrWhiteSpace(snippetName)
            ? string.Empty
            : $"#pragma snippet \"{snippetName.Trim()}\"";

    public static string BuildIncludeDirective(string includePath, bool absolute)
    {
        if (string.IsNullOrWhiteSpace(includePath))
            return string.Empty;

        string trimmed = includePath.Trim();
        string normalized;
        try
        {
            normalized = absolute
                ? Path.GetFullPath(trimmed).Replace('\\', '/')
                : trimmed.Replace('\\', '/');
        }
        catch
        {
            normalized = trimmed.Replace('\\', '/');
        }

        return $"#include \"{normalized}\"";
    }

    public static string AppendDirective(string source, string directive)
    {
        if (string.IsNullOrWhiteSpace(directive))
            return source ?? string.Empty;

        source ??= string.Empty;
        if (source.Length == 0)
            return directive + Environment.NewLine;

        string separator = source.EndsWith('\n') ? string.Empty : Environment.NewLine;
        return source + separator + directive + Environment.NewLine;
    }

    private static void AddIncludeCandidates(
        Dictionary<string, ShaderEditorIncludeCandidate> candidates,
        string root,
        string displayRoot,
        int maxCount)
    {
        if (candidates.Count >= maxCount || !Directory.Exists(root))
            return;

        string normalizedRoot = Path.GetFullPath(root);
        string normalizedDisplayRoot = Path.GetFullPath(displayRoot);
        foreach (string filePath in Directory.EnumerateFiles(normalizedRoot, "*", SearchOption.AllDirectories))
        {
            if (candidates.Count >= maxCount)
                return;

            if (!IsShaderPreviewFile(filePath))
                continue;

            string fullPath = Path.GetFullPath(filePath);
            string includePath = Path.GetRelativePath(normalizedDisplayRoot, fullPath).Replace('\\', '/');
            if (includePath.StartsWith("..", StringComparison.Ordinal))
                includePath = Path.GetFileName(fullPath);

            candidates.TryAdd(includePath, new ShaderEditorIncludeCandidate(includePath, includePath, fullPath));
        }
    }

    private static ShaderEditorIncludePreview PreviewIncludeFile(string displayName, string directive, string resolvedPath)
    {
        try
        {
            return new ShaderEditorIncludePreview(
                true,
                displayName,
                directive,
                Path.GetFullPath(resolvedPath),
                File.ReadAllText(resolvedPath),
                "Include preview ready.");
        }
        catch (Exception ex)
        {
            return new ShaderEditorIncludePreview(
                false,
                displayName,
                directive,
                resolvedPath,
                string.Empty,
                ex.Message);
        }
    }

    private static bool TryResolveIncludePath(string includePath, string? sourcePath, out string? resolvedPath)
    {
        resolvedPath = null;
        if (string.IsNullOrWhiteSpace(includePath))
            return false;

        string trimmed = includePath.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            string absolutePath = Path.GetFullPath(trimmed);
            if (File.Exists(absolutePath))
            {
                resolvedPath = absolutePath;
                return true;
            }
        }

        string? sourceDirectory = GetSourceDirectory(sourcePath);
        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            string fromSource = Path.GetFullPath(Path.Combine(sourceDirectory, trimmed));
            if (File.Exists(fromSource))
            {
                resolvedPath = fromSource;
                return true;
            }
        }

        foreach (string root in GetShaderSearchRoots(sourcePath))
        {
            string fromRoot = Path.GetFullPath(Path.Combine(root, trimmed));
            if (File.Exists(fromRoot))
            {
                resolvedPath = fromRoot;
                return true;
            }
        }

        if (trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return false;

        foreach (string root in GetShaderSearchRoots(sourcePath))
        {
            foreach (string filePath in Directory.EnumerateFiles(root, trimmed, SearchOption.AllDirectories))
            {
                resolvedPath = Path.GetFullPath(filePath);
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> GetShaderSearchRoots(string? sourcePath)
    {
        List<string> roots = [];
        AddShaderRoot(roots, FindShaderRoot(sourcePath));

        string? sourceDirectory = GetSourceDirectory(sourcePath);
        AddShaderRoot(roots, FindShaderRoot(sourceDirectory));

        AddShaderRoot(roots, FindShaderRoot(Environment.CurrentDirectory));
        AddShaderRoot(roots, FindShaderRoot(AppContext.BaseDirectory));

        if (!string.IsNullOrWhiteSpace(Engine.Assets?.EngineAssetsPath))
            AddShaderRoot(roots, Path.Combine(Engine.Assets.EngineAssetsPath, "Shaders"));
        if (!string.IsNullOrWhiteSpace(Engine.Assets?.GameAssetsPath))
            AddShaderRoot(roots, Path.Combine(Engine.Assets.GameAssetsPath, "Shaders"));

        return roots;
    }

    private static void AddShaderRoot(List<string> roots, string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;

        string normalized = Path.GetFullPath(root);
        if (!roots.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
            roots.Add(normalized);
    }

    private static string? GetSourceDirectory(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        try
        {
            return Directory.Exists(sourcePath)
                ? Path.GetFullPath(sourcePath)
                : Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        }
        catch
        {
            return null;
        }
    }

    private static string? FindShaderRoot(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
            return null;

        DirectoryInfo? directory;
        try
        {
            directory = Directory.Exists(startPath)
                ? new DirectoryInfo(startPath)
                : new FileInfo(startPath).Directory;
        }
        catch
        {
            return null;
        }

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

    private static bool IsShaderPreviewFile(string path)
        => ShaderPreviewExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static string GetVariantDisplayName(ShaderEditorVariantPreset preset)
        => preset switch
        {
            ShaderEditorVariantPreset.ShadowCaster => "Shadow Caster",
            ShaderEditorVariantPreset.DepthNormalPrePass => "Depth-Normal Pre-Pass",
            ShaderEditorVariantPreset.WeightedBlendedOit => "Weighted Blended OIT",
            ShaderEditorVariantPreset.PerPixelLinkedList => "Per-Pixel Linked List OIT",
            ShaderEditorVariantPreset.DepthPeeling => "Depth Peeling",
            ShaderEditorVariantPreset.OptimizedLocked => "Optimized Locked",
            ShaderEditorVariantPreset.CustomDefine => "Custom Define",
            _ => preset.ToString()
        };

    public static string SuggestVariantPath(string? sourcePath, string variantName, string extensionOverride = "")
    {
        string normalizedVariant = NormalizeFileNameToken(variantName);
        string directory = !string.IsNullOrWhiteSpace(sourcePath)
            ? Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory
            : Environment.CurrentDirectory;
        string sourceName = !string.IsNullOrWhiteSpace(sourcePath)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : "Shader";
        string extension = !string.IsNullOrWhiteSpace(extensionOverride)
            ? extensionOverride
            : !string.IsNullOrWhiteSpace(sourcePath)
                ? Path.GetExtension(sourcePath)
                : ".glsl";

        if (!extension.StartsWith(".", StringComparison.Ordinal))
            extension = "." + extension;

        return Path.Combine(directory, $"{sourceName}_{normalizedVariant}{extension}");
    }

    public static ShaderEditorCrossCompileResult CrossCompile(
        string source,
        string? sourcePath,
        EShaderType shaderType,
        ShaderSourceLanguage sourceLanguage,
        string entryPoint,
        bool generateHlsl,
        bool generateGlsl)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            byte[] spirv = ShaderCrossCompiler.CompileToSpirv(
                source,
                shaderType,
                sourceLanguage,
                string.IsNullOrWhiteSpace(sourcePath) ? "ShaderEditor.glsl" : Path.GetFileName(sourcePath),
                string.IsNullOrWhiteSpace(entryPoint) ? "main" : entryPoint.Trim());

            string hlsl = generateHlsl ? CrossCompileSpirv(spirv, spvc_backend.Hlsl, ConfigureHlslOptions) : string.Empty;
            string glsl = generateGlsl ? CrossCompileSpirv(spirv, spvc_backend.Glsl, ConfigureGlslOptions) : string.Empty;

            stopwatch.Stop();
            return new ShaderEditorCrossCompileResult(
                true,
                source,
                spirv,
                hlsl,
                glsl,
                stopwatch.Elapsed,
                DateTimeOffset.Now,
                "Cross-compile complete.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ShaderEditorCrossCompileResult(
                false,
                source,
                [],
                string.Empty,
                string.Empty,
                stopwatch.Elapsed,
                DateTimeOffset.Now,
                ex.Message);
        }
    }

    public static string GetTrailingIdentifierToken(string source)
    {
        if (string.IsNullOrEmpty(source))
            return string.Empty;

        int index = source.Length - 1;
        while (index >= 0 && char.IsWhiteSpace(source[index]))
            index--;

        int end = index + 1;
        while (index >= 0 && (char.IsLetterOrDigit(source[index]) || source[index] == '_'))
            index--;

        return index + 1 < end ? source[(index + 1)..end] : string.Empty;
    }

    public static string InsertCompletionAtEnd(string source, string prefix, string completion)
    {
        if (string.IsNullOrEmpty(completion))
            return source;

        source ??= string.Empty;
        prefix ??= string.Empty;

        if (!string.IsNullOrEmpty(prefix) && source.EndsWith(prefix, StringComparison.Ordinal))
            return source[..^prefix.Length] + completion;

        return source + completion;
    }

    public static string BuildInstrumentedPreviewSource(
        string source,
        int oneBasedLine,
        string expression,
        string outputVariable)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(expression))
            return source ?? string.Empty;

        outputVariable = string.IsNullOrWhiteSpace(outputVariable) ? "FragColor" : outputVariable.Trim();
        string[] lines = SplitLinesPreserveEmpty(source);
        int targetIndex = Math.Clamp(oneBasedLine, 1, Math.Max(1, lines.Length)) - 1;

        var builder = new StringBuilder(source.Length + 1024);
        bool helperInserted = false;

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            builder.AppendLine(lines[lineIndex]);

            if (!helperInserted && IsGoodHelperInsertionLine(lines, lineIndex))
            {
                AppendPreviewHelpers(builder);
                helperInserted = true;
            }

            if (lineIndex == targetIndex)
            {
                string indent = GetIndent(lines[lineIndex]);
                builder.Append(indent)
                    .Append(outputVariable)
                    .Append(" = XreShaderEditorPreviewColor(")
                    .Append(expression.Trim())
                    .AppendLine(");");
                builder.Append(indent).AppendLine("return;");
            }
        }

        if (!helperInserted)
            AppendPreviewHelpers(builder);

        return builder.ToString();
    }

    public static string ExtractOpenAiResponseText(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("output_text", out JsonElement outputText) && outputText.ValueKind == JsonValueKind.String)
                return outputText.GetString() ?? string.Empty;

            if (root.TryGetProperty("output", out JsonElement outputArray) && outputArray.ValueKind == JsonValueKind.Array)
            {
                var builder = new StringBuilder();
                foreach (JsonElement item in outputArray.EnumerateArray())
                {
                    if (!item.TryGetProperty("content", out JsonElement contentArray) || contentArray.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (JsonElement block in contentArray.EnumerateArray())
                    {
                        if (block.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                            builder.Append(text.GetString());
                    }
                }

                if (builder.Length > 0)
                    return builder.ToString();
            }
        }
        catch (JsonException)
        {
            return body;
        }

        return body;
    }

    private static ShaderEditorVariantResult CreateDefinedVariantResult(
        string source,
        string? sourcePath,
        EShaderType shaderType,
        ShaderEditorVariantPreset preset,
        string defineName,
        string successMessage)
    {
        string variantSource = CreateDefinedVariantSource(source, sourcePath, shaderType, defineName);
        return new ShaderEditorVariantResult(
            true,
            preset,
            GetVariantDisplayName(preset),
            variantSource,
            successMessage);
    }

    private static string CreateDefinedVariantSource(string source, string? sourcePath, EShaderType shaderType, string defineName)
    {
        string normalizedDefine = NormalizeDefineName(defineName);
        if (string.IsNullOrWhiteSpace(normalizedDefine))
            return source;

        if (ContainsDefine(source, normalizedDefine))
            return source;

        var shader = new XRShader(shaderType)
        {
            Source = new TextFile { Text = source, FilePath = sourcePath }
        };

        XRShader? variant = ShaderHelper.CreateDefinedShaderVariant(shader, normalizedDefine);
        return variant?.Source?.Text ?? InjectDefineAfterVersion(source, normalizedDefine);
    }

    private static string NormalizeDefineName(string? defineName)
    {
        if (string.IsNullOrWhiteSpace(defineName))
            return string.Empty;

        string trimmed = defineName.Trim();
        if (trimmed.StartsWith("#define", StringComparison.Ordinal))
            trimmed = trimmed["#define".Length..].Trim();

        int whitespaceIndex = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        return whitespaceIndex > 0 ? trimmed[..whitespaceIndex] : trimmed;
    }

    private static bool ContainsDefine(string source, string defineName)
        => Regex.IsMatch(
            source,
            @"^\s*#\s*define\s+" + Regex.Escape(defineName) + @"(?:\s|$)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static string InjectDefineAfterVersion(string source, string defineName)
    {
        int searchIndex = 0;
        while (searchIndex < source.Length)
        {
            int lineEnd = source.IndexOf('\n', searchIndex);
            int lineLength = (lineEnd >= 0 ? lineEnd : source.Length) - searchIndex;
            string line = source.Substring(searchIndex, lineLength);
            if (line.TrimStart(' ', '\t', '\r').StartsWith("#version", StringComparison.Ordinal))
            {
                int insertionIndex = lineEnd >= 0 ? lineEnd + 1 : source.Length;
                string header = source[..insertionIndex];
                string body = insertionIndex < source.Length
                    ? source[insertionIndex..].TrimStart('\r', '\n')
                    : string.Empty;

                return string.IsNullOrEmpty(body)
                    ? header + Environment.NewLine + $"#define {defineName}" + Environment.NewLine
                    : header + Environment.NewLine + $"#define {defineName}" + Environment.NewLine + Environment.NewLine + body;
            }

            if (lineEnd < 0)
                break;

            searchIndex = lineEnd + 1;
        }

        return $"#define {defineName}{Environment.NewLine}{source}";
    }

    private static string NormalizeFileNameToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "variant";

        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        return builder.ToString().Trim('_') is { Length: > 0 } token ? token : "variant";
    }

    private static unsafe string CrossCompileSpirv(byte[] spirvBytes, spvc_backend backend, Action<spvc_compiler_options>? configureOptions)
    {
        if (spirvBytes.Length % 4 != 0)
            throw new InvalidOperationException("SPIR-V bytecode length is not aligned.");

        spvc_context context;
        EnsureSuccess(SPIRV.spvc_context_create(&context), context, "Failed to create SPIRV-Cross context.");

        try
        {
            spvc_parsed_ir parsedIr;
            fixed (byte* bytes = spirvBytes)
            {
                var words = (SpvId*)bytes;
                uint wordCount = (uint)(spirvBytes.Length / 4);
                EnsureSuccess(SPIRV.spvc_context_parse_spirv(context, words, wordCount, &parsedIr), context, "Failed to parse SPIR-V module.");
            }

            spvc_compiler compiler;
            EnsureSuccess(SPIRV.spvc_context_create_compiler(context, backend, parsedIr, spvc_capture_mode.TakeOwnership, &compiler), context,
                "Failed to create SPIRV-Cross compiler.");

            spvc_compiler_options options;
            EnsureSuccess(SPIRV.spvc_compiler_create_compiler_options(compiler, &options), context, "Failed to create SPIRV-Cross compiler options.");
            configureOptions?.Invoke(options);
            EnsureSuccess(SPIRV.spvc_compiler_install_compiler_options(compiler, options), context, "Failed to install SPIRV-Cross options.");

            byte* sourcePtr = null;
            EnsureSuccess(SPIRV.spvc_compiler_compile(compiler, (byte*)&sourcePtr), context, "SPIRV-Cross compilation failed.");

            if (sourcePtr == null)
                throw new InvalidOperationException("SPIRV-Cross produced empty output.");

            return GetString(sourcePtr);
        }
        finally
        {
            SPIRV.spvc_context_destroy(context);
        }
    }

    private static void ConfigureHlslOptions(spvc_compiler_options options)
    {
        SPIRV.spvc_compiler_options_set_uint(options, spvc_compiler_option.HlslShaderModel, 50);
        SPIRV.spvc_compiler_options_set_bool(options, spvc_compiler_option.HlslPointSizeCompat, true);
    }

    private static void ConfigureGlslOptions(spvc_compiler_options options)
    {
        SPIRV.spvc_compiler_options_set_uint(options, spvc_compiler_option.GlslVersion, 450);
        SPIRV.spvc_compiler_options_set_bool(options, spvc_compiler_option.GlslVulkanSemantics, true);
    }

    private static unsafe void EnsureSuccess(spvc_result result, spvc_context context, string message)
    {
        if (result == spvc_result.SPVC_SUCCESS)
            return;

        string detail = GetString(SPIRV.spvc_context_get_last_error_string(context));
        throw new InvalidOperationException($"{message} {detail}".Trim());
    }

    private static unsafe string GetString(byte* ptr)
    {
        if (ptr == null)
            return string.Empty;

        int length = 0;
        while (length < 1_000_000 && ptr[length] != 0)
            length++;

        return Encoding.UTF8.GetString(ptr, length);
    }

    private static bool TryParseDiagnosticLine(string line, out ShaderEditorDiagnostic? diagnostic)
    {
        Match match = ClangStyleDiagnosticRegex.Match(line);
        if (!match.Success)
            match = GlslangDiagnosticRegex.Match(line);

        if (!match.Success)
        {
            diagnostic = null;
            return false;
        }

        int lineNumber = TryParsePositiveInt(match.Groups["line"].Value);
        int column = TryParsePositiveInt(match.Groups["column"].Value);
        string severityText = match.Groups["severity"].Value;
        string message = match.Groups["message"].Value.Trim();
        ShaderEditorDiagnosticSeverity severity = severityText.StartsWith("warn", StringComparison.OrdinalIgnoreCase)
            ? ShaderEditorDiagnosticSeverity.Warning
            : ShaderEditorDiagnosticSeverity.Error;

        diagnostic = new ShaderEditorDiagnostic(lineNumber, column, severity, message, line);
        return true;
    }

    private static int TryParsePositiveInt(string text)
        => int.TryParse(text, out int value) && value > 0 ? value : 0;

    private static string[] SplitLinesPreserveEmpty(string source)
        => source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static bool IsGoodHelperInsertionLine(IReadOnlyList<string> lines, int lineIndex)
    {
        string line = lines[lineIndex].TrimStart();
        if (line.StartsWith("#version", StringComparison.Ordinal))
            return true;

        if (lineIndex == 0 && !line.StartsWith("#", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static string GetIndent(string line)
    {
        int length = 0;
        while (length < line.Length && char.IsWhiteSpace(line[length]))
            length++;
        return length == 0 ? string.Empty : line[..length];
    }

    private static void AppendPreviewHelpers(StringBuilder builder)
    {
        builder.AppendLine("#ifndef XRE_SHADER_EDITOR_PREVIEW_HELPERS");
        builder.AppendLine("#define XRE_SHADER_EDITOR_PREVIEW_HELPERS");
        builder.AppendLine("vec4 XreShaderEditorPreviewColor(float value) { return vec4(vec3(value), 1.0); }");
        builder.AppendLine("vec4 XreShaderEditorPreviewColor(double value) { return vec4(vec3(float(value)), 1.0); }");
        builder.AppendLine("vec4 XreShaderEditorPreviewColor(int value) { return vec4(vec3(float(value)), 1.0); }");
        builder.AppendLine("vec4 XreShaderEditorPreviewColor(uint value) { return vec4(vec3(float(value)), 1.0); }");
        builder.AppendLine("vec4 XreShaderEditorPreviewColor(bool value) { return value ? vec4(0.2, 1.0, 0.2, 1.0) : vec4(1.0, 0.2, 0.2, 1.0); }");
        builder.AppendLine("vec4 XreShaderEditorPreviewColor(vec2 value) { return vec4(value, 0.0, 1.0); }");
        builder.AppendLine("vec4 XreShaderEditorPreviewColor(vec3 value) { return vec4(value, 1.0); }");
        builder.AppendLine("vec4 XreShaderEditorPreviewColor(vec4 value) { return value; }");
        builder.AppendLine("#endif");
    }
}
