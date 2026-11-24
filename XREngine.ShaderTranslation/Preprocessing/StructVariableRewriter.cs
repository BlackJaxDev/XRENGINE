using System.Text;

namespace XREngine.ShaderTranslation.Preprocessing;

/// <summary>
/// Removes the <c>struct</c> keyword from variable declarations such as <c>struct Light light;</c>.
/// </summary>
/// <remarks>
/// FXC accepts the C form <c>struct TypeName variable;</c> for a variable of a struct type that is already
/// declared. Slang reads <c>struct TypeName</c> as the start of a new struct and then expects <c>{</c> or
/// <c>;</c>. The rewriter replaces the keyword with spaces when an identifier follows the type name, which
/// occurs only in a declaration of a variable, a parameter, or a field. A struct definition has <c>{</c>,
/// <c>:</c>, or <c>;</c> after the type name and does not change. The rewrite keeps the line count and the
/// column of each token.
/// </remarks>
public static class StructVariableRewriter
{
    private const string Keyword = "struct";

    /// <summary>Rewrites the struct variable declarations of a translation unit.</summary>
    public static string Rewrite(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains(Keyword, StringComparison.Ordinal))
            return text;

        List<HlslToken> tokens = HlslLexer.Tokenize(text);
        StringBuilder? builder = null;
        for (int index = 0; index + 2 < tokens.Count; index++)
        {
            if (!tokens[index].IsIdentifier(Keyword) ||
                tokens[index + 1].Kind != HlslTokenKind.Identifier ||
                tokens[index + 2].Kind != HlslTokenKind.Identifier)
            {
                continue;
            }

            builder ??= new StringBuilder(text);
            for (int character = tokens[index].Start; character < tokens[index].End; character++)
                builder[character] = ' ';
        }

        return builder?.ToString() ?? text;
    }
}
