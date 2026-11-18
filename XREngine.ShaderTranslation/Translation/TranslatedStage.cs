namespace XREngine.ShaderTranslation.Translation;

/// <summary>
/// Holds the outputs of one translated stage.
/// </summary>
/// <param name="Stage">The stage.</param>
/// <param name="EntryPoint">The source entry point function.</param>
/// <param name="Keywords">The keywords defined for this variant.</param>
/// <param name="Hlsl">The assembled translation unit given to the compiler.</param>
/// <param name="Spirv">The SPIR-V module.</param>
/// <param name="Glsl">The OpenGL GLSL 4.60 source in engine binding form.</param>
public sealed record TranslatedStage(
    ShaderProgramStage Stage,
    string EntryPoint,
    IReadOnlyList<string> Keywords,
    string Hlsl,
    byte[] Spirv,
    string Glsl);
