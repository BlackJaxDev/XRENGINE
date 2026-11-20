using System.Text;

namespace XREngine.ShaderTranslation.Preprocessing;

/// <summary>
/// Converts the selector of each <c>switch</c> statement to <c>int</c>, and removes attributes before <c>switch</c>.
/// </summary>
/// <remarks>
/// FXC accepts a <c>float</c> selector, for example <c>switch (_BlendMode)</c> on a material property, and
/// converts it to an integer. Slang requires an integer selector and reports the <c>case</c> labels as
/// not constant. The rewrite changes <c>switch (x)</c> to <c>switch (int(x))</c>. A selector that is already
/// an integer keeps its value. FXC also accepts the hints <c>[flatten]</c>, <c>[branch]</c>, <c>[forcecase]</c>, and
/// <c>[call]</c> before <c>switch</c>. Slang rejects them, so the rewrite replaces each attribute directly before
/// <c>switch</c> with spaces. The hints do not change the result. The rewrite keeps the line count.
/// </remarks>
public static class SwitchSelectorRewriter
{
    /// <summary>Rewrites the <c>switch</c> selectors of a translation unit.</summary>
    public static string Rewrite(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains("switch", StringComparison.Ordinal))
            return text;

        List<HlslToken> tokens = HlslLexer.Tokenize(text);
        List<(int Open, int Close, int AttributeStart)> selectors = [];
        for (int index = 0; index + 1 < tokens.Count; index++)
        {
            if (!tokens[index].IsIdentifier("switch") || !tokens[index + 1].IsSymbol("("))
                continue;

            int close = FindClose(tokens, index + 1);
            if (close > 0)
                selectors.Add((tokens[index + 1].End, tokens[close].Start, FindAttributeStart(tokens, index)));
        }

        if (selectors.Count == 0)
            return text;

        StringBuilder builder = new(text.Length + selectors.Count * 6);
        int position = 0;
        foreach ((int open, int close, int attributeStart) in selectors)
        {
            if (attributeStart >= 0)
            {
                int attributeEnd = text.LastIndexOf(']', open) + 1;
                builder.Append(text, position, attributeStart - position);
                for (int character = attributeStart; character < attributeEnd; character++)
                    builder.Append(text[character] is '\n' or '\r' ? text[character] : ' ');
                position = attributeEnd;
            }

            builder.Append(text, position, open - position).Append("int(");
            builder.Append(text, open, close - open).Append(')');
            position = close;
        }

        builder.Append(text, position, text.Length - position);
        return builder.ToString();
    }

    /// <summary>
    /// Returns the text position of the first attribute directly before the <c>switch</c> token, or -1.
    /// </summary>
    private static int FindAttributeStart(List<HlslToken> tokens, int switchIndex)
    {
        int start = -1;
        int index = switchIndex - 1;
        while (index >= 2 && tokens[index].IsSymbol("]"))
        {
            int open = index - 1;
            while (open >= 0 && !tokens[open].IsSymbol("[") && !tokens[open].IsSymbol(";") && !tokens[open].IsSymbol("{") && !tokens[open].IsSymbol("}"))
                open--;
            if (open < 0 || !tokens[open].IsSymbol("[") || open + 1 >= index || tokens[open + 1].Kind != HlslTokenKind.Identifier)
                break;
            // An index expression such as a[i] is not an attribute: an attribute follows a statement boundary or another attribute.
            if (open > 0 && !(tokens[open - 1].IsSymbol(";") || tokens[open - 1].IsSymbol("{") || tokens[open - 1].IsSymbol("}") ||
                tokens[open - 1].IsSymbol(")") || tokens[open - 1].IsSymbol("]") || tokens[open - 1].IsIdentifier("else")))
            {
                break;
            }

            start = tokens[open].Start;
            index = open - 1;
        }

        return start;
    }

    private static int FindClose(List<HlslToken> tokens, int open)
    {
        int depth = 0;
        for (int index = open; index < tokens.Count; index++)
        {
            if (tokens[index].IsSymbol("("))
                depth++;
            else if (tokens[index].IsSymbol(")") && --depth == 0)
                return index;
        }

        return -1;
    }
}
