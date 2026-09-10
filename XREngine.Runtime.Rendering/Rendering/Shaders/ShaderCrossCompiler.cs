namespace XREngine.Rendering;

using XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Backend-neutral entry point for GLSL/HLSL and opt-in native Slang SPIR-V compilation.
/// </summary>
public static class ShaderCrossCompiler
{
    /// <summary>
    /// Compiles an explicit frontend request while retaining its bytecode provenance.
    /// </summary>
    public static Task<ShaderCompileResult> CompileAsync(
        ShaderCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GetCompiler().CompileAsync(request, cancellationToken);
    }

    public static byte[] CompileToSpirv(
        string source,
        EShaderType shaderType,
        ShaderSourceLanguage sourceLanguage,
        string? name = null,
        string entryPoint = "main")
        => GetCompiler().CompileToSpirv(source, shaderType, sourceLanguage, name, entryPoint);

    public static byte[] CompileGlslToSpirv(
        string glslSource,
        EShaderType shaderType,
        string? name = null,
        string entryPoint = "main")
        => CompileToSpirv(glslSource, shaderType, ShaderSourceLanguage.Glsl, name, entryPoint);

    public static byte[] CompileHlslToSpirv(
        string hlslSource,
        EShaderType shaderType,
        string? name = null,
        string entryPoint = "main")
        => CompileToSpirv(hlslSource, shaderType, ShaderSourceLanguage.Hlsl, name, entryPoint);

    private static IRuntimeShaderCrossCompiler GetCompiler()
        => RuntimeShaderCrossCompiler.Current
            ?? throw new InvalidOperationException(
                "Shader cross-compilation requires a registered rendering backend module with a compiler capability. " +
                "Install Runtime.Bootstrap and register the Vulkan backend before compiling shader source.");
}
