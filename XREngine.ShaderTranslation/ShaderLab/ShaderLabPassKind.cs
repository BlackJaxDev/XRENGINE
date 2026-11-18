namespace XREngine.ShaderTranslation.ShaderLab;

/// <summary>
/// Identifies the form of a ShaderLab pass entry.
/// </summary>
public enum ShaderLabPassKind
{
    /// <summary>A <c>Pass { }</c> block with state and an optional program.</summary>
    Normal,

    /// <summary>A <c>GrabPass { }</c> block that copies scene color to a texture.</summary>
    Grab,

    /// <summary>A <c>UsePass "Shader/PASS"</c> reference to a pass of another shader.</summary>
    Use,
}
