using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.ShaderTranslation.Preprocessing;

/// <summary>
/// Converts matrix constructors that mix scalars and vectors to a form that Slang accepts.
/// </summary>
/// <remarks>
/// FXC accepts a matrix constructor with any mix of scalar and vector arguments when the component count
/// is correct, for example <c>float4x4(red, 0, green, 0, blue, 0, 0, 0, 0, 1)</c> with <c>float3</c> rows.
/// Slang accepts only one argument, one vector for each row, or one scalar for each component. The rewriter
/// does not know the argument types, so it changes each constructor whose argument count is not one of these
/// counts. The new form is <c>float4x4(XrMatrixComponentsStart().add(red).add(0)...toFloat4x4())</c>. The
/// <c>XrMatrixComponents</c> type in <c>HLSLLegacyConversions.cginc</c> collects the components in row order.
/// The outer constructor converts the float matrix to the original component type. The rewrite keeps the line count.
/// </remarks>
public static partial class MatrixConstructorRewriter
{
    /// <summary>Rewrites the matrix constructors of a translation unit.</summary>
    public static string Rewrite(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains("x2(", StringComparison.Ordinal) &&
            !text.Contains("x3(", StringComparison.Ordinal) &&
            !text.Contains("x4(", StringComparison.Ordinal))
        {
            return text;
        }

        List<HlslToken> tokens = HlslLexer.Tokenize(text);
        StringBuilder? builder = null;
        int position = 0;
        int index = 0;
        while (index + 1 < tokens.Count)
        {
            HlslToken token = tokens[index];
            Match type = MatrixTypePattern().Match(token.Text);
            if (token.Kind != HlslTokenKind.Identifier || !type.Success || !tokens[index + 1].IsSymbol("(") ||
                (index > 0 && tokens[index - 1].IsSymbol(".")))
            {
                index++;
                continue;
            }

            int close = FindArguments(tokens, index + 1, out List<(int Start, int End)> arguments);
            if (close < 0)
                break;

            int rows = type.Groups["rows"].Value[0] - '0';
            int columns = type.Groups["columns"].Value[0] - '0';
            if (arguments.Count is 0 or 1 || arguments.Count == rows || arguments.Count == rows * columns)
            {
                index++;
                continue;
            }

            builder ??= new StringBuilder(text.Length + 256);
            builder.Append(text, position, token.Start - position);
            builder.Append(token.Text).Append("(XrMatrixComponentsStart()");
            foreach ((int start, int end) in arguments)
                builder.Append(".add(").Append(Rewrite(text[start..end])).Append(')');
            builder.Append(".toFloat").Append(rows).Append('x').Append(columns).Append("())");
            position = tokens[close].End;
            while (index < tokens.Count && tokens[index].Start < position)
                index++;
        }

        if (builder is null)
            return text;

        builder.Append(text, position, text.Length - position);
        return builder.ToString();
    }

    /// <summary>
    /// Finds the top-level arguments of the call whose opening parenthesis is at <paramref name="open"/>.
    /// </summary>
    /// <returns>The index of the closing parenthesis, or -1 when the call has no end.</returns>
    private static int FindArguments(List<HlslToken> tokens, int open, out List<(int Start, int End)> arguments)
    {
        arguments = [];
        int depth = 0;
        int argumentStart = tokens[open].End;
        for (int index = open; index < tokens.Count; index++)
        {
            HlslToken token = tokens[index];
            if (token.IsSymbol("(") || token.IsSymbol("[") || token.IsSymbol("{"))
            {
                depth++;
            }
            else if (token.IsSymbol(")") || token.IsSymbol("]") || token.IsSymbol("}"))
            {
                if (--depth == 0)
                {
                    if (token.Start > argumentStart || arguments.Count > 0)
                        arguments.Add((argumentStart, token.Start));
                    return index;
                }
            }
            else if (depth == 1 && token.IsSymbol(","))
            {
                arguments.Add((argumentStart, token.Start));
                argumentStart = token.End;
            }
        }

        return -1;
    }

    [GeneratedRegex(@"^(?:float|half|fixed|min16float|min10float|double|int|uint|min16int|min16uint)(?<rows>[2-4])x(?<columns>[2-4])$")]
    private static partial Regex MatrixTypePattern();
}
