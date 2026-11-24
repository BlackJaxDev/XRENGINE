namespace XREngine.ShaderTranslation.ShaderLab;

/// <summary>
/// Holds one lexical token of ShaderLab structure text.
/// </summary>
/// <param name="Kind">The token class.</param>
/// <param name="Text">The token text. A string token holds the text without quotes.</param>
/// <param name="Line">The one-based source line of the first token character.</param>
/// <param name="Start">The source offset of the first token character.</param>
/// <param name="End">The source offset after the last token character.</param>
/// <param name="Program">The captured block of a <see cref="ShaderLabTokenKind.ProgramBlock"/> token; otherwise null.</param>
public readonly record struct ShaderLabToken(
    ShaderLabTokenKind Kind,
    string Text,
    int Line,
    int Start,
    int End,
    ShaderLabProgramBlock? Program = null)
{
    /// <summary>Returns true when this token is the given symbol.</summary>
    public bool IsSymbol(char symbol)
        => Kind == ShaderLabTokenKind.Symbol && Text.Length == 1 && Text[0] == symbol;

    /// <summary>Returns true when this token is an identifier equal to the keyword, without case.</summary>
    public bool IsKeyword(string keyword)
        => Kind == ShaderLabTokenKind.Identifier && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);
}
