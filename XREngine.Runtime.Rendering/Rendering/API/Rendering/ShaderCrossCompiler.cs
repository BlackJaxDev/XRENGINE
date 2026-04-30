using System;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;

namespace XREngine.Rendering;

public enum ShaderSourceLanguage
{
    Glsl,
    Hlsl
}

public static class ShaderCrossCompiler
{
    private static readonly Shaderc ShadercApi = Shaderc.GetApi();

    public static unsafe byte[] CompileToSpirv(string source, EShaderType shaderType, ShaderSourceLanguage sourceLanguage, string? name = null, string entryPoint = "main")
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Shader source is empty.", nameof(source));
        if (string.IsNullOrWhiteSpace(entryPoint))
            throw new ArgumentException("Entry point is required.", nameof(entryPoint));

        Compiler* compiler = ShadercApi.CompilerInitialize();
        if (compiler is null)
            throw new InvalidOperationException("Failed to initialize the shaderc compiler instance.");

        CompileOptions* options = ShadercApi.CompileOptionsInitialize();
        if (options is null)
        {
            ShadercApi.CompilerRelease(compiler);
            throw new InvalidOperationException("Failed to allocate shaderc compile options.");
        }

        ShadercApi.CompileOptionsSetSourceLanguage(options, sourceLanguage == ShaderSourceLanguage.Hlsl ? SourceLanguage.Hlsl : SourceLanguage.Glsl);
        ShadercApi.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);

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

    public static byte[] CompileGlslToSpirv(string glslSource, EShaderType shaderType, string? name = null, string entryPoint = "main")
        => CompileToSpirv(glslSource, shaderType, ShaderSourceLanguage.Glsl, name, entryPoint);

    public static byte[] CompileHlslToSpirv(string hlslSource, EShaderType shaderType, string? name = null, string entryPoint = "main")
        => CompileToSpirv(hlslSource, shaderType, ShaderSourceLanguage.Hlsl, name, entryPoint);

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
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
}
