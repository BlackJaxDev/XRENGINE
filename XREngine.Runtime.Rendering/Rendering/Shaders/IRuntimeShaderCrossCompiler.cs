namespace XREngine.Rendering;

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
}
