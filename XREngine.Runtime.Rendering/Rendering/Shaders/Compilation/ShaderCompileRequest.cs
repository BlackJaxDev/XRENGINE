namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Language-neutral shader frontend request for an explicit bytecode target.
/// </summary>
public sealed record ShaderCompileRequest(
    ShaderSourceLanguage Language,
    string Source,
    string? SourcePath,
    EShaderType Stage,
    string EntryPoint = "main",
    ShaderCompileTarget Target = ShaderCompileTarget.Vulkan14Spirv16,
    IReadOnlyList<string>? Includes = null,
    IReadOnlyList<string>? Defines = null,
    IReadOnlyList<string>? RequiredCapabilities = null,
    ShaderMatrixLayout MatrixLayout = ShaderMatrixLayout.ColumnMajor,
    string SemanticSchemaIdentity = "");
