namespace XREngine.ShaderTranslation.Preprocessing;

/// <summary>
/// One token of HLSL source text.
/// </summary>
/// <param name="Kind">The token kind.</param>
/// <param name="Text">The token text.</param>
/// <param name="Start">The index of the first character in the source text.</param>
public readonly record struct HlslToken(HlslTokenKind Kind, string Text, int Start)
{
    /// <summary>Gets the index after the last character in the source text.</summary>
    public int End => Start + Text.Length;

    /// <summary>Returns true when the token is the given symbol.</summary>
    public bool IsSymbol(string symbol) => Kind == HlslTokenKind.Symbol && Text == symbol;

    /// <summary>Returns true when the token is the given identifier or keyword.</summary>
    public bool IsIdentifier(string name) => Kind == HlslTokenKind.Identifier && Text == name;
}
