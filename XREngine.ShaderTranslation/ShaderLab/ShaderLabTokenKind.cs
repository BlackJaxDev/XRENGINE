namespace XREngine.ShaderTranslation.ShaderLab;

/// <summary>
/// Identifies the class of a <see cref="ShaderLabToken"/>.
/// </summary>
public enum ShaderLabTokenKind
{
    Identifier,
    String,
    Number,
    Symbol,
    ProgramBlock,
    End,
}
