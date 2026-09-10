namespace XREngine.Rendering;

using XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Backend-module capability for compiling authoring shader sources to SPIR-V.
/// </summary>
internal interface IRuntimeShaderCrossCompiler
{
    byte[] CompileToSpirv(
        string source,
        EShaderType shaderType,
        ShaderSourceLanguage sourceLanguage,
        string? name,
        string entryPoint);

    Task<ShaderCompileResult> CompileAsync(ShaderCompileRequest request, CancellationToken cancellationToken = default);
}
