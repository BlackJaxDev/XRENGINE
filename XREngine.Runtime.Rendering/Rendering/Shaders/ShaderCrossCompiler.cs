namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral entry point for compiling GLSL or HLSL authoring sources to SPIR-V.
/// </summary>
public static class ShaderCrossCompiler
{
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
