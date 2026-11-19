namespace XREngine.ShaderTranslation.Preprocessing;

/// <summary>
/// Splits HLSL source text into tokens for source analysis.
/// </summary>
/// <remarks>
/// The lexer skips comments and preprocessor directive lines, including continued lines.
/// It does not expand macros. Token positions refer to the original text, so callers can edit it in place.
/// </remarks>
public static class HlslLexer
{
    private static readonly string[] Symbols =
    [
        "<<=", ">>=", "::", "->", "++", "--", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||",
        "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=",
    ];

    /// <summary>Returns the tokens of the text.</summary>
    public static List<HlslToken> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<HlslToken> tokens = [];
        int index = 0;
        bool lineStart = true;
        while (index < text.Length)
        {
            char current = text[index];
            if (current == '\n')
            {
                lineStart = true;
                index++;
                continue;
            }

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '#' && lineStart)
            {
                index = SkipDirective(text, index);
                continue;
            }

            lineStart = false;
            if (current == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                index = SkipToLineEnd(text, index);
                continue;
            }

            if (current == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                int close = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = close < 0 ? text.Length : close + 2;
                continue;
            }

            int start = index;
            if (char.IsLetter(current) || current == '_')
            {
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
                    index++;
                tokens.Add(new HlslToken(HlslTokenKind.Identifier, text[start..index], start));
                continue;
            }

            if (char.IsDigit(current) || (current == '.' && index + 1 < text.Length && char.IsDigit(text[index + 1])))
            {
                index = SkipNumber(text, index);
                tokens.Add(new HlslToken(HlslTokenKind.Number, text[start..index], start));
                continue;
            }

            if (current is '"' or '\'')
            {
                index = SkipQuoted(text, index, current);
                tokens.Add(new HlslToken(HlslTokenKind.String, text[start..index], start));
                continue;
            }

            string symbol = MatchSymbol(text, index);
            tokens.Add(new HlslToken(HlslTokenKind.Symbol, symbol, start));
            index += symbol.Length;
        }

        return tokens;
    }

    private static int SkipDirective(string text, int index)
    {
        while (index < text.Length && text[index] != '\n')
        {
            if (text[index] == '\\' && index + 1 < text.Length && (text[index + 1] == '\n' || text[index + 1] == '\r'))
            {
                index = text.IndexOf('\n', index + 1);
                if (index < 0)
                    return text.Length;
            }

            index++;
        }

        return index;
    }

    private static int SkipToLineEnd(string text, int index)
    {
        int end = text.IndexOf('\n', index);
        return end < 0 ? text.Length : end;
    }

    private static int SkipNumber(string text, int index)
    {
        while (index < text.Length)
        {
            char current = text[index];
            bool exponentSign = (current is '+' or '-') && index > 0 && (text[index - 1] is 'e' or 'E') && !IsHexNumberBefore(text, index);
            if (!char.IsLetterOrDigit(current) && current != '.' && !exponentSign)
                break;
            index++;
        }

        return index;
    }

    private static bool IsHexNumberBefore(string text, int index)
    {
        int start = index - 1;
        while (start > 0 && char.IsLetterOrDigit(text[start - 1]))
            start--;
        return start + 1 < text.Length && text[start] == '0' && (text[start + 1] is 'x' or 'X');
    }

    private static int SkipQuoted(string text, int index, char quote)
    {
        index++;
        while (index < text.Length && text[index] != quote && text[index] != '\n')
            index += text[index] == '\\' ? 2 : 1;
        return Math.Min(index + 1, text.Length);
    }

    private static string MatchSymbol(string text, int index)
    {
        foreach (string symbol in Symbols)
            if (string.CompareOrdinal(text, index, symbol, 0, symbol.Length) == 0)
                return symbol;
        return text[index].ToString();
    }
}
