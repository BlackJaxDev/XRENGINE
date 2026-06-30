using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Rendering.Shaders;

namespace XREngine.Rendering.Vulkan;

internal static class VulkanShaderCompiler
{
    private static readonly Shaderc ShadercApi = Shaderc.GetApi();
    private static readonly Regex OvrMultiviewExtensionRegex = new(
        @"^\s*#\s*extension\s+GL_OVR_multiview2\s*:\s*(?<behavior>\w+)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex OvrMultiviewBaseExtensionRegex = new(
        @"^\s*#\s*extension\s+GL_OVR_multiview\s*:\s*(?<behavior>\w+)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex OvrViewIdBuiltinRegex = new(
        @"\bgl_ViewID_OVR\b",
        RegexOptions.Compiled);
    private static readonly Regex NvStereoExtensionRegex = new(
        @"^\s*#\s*extension\s+GL_NV_(?:stereo_view_rendering|viewport_array2)\s*:\s*\w+\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex VersionDirectiveRegex = new(
        @"^\s*#\s*version\b[^\r\n]*(?:\r?\n)?",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex ReservedBuiltinDeclarationRegex = new(
        @"^\s*(?:layout\s*\([^)]*\)\s*)?(?:in|out|uniform|attribute|varying)\s+(?:highp|mediump|lowp\s+)?(?:[A-Za-z_]\w*\s+)*gl_[A-Za-z_]\w*(?:\s*\[[^\]]*\])?\s*;\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex GlPerVertexBlockRegex = new(
        @"(?ms)^\s*(?:layout\s*\([^)]*\)\s*)?(?:in|out)\s+gl_PerVertex\s*\{(?<body>.*?)\}\s*(?:(?:gl_in|gl_out)\s*\[\s*\])?\s*;\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex NvSecondaryPositionAssignRegex = new(
        @"^\s*gl_SecondaryPositionNV\s*=\s*.*;\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex NvViewportMaskAssignRegex = new(
        @"^\s*gl_(?:SecondaryViewportMaskNV|ViewportMask)\s*\[[^\]]+\]\s*=\s*.*;\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex NvSecondaryViewOffsetLayerDeclarationRegex = new(
        @"^\s*layout\s*\([^)]*secondary_view_offset[^)]*\)\s*out\s+[^;]*gl_Layer\s*;\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex NvLayerAssignmentRegex = new(
        @"^\s*gl_Layer\s*=\s*.*;\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex ShaderCompileLineRegex = new(
        @"(?m)(?<shader>[A-Za-z0-9_./\\-]+):(?<line>\d+):",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public sealed record PreparedSource(
        string EntryPoint,
        string OriginalSource,
        string OptimizedSource,
        string RewrittenSource,
        AutoUniformBlockInfo? AutoUniformBlock);

    public static unsafe byte[] Compile(
        XRShader shader,
        out string entryPoint,
        out AutoUniformBlockInfo? autoUniformBlock,
        out string? rewrittenSource)
        => Compile(shader, RuntimeEngine.Rendering.ShouldUseVulkanShaderClipDepthRemap, out entryPoint, out autoUniformBlock, out rewrittenSource);

    public static unsafe byte[] Compile(
        XRShader shader,
        bool useVulkanClipDepthRemap,
        out string entryPoint,
        out AutoUniformBlockInfo? autoUniformBlock,
        out string? rewrittenSource)
    {
        PreparedSource prepared = Prepare(shader, useVulkanClipDepthRemap);
        entryPoint = prepared.EntryPoint;
        autoUniformBlock = prepared.AutoUniformBlock;
        rewrittenSource = prepared.RewrittenSource;
        return CompilePrepared(shader, prepared);
    }

    public static PreparedSource Prepare(XRShader shader, bool useVulkanClipDepthRemap)
        => Prepare(shader, useVulkanClipDepthRemap, null);

    public static PreparedSource Prepare(
        XRShader shader,
        bool useVulkanClipDepthRemap,
        VulkanTransformFeedbackCompilePlan? transformFeedbackPlan)
    {
        string entryPoint = "main";
        string source = shader.Source?.Text ?? string.Empty;
        source = ShaderSourcePreprocessor.ResolveSource(source, shader.Source?.FilePath, annotateIncludes: true);
        source = ResolvedShaderSourceOptimizer.Optimize(
            source,
            new ResolvedShaderSourceOptimizationOptions
            {
                DiagnosticLabel = shader.Name ?? "UnnamedShader",
            }).Source;
        source = NormalizeLegacyStereoForVulkan(source, shader.Name ?? "UnnamedShader");
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' does not contain GLSL source code.");

        AutoUniformRewriteResult rewrite = VulkanShaderAutoUniforms.Rewrite(source, shader.Type, useVulkanClipDepthRemap);
        string transformFeedbackSource = VulkanShaderTransformFeedback.Rewrite(rewrite.Source, shader.Type, transformFeedbackPlan);
        string rewrittenSource = InjectVulkanBackendDefine(transformFeedbackSource);
        return new PreparedSource(entryPoint, shader.Source?.Text ?? string.Empty, source, rewrittenSource, rewrite.BlockInfo);
    }

    public static unsafe byte[] CompilePrepared(XRShader shader, PreparedSource prepared)
    {
        Compiler* compiler = ShadercApi.CompilerInitialize();
        if (compiler is null)
            throw new InvalidOperationException("Failed to initialize the shaderc compiler instance.");

        CompileOptions* options = ShadercApi.CompileOptionsInitialize();
        if (options is null)
        {
            ShadercApi.CompilerRelease(compiler);
            throw new InvalidOperationException("Failed to allocate shaderc compile options.");
        }

        ShadercApi.CompileOptionsSetSourceLanguage(options, SourceLanguage.Glsl);
        ShadercApi.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);

        byte[] sourceBytes = Encoding.UTF8.GetBytes(prepared.RewrittenSource);
        byte[] nameBytes = GetNullTerminatedUtf8(shader.Name ?? $"Shader_{shader.GetHashCode():X8}");
        byte[] entryPointBytes = GetNullTerminatedUtf8(prepared.EntryPoint);

        CompilationResult* result;
        fixed (byte* sourcePtr = sourceBytes)
        fixed (byte* namePtr = nameBytes)
        fixed (byte* entryPtr = entryPointBytes)
        {
            result = ShadercApi.CompileIntoSpv(
                compiler,
                sourcePtr,
                (nuint)sourceBytes.Length,
                ToShaderKind(shader.Type),
                namePtr,
                entryPtr,
                options);
        }

        try
        {
            if (result is null)
                throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' failed to compile due to an unknown error.");

            CompilationStatus status = ShadercApi.ResultGetCompilationStatus(result);
            if (status != CompilationStatus.Success)
            {
                string message = SilkMarshal.PtrToString((nint)ShadercApi.ResultGetErrorMessage(result)) ?? "Unknown error";
                string diagnostics = BuildCompileFailureDiagnostics(shader, prepared.OptimizedSource, prepared.RewrittenSource, prepared.AutoUniformBlock, message);
                Debug.VulkanWarningEvery(
                    $"Vulkan.ShaderCompileDiagnostics.{shader.Name ?? "UnnamedShader"}.{shader.Type}",
                    TimeSpan.FromSeconds(2),
                    diagnostics);
                bool includePreview = XREngine.Rendering.RenderDiagnosticsFlags.VkDumpShaderOnError;

                if (includePreview)
                {
                    string preview = BuildSourcePreview(prepared.RewrittenSource, 120);
                    throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' failed to compile: {message}{Environment.NewLine}{preview}");
                }

                throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' failed to compile: {message}");
            }

            nuint length = ShadercApi.ResultGetLength(result);
            if (length == 0)
                throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' produced an empty SPIR-V module.");

            byte[] spirv = new byte[(int)length];
            void* bytesPtr = ShadercApi.ResultGetBytes(result);
            Marshal.Copy((nint)bytesPtr, spirv, 0, spirv.Length);

            ShadercApi.ResultRelease(result);
            result = null;
            return spirv;
        }
        finally
        {
            if (result is not null)
                ShadercApi.ResultRelease(result);

            ShadercApi.CompileOptionsRelease(options);
            ShadercApi.CompilerRelease(compiler);
        }
    }

    public static string BuildArtifactIdentity(
        XRShader shader,
        int shaderConfigVersion,
        bool useVulkanClipDepthRemap,
        string? rewrittenSource)
    {
        StringBuilder builder = new(512);
        builder.AppendLine("Backend=Vulkan");
        builder.AppendLine("XRENGINE_VULKAN=1");
        builder.Append("ShaderType=").Append(shader.Type).Append('\n');
        builder.Append("ShaderName=").Append(shader.Name ?? "UnnamedShader").Append('\n');
        builder.Append("SourcePath=").Append(shader.Source?.FilePath ?? shader.FilePath ?? string.Empty).Append('\n');
        builder.Append("IsGeneratedUberVariant=").Append(shader.IsGeneratedUberVariant).Append('\n');
        builder.Append("GeneratedUberVariantHash=").Append(shader.GeneratedUberVariantHash).Append('\n');
        builder.Append("ShaderConfigVersion=").Append(shaderConfigVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("useVulkanClipDepthRemap=").Append(useVulkanClipDepthRemap ? '1' : '0').Append('\n');
        builder.Append("Optimizer=").Append(ResolvedShaderSourceOptimizer.BuildIdentitySegment()).Append('\n');
        builder.AppendLine("Rewrite=VulkanShaderAutoUniforms+VulkanShaderTransformFeedback+InjectVulkanBackendDefine:v1");

        if (shader.TryGetResolvedShaderSource(out ResolvedShaderSource resolved, annotateIncludes: false, logFailures: false))
        {
            builder.Append("ResolvedSourceIdentity=").Append(resolved.SourceIdentity).Append('\n');
            builder.Append("Defines=").AppendJoin('|', resolved.MacroSummary.Defines).Append('\n');
            builder.Append("Undefines=").AppendJoin('|', resolved.MacroSummary.Undefines).Append('\n');
            builder.Append("Pragmas=").AppendJoin('|', resolved.MacroSummary.Pragmas).Append('\n');
            foreach (ShaderSourceFileDependency dependency in resolved.FileDependencies.OrderBy(static x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("Dependency=")
                    .Append(dependency.Path)
                    .Append('|')
                    .Append(dependency.LastWriteTimeUtcTicks)
                    .Append('|')
                    .Append(dependency.Length)
                    .Append('\n');
            }
        }

        string source = rewrittenSource ?? shader.Source?.Text ?? string.Empty;
        builder.Append("RewrittenSourceHash=")
            .Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))))
            .Append('\n');

