namespace XREngine.ShaderTranslation.Preprocessing;

/// <summary>
/// Identifies the kind of an <see cref="HlslToken"/>.
/// </summary>
public enum HlslTokenKind
{
    /// <summary>A name or a keyword.</summary>
    Identifier,

    /// <summary>A numeric literal.</summary>
    Number,

    /// <summary>A string or character literal.</summary>
    String,

    /// <summary>An operator or a punctuation mark.</summary>
    Symbol,
}
