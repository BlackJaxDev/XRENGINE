namespace XREngine.ShaderTranslation.ShaderLab;

/// <summary>
/// Identifies the language marker of a ShaderLab program block.
/// </summary>
public enum ShaderLabProgramLanguage
{
    /// <summary><c>CGPROGRAM</c> or <c>CGINCLUDE</c>. Unity adds implicit includes for this marker.</summary>
    Cg,

    /// <summary><c>HLSLPROGRAM</c> or <c>HLSLINCLUDE</c>. Unity adds no implicit includes.</summary>
    Hlsl,

    /// <summary><c>GLSLPROGRAM</c> or <c>GLSLINCLUDE</c>. Translation does not support this marker.</summary>
    Glsl,
}
