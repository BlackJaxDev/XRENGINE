using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Shaderc-backed cross-compiler capability supplied by the Vulkan backend module.
/// </summary>
internal sealed class VulkanShaderCrossCompiler : IRuntimeShaderCrossCompiler
{
    private static readonly Shaderc ShadercApi = Shaderc.GetApi();

    public static VulkanShaderCrossCompiler Instance { get; } = new();

    private VulkanShaderCrossCompiler()
    {
    }

    public unsafe byte[] CompileToSpirv(
        string source,
        EShaderType shaderType,
        ShaderSourceLanguage sourceLanguage,
        string? name,
        string entryPoint)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Shader source is empty.", nameof(source));
        if (string.IsNullOrWhiteSpace(entryPoint))
            throw new ArgumentException("Entry point is required.", nameof(entryPoint));
        if (sourceLanguage is not (ShaderSourceLanguage.Glsl or ShaderSourceLanguage.Hlsl))
            throw new NotSupportedException("Slang requires the request-based asynchronous compilation API.");

        Compiler* compiler = ShadercApi.CompilerInitialize();
        if (compiler is null)
            throw new InvalidOperationException("Failed to initialize the shaderc compiler instance.");

        CompileOptions* options = ShadercApi.CompileOptionsInitialize();
        if (options is null)
        {
            ShadercApi.CompilerRelease(compiler);
            throw new InvalidOperationException("Failed to allocate shaderc compile options.");
        }

        ShadercApi.CompileOptionsSetSourceLanguage(
            options,
            sourceLanguage == ShaderSourceLanguage.Hlsl ? SourceLanguage.Hlsl : SourceLanguage.Glsl);
        ShadercApi.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);
        ShadercApi.CompileOptionsSetWarningsAsErrors(options);
        VulkanShaderCompiler.ConfigureTargetEnvironment(options, shaderType, source);

        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        byte[] nameBytes = GetNullTerminatedUtf8(name ?? "Shader");
        byte[] entryPointBytes = GetNullTerminatedUtf8(entryPoint);

        CompilationResult* result;
        fixed (byte* sourcePtr = sourceBytes)
        fixed (byte* namePtr = nameBytes)
        fixed (byte* entryPtr = entryPointBytes)
        {
            result = ShadercApi.CompileIntoSpv(
                compiler,
                sourcePtr,
                (nuint)sourceBytes.Length,
                ToShaderKind(shaderType),
                namePtr,
                entryPtr,
                options);
        }

        try
        {
            if (result is null)
                throw new InvalidOperationException("Shader compilation failed due to an unknown error.");

            CompilationStatus status = ShadercApi.ResultGetCompilationStatus(result);
            if (status != CompilationStatus.Success)
            {
                string message = SilkMarshal.PtrToString((nint)ShadercApi.ResultGetErrorMessage(result)) ?? "Unknown error";
                throw new InvalidOperationException($"Shader compilation failed: {message}");
            }

            nuint length = ShadercApi.ResultGetLength(result);
            if (length == 0)
                throw new InvalidOperationException("Shader compilation produced an empty SPIR-V module.");

            byte[] spirv = new byte[(int)length];
            void* bytesPtr = ShadercApi.ResultGetBytes(result);
            Marshal.Copy((nint)bytesPtr, spirv, 0, spirv.Length);
            VulkanShaderCompiler.ValidateModuleWhenRequested(name ?? "Shader", spirv);

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

    public async Task<ShaderCompileResult> CompileAsync(
        ShaderCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Language == ShaderSourceLanguage.Slang)
            return await SlangVulkanShaderCompiler.CompileAsync(request, cancellationToken).ConfigureAwait(false);

        if (request.Target != ShaderCompileTarget.Vulkan14Spirv16 ||
            request.Includes is { Count: > 0 } || request.Defines is { Count: > 0 } || request.RequiredCapabilities is { Count: > 0 })
            throw new NotSupportedException("The Shaderc request adapter accepts pre-resolved GLSL/HLSL for Vulkan 1.4. Include, define, and capability options require native Slang or explicit source preparation.");

        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch stopwatch = Stopwatch.StartNew();
        byte[] spirv = CompileToSpirv(
            request.Source,
            request.Stage,
            request.Language,
            request.SourcePath,
            request.EntryPoint);
        stopwatch.Stop();
        string sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Source)));
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
            request.Language, request.Target, request.Stage, request.EntryPoint, request.SourcePath,
            request.SemanticSchemaIdentity, sourceHash, Convert.ToHexString(SHA256.HashData(spirv))))));
        return new ShaderCompileResult(
            spirv,
            request.EntryPoint,
            $"shaderc-{identity[..24]}",
            $"Shaderc/{typeof(Shaderc).Assembly.GetName().Version}",
            null,
            [new ShaderCompileDependency(request.SourcePath ?? "<memory>", sourceHash)],
            Array.Empty<ShaderCompileDiagnostic>(),
            LoadedFromCache: false,
            stopwatch.Elapsed);
    }

    private static byte[] GetNullTerminatedUtf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Array.Resize(ref bytes, bytes.Length + 1);
        bytes[^1] = 0;
        return bytes;
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
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };
}
