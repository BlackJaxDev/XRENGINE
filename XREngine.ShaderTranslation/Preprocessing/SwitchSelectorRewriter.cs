using System.Text;

namespace XREngine.ShaderTranslation.Preprocessing;

/// <summary>
/// Converts the selector of each <c>switch</c> statement to <c>int</c>.
/// </summary>
/// <remarks>
/// FXC accepts a <c>float</c> selector, for example <c>switch (_BlendMode)</c> on a material property, and
/// converts it to an integer. Slang requires an integer selector and reports the <c>case</c> labels as
/// not constant. The rewrite changes <c>switch (x)</c> to <c>switch (int(x))</c>. A selector that is already
/// an integer keeps its value. The rewrite keeps the line count.
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
        List<(int Open, int Close)> selectors = [];
        for (int index = 0; index + 1 < tokens.Count; index++)
        {
            if (!tokens[index].IsIdentifier("switch") || !tokens[index + 1].IsSymbol("("))
                continue;

            int close = FindClose(tokens, index + 1);
            if (close > 0)
                selectors.Add((tokens[index + 1].End, tokens[close].Start));
        }

        if (selectors.Count == 0)
            return text;

        StringBuilder builder = new(text.Length + selectors.Count * 6);
        int position = 0;
        foreach ((int open, int close) in selectors)
        {
            builder.Append(text, position, open - position).Append("int(");
            builder.Append(text, open, close - open).Append(')');
            position = close;
        }

        builder.Append(text, position, text.Length - position);
        return builder.ToString();
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
