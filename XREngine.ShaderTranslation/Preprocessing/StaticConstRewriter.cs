using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.ShaderTranslation.Preprocessing;

/// <summary>
/// Removes <c>const</c> from a <c>static const</c> variable whose initializer calls a function.
/// </summary>
/// <remarks>
/// FXC evaluates a call such as <c>GammaToLinearSpace(float3(0.5, 0.5, 0.5))</c> at compile time, so Unity
/// code uses it in <c>static const</c> initializers. Slang requires a constant expression. Without
/// <c>const</c>, the variable is a private copy that each invocation initializes, which gives the same value.
/// Constructors of numeric types, such as <c>float3(...)</c>, are constant and do not cause the rewrite.
/// The rewrite keeps the line count.
/// </remarks>
public static partial class StaticConstRewriter
{
    /// <summary>Rewrites the <c>static const</c> declarations of a translation unit.</summary>
    public static string Rewrite(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains("const", StringComparison.Ordinal))
            return text;

        List<HlslToken> tokens = HlslLexer.Tokenize(text);
        List<HlslToken> removed = [];
        for (int index = 0; index + 1 < tokens.Count; index++)
        {
            HlslToken first = tokens[index];
            HlslToken second = tokens[index + 1];
            HlslToken constToken;
            if (first.IsIdentifier("static") && second.IsIdentifier("const"))
                constToken = second;
            else if (first.IsIdentifier("const") && second.IsIdentifier("static"))
                constToken = first;
            else
                continue;

            if (InitializerCallsFunction(tokens, index + 2))
                removed.Add(constToken);
        }

        if (removed.Count == 0)
            return text;

        StringBuilder builder = new(text);
        foreach (HlslToken token in removed)
            builder.Remove(token.Start, token.Text.Length).Insert(token.Start, new string(' ', token.Text.Length));
        return builder.ToString();
    }

    private static bool InitializerCallsFunction(List<HlslToken> tokens, int index)
    {
        bool inInitializer = false;
        int depth = 0;
        for (; index < tokens.Count; index++)
        {
            HlslToken token = tokens[index];
            if (token.IsSymbol("(") || token.IsSymbol("{") || token.IsSymbol("["))
                depth++;
            else if (token.IsSymbol(")") || token.IsSymbol("}") || token.IsSymbol("]"))
                depth--;
            else if (depth == 0 && token.IsSymbol(";"))
                return false;
            else if (depth == 0 && token.IsSymbol("="))
                inInitializer = true;
            else if (inInitializer && token.Kind == HlslTokenKind.Identifier && index + 1 < tokens.Count &&
                tokens[index + 1].IsSymbol("(") && !NumericTypePattern().IsMatch(token.Text))
                return true;
        }

        return false;
    }

    [GeneratedRegex(@"^(?:float|half|fixed|double|int|uint|bool|dword|min16float|min10float|min16int|min12int|min16uint)(?:[1-4](?:x[1-4])?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericTypePattern();
}
