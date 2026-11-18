namespace XREngine.ShaderTranslation.Diagnostics;

/// <summary>
/// Defines the stable codes of shader translation diagnostics.
/// </summary>
public static class ShaderTranslationDiagnosticCodes
{
    /// <summary>The ShaderLab structure text did not parse.</summary>
    public const string ParseFailed = "UNITYSHADER0001";

    /// <summary>The shader has no subshader with a translatable pass.</summary>
    public const string NoTranslatablePass = "UNITYSHADER0002";

    /// <summary>The compiler frontend rejected a stage.</summary>
    public const string CompileFailed = "UNITYSHADER0003";

    /// <summary>The program uses <c>#pragma surface</c>. Surface shader generation is not available yet.</summary>
    public const string SurfaceShaderUnsupported = "UNITYSHADER0004";

    /// <summary>The program uses a GLSL program block.</summary>
    public const string GlslProgramUnsupported = "UNITYSHADER0005";

    /// <summary>A pass has a light mode that the engine does not draw. The pass is skipped.</summary>
    public const string PassSkipped = "UNITYSHADER0006";

    /// <summary>A vertex input semantic has no engine vertex attribute.</summary>
    public const string UnsupportedVertexInput = "UNITYSHADER0007";

    /// <summary>A pass uses <c>GrabPass</c> or <c>UsePass</c>, which the translator does not resolve yet.</summary>
    public const string PassReferenceUnsupported = "UNITYSHADER0008";

    /// <summary>A program has no vertex or fragment entry point.</summary>
    public const string MissingEntryPoint = "UNITYSHADER0009";

    /// <summary>A required external tool (slangc or spirv-cross) is not available.</summary>
    public const string ToolUnavailable = "UNITYSHADER0010";

    /// <summary>A stage uses a hull, domain, or geometry entry point.</summary>
    public const string AdditionalStage = "UNITYSHADER0011";

    /// <summary>A binding rewrite needs attention, for example a texture sampled with two samplers.</summary>
    public const string BindingRewrite = "UNITYSHADER0012";
}