        return "VKSHD-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())), 0, 12);
    }

    private static byte[] GetNullTerminatedUtf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Array.Resize(ref bytes, bytes.Length + 1);
        bytes[^1] = 0;
        return bytes;
    }

    private static string InjectVulkanBackendDefine(string source)
    {
        const string defineLine = "#define XRENGINE_VULKAN 1";
        if (source.Contains(defineLine, StringComparison.Ordinal))
            return source;

        Match versionMatch = VersionDirectiveRegex.Match(source);
        int insertionIndex = versionMatch.Success
            ? versionMatch.Index + versionMatch.Length
            : 0;

        while (insertionIndex < source.Length)
        {
            int lineEnd = source.IndexOf('\n', insertionIndex);
            int lineLength = (lineEnd >= 0 ? lineEnd : source.Length) - insertionIndex;
            ReadOnlySpan<char> line = source.AsSpan(insertionIndex, lineLength).TrimStart();
            if (!line.StartsWith("#extension", StringComparison.OrdinalIgnoreCase))
                break;

            insertionIndex = lineEnd >= 0 ? lineEnd + 1 : source.Length;
        }

        return source.Insert(insertionIndex, defineLine + Environment.NewLine);
    }

    private static ShaderKind ToShaderKind(EShaderType type)
        => type switch
        {
            EShaderType.Vertex => ShaderKind.VertexShader,
            EShaderType.Fragment => ShaderKind.FragmentShader,
            EShaderType.Geometry => ShaderKind.GeometryShader,
            EShaderType.TessControl => ShaderKind.TessControlShader,
            EShaderType.TessEvaluation => ShaderKind.TessEvaluationShader,
            EShaderType.Compute => ShaderKind.ComputeShader,
            EShaderType.Task => ShaderKind.TaskShader,
            EShaderType.Mesh => ShaderKind.MeshShader,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };

    private static string BuildSourcePreview(string source, int maxLines)
    {
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int lineCount = Math.Min(lines.Length, Math.Max(1, maxLines));
        StringBuilder builder = new();
        builder.AppendLine("--- Rewritten GLSL preview ---");
        for (int i = 0; i < lineCount; i++)
            builder.AppendLine($"{i + 1,4}: {lines[i]}");

        return builder.ToString();
    }

    private static string BuildCompileFailureDiagnostics(
        XRShader shader,
        string source,
        string rewrittenSource,
        AutoUniformBlockInfo? autoUniformBlock,
        string compileMessage)
    {
        string shaderName = shader.Name ?? "UnnamedShader";
        StringBuilder builder = new();
        builder.AppendLine($"[Vulkan] Shader compile diagnostics for '{shaderName}' ({shader.Type}).");
        builder.AppendLine($"[Vulkan]   File='{shader.Source?.FilePath ?? "<embedded>"}' SourceLines={CountLines(source)} RewrittenLines={CountLines(rewrittenSource)}");
        builder.AppendLine($"[Vulkan]   AutoUniformBlock={DescribeAutoUniformBlock(autoUniformBlock)}");

        IReadOnlyList<string> unclassifiedOpaqueLikeTypes = VulkanShaderAutoUniforms.FindOpaqueLikeTypesMissingClassification(source);
        if (unclassifiedOpaqueLikeTypes.Count > 0)
        {
            builder.AppendLine(
                $"[Vulkan]   OpaqueLikeUniformTypesNotClassified={string.Join(", ", unclassifiedOpaqueLikeTypes)}");
        }

        string compileSummary = ExtractFirstCompileErrorLine(compileMessage);
        if (!string.IsNullOrWhiteSpace(compileSummary))
            builder.AppendLine($"[Vulkan]   FirstError={compileSummary}");

        string errorContext = BuildErrorContextPreview(shaderName, rewrittenSource, compileMessage, surroundingLines: 2, maxErrors: 10);
        builder.Append(!string.IsNullOrWhiteSpace(errorContext)
            ? errorContext
            : BuildSourcePreview(rewrittenSource, 80));

        return builder.ToString().TrimEnd();
    }

    private static string DescribeAutoUniformBlock(AutoUniformBlockInfo? autoUniformBlock)
    {
        if (autoUniformBlock is null)
            return "<none>";

        IReadOnlyList<AutoUniformMember> members = autoUniformBlock.Members;
        int previewCount = Math.Min(members.Count, 8);
        string[] previews = new string[previewCount];
        for (int i = 0; i < previewCount; i++)
        {
            AutoUniformMember member = members[i];
            previews[i] = member.IsArray && member.ArrayLength > 0
                ? $"{member.GlslType} {member.Name}[{member.ArrayLength}]"
                : $"{member.GlslType} {member.Name}";
        }

        string suffix = members.Count > previewCount ? ", ..." : string.Empty;
        return $"name='{autoUniformBlock.BlockName}' set={autoUniformBlock.Set} binding={autoUniformBlock.Binding} size={autoUniformBlock.Size} members={members.Count} [{string.Join(", ", previews)}{suffix}]";
    }

    private static string ExtractFirstCompileErrorLine(string compileMessage)
    {
        using StringReader reader = new(compileMessage);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (!string.IsNullOrWhiteSpace(line))
                return line;
        }

        return string.Empty;
    }

    private static string BuildErrorContextPreview(string shaderName, string source, string compileMessage, int surroundingLines, int maxErrors)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(compileMessage))
            return string.Empty;

        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<int> errorLines = [];
        HashSet<int> seenLines = [];

        foreach (Match match in ShaderCompileLineRegex.Matches(compileMessage))
        {
            if (!match.Success)
                continue;

            if (!int.TryParse(match.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lineNumber))
                continue;

            if (lineNumber < 1 || lineNumber > lines.Length || !seenLines.Add(lineNumber))
                continue;

            errorLines.Add(lineNumber);
            if (errorLines.Count >= maxErrors)
                break;
        }

        if (errorLines.Count == 0)
            return string.Empty;

        StringBuilder builder = new();
        builder.AppendLine("--- Rewritten GLSL error context ---");

        for (int index = 0; index < errorLines.Count; index++)
        {
            int errorLine = errorLines[index];
            int start = Math.Max(1, errorLine - surroundingLines);
            int end = Math.Min(lines.Length, errorLine + surroundingLines);

            if (index > 0)
                builder.AppendLine();

            builder.AppendLine($"[{shaderName}:{errorLine}]");
            for (int lineIndex = start; lineIndex <= end; lineIndex++)
            {
                string marker = lineIndex == errorLine ? ">" : " ";
                builder.AppendLine($"{marker}{lineIndex,4}: {lines[lineIndex - 1]}");
            }
        }

        return builder.ToString();
    }

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

    private static string RewriteLegacyMultiviewExtensionsForVulkan(string source)
    {
        if (string.IsNullOrWhiteSpace(source) ||
            (!source.Contains("GL_OVR_multiview2", StringComparison.OrdinalIgnoreCase) &&
             !source.Contains("GL_OVR_multiview", StringComparison.OrdinalIgnoreCase)))
            return source;

        source = OvrMultiviewExtensionRegex.Replace(
            source,
            static match => $"#extension GL_EXT_multiview : {match.Groups["behavior"].Value}");

        source = OvrMultiviewBaseExtensionRegex.Replace(
            source,
            static match => $"#extension GL_EXT_multiview : {match.Groups["behavior"].Value}");

        source = OvrViewIdBuiltinRegex.Replace(source, "gl_ViewIndex");
        return source;
    }

    private static string NormalizeLegacyStereoForVulkan(string source, string shaderName)
    {
        if (string.IsNullOrWhiteSpace(source))
            return source;

        bool hadOvrMultiview =
            OvrMultiviewExtensionRegex.IsMatch(source) ||
            OvrMultiviewBaseExtensionRegex.IsMatch(source) ||
            OvrViewIdBuiltinRegex.IsMatch(source);
        bool hadNvStereo = NvStereoExtensionRegex.IsMatch(source);

        string beforeMultiviewRewrite = source;
        source = RewriteLegacyMultiviewExtensionsForVulkan(source);
        if (hadOvrMultiview &&
            !string.Equals(beforeMultiviewRewrite, source, StringComparison.Ordinal))
        {
            LogVulkanStereoRewrite(shaderName, "OVR_multiview/gl_ViewID_OVR", "GL_EXT_multiview/gl_ViewIndex");
        }

        if (hadNvStereo)
        {
            string beforeNvRewrite = source;
            source = NvStereoExtensionRegex.Replace(source, string.Empty);
            source = EnsureExtMultiviewDirective(source);
            source = NvSecondaryPositionAssignRegex.Replace(source, string.Empty);
            source = NvViewportMaskAssignRegex.Replace(source, string.Empty);
            source = NvSecondaryViewOffsetLayerDeclarationRegex.Replace(source, string.Empty);
            source = NvLayerAssignmentRegex.Replace(source, string.Empty);
            if (!string.Equals(beforeNvRewrite, source, StringComparison.Ordinal))
                LogVulkanStereoRewrite(shaderName, "NV_stereo_view_rendering", "GL_EXT_multiview-compatible shader");
        }

        source = ReservedBuiltinDeclarationRegex.Replace(source, string.Empty);
        source = RemoveGeneratedGlPerVertexBlocksForVulkan(source);
        ValidateUnsupportedVulkanStereoSemantics(source, shaderName);
        return source;
    }

    private static void LogVulkanStereoRewrite(string shaderName, string sourceSemantics, string targetSemantics)
    {
        Debug.VulkanEvery(
            $"Vulkan.Shader.StereoRewrite.{shaderName}.{sourceSemantics}",
            TimeSpan.FromSeconds(2),
            "[VulkanShaderCompiler] Rewrote stereo shader '{0}' from {1} to {2}.",
            shaderName,
            sourceSemantics,
            targetSemantics);
    }

    private static string RemoveGeneratedGlPerVertexBlocksForVulkan(string source)
    {
        return GlPerVertexBlockRegex.Replace(source, static match =>
        {
            string body = match.Groups["body"].Value;
            bool looksGenerated =
                body.Contains("vec4 gl_Position", StringComparison.Ordinal) &&
                body.Contains("float gl_PointSize", StringComparison.Ordinal) &&
                body.Contains("float gl_ClipDistance[]", StringComparison.Ordinal) &&
                !body.Contains("gl_CullDistance", StringComparison.Ordinal);

            return looksGenerated ? string.Empty : match.Value;
        });
    }

    private static string EnsureExtMultiviewDirective(string source)
    {
        if (source.Contains("#extension GL_EXT_multiview", StringComparison.OrdinalIgnoreCase))
            return source;

        Match versionMatch = VersionDirectiveRegex.Match(source);
        if (!versionMatch.Success)
            return "#extension GL_EXT_multiview : require\n" + source;

        string directive = "#extension GL_EXT_multiview : require\n";
        return source.Insert(versionMatch.Index + versionMatch.Length, directive);
    }

    private static void ValidateUnsupportedVulkanStereoSemantics(string source, string shaderName)
    {
        if (source.Contains("GL_NV_stereo_view_rendering", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("GL_NV_viewport_array2", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("gl_SecondaryPositionNV", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("gl_SecondaryViewportMaskNV", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("gl_ViewportMask", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Shader '{shaderName}' contains NV stereo semantics that cannot be represented in Vulkan GLSL. " +
                "Use multiview-compatible logic (GL_EXT_multiview / gl_ViewIndex) and avoid NV secondary-position/viewport-mask built-ins.");
        }
    }
}
