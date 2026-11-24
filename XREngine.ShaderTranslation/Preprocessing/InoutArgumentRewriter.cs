using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.ShaderTranslation.Preprocessing;

/// <summary>
/// Adds a converted copy for each <c>out</c> or <c>inout</c> argument whose type is not the parameter type.
/// </summary>
/// <remarks>
/// FXC accepts a local variable of another numeric type as an <c>out</c> or <c>inout</c> argument, for example a
/// <c>float</c> variable for an <c>inout int</c> parameter. It converts the value into the parameter and back
/// after the call. Slang requires an argument of the exact type. The rewriter changes a call statement such as
/// <c>F(value);</c> to <c>{ int _xrArgument0 = int(value); F(_xrArgument0); value = float(_xrArgument0); }</c>.
/// The rewriter changes only a call that is a full statement to a function with one definition for that
/// argument count, and only an argument that is a local variable or parameter with a numeric scalar or vector
/// type that has the same component count. The rewrite keeps the line count.
/// </remarks>
public static partial class InoutArgumentRewriter
{
    private const string TemporaryPrefix = "_xrArgument";

    private sealed record Parameter(string Modifier, string Type);

    private sealed record Function(string Name, List<Parameter> Parameters, int ParametersOpen, int BodyOpen, int BodyClose);

    private readonly record struct Edit(int Position, int Length, string Text, int Order);

    /// <summary>Rewrites the calls with converted <c>out</c> and <c>inout</c> arguments in a translation unit.</summary>
    public static string Rewrite(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains("out ", StringComparison.Ordinal))
            return text;

        List<HlslToken> tokens = HlslLexer.Tokenize(text);
        List<Function> functions = CollectFunctions(tokens);
        if (functions.Count == 0)
            return text;

        Dictionary<string, List<Function>> byName = new(StringComparer.Ordinal);
        foreach (Function function in functions)
        {
            if (!byName.TryGetValue(function.Name, out List<Function>? overloads))
                byName[function.Name] = overloads = [];
            overloads.Add(function);
        }

        List<Edit> edits = [];
        int temporaryCount = 0;
        foreach (Function function in functions)
            RewriteBody(tokens, function, byName, edits, ref temporaryCount);

        if (edits.Count == 0)
            return text;

        edits.Sort(static (left, right) =>
        {
            int compare = left.Position.CompareTo(right.Position);
            return compare != 0 ? compare : left.Order.CompareTo(right.Order);
        });
        StringBuilder builder = new(text.Length + edits.Count * 64);
        int position = 0;
        foreach (Edit edit in edits)
        {
            builder.Append(text, position, edit.Position - position).Append(edit.Text);
            position = edit.Position + edit.Length;
        }

        builder.Append(text, position, text.Length - position);
        return builder.ToString();
    }

    private static List<Function> CollectFunctions(List<HlslToken> tokens)
    {
        List<Function> functions = [];
        for (int index = 0; index < tokens.Count; index++)
        {
            if (!tokens[index].IsSymbol("{"))
                continue;

            int close = FindMatch(tokens, index, "{", "}");
            if (close < 0)
                break;

            Function? function = ReadFunction(tokens, index, close);
            if (function is not null)
                functions.Add(function);
            index = close;
        }

        return functions;
    }

    private static Function? ReadFunction(List<HlslToken> tokens, int bodyOpen, int bodyClose)
    {
        int parametersClose = bodyOpen - 1;
        // Skip a return semantic such as ': SV_Target'.
        if (parametersClose >= 2 && tokens[parametersClose].Kind == HlslTokenKind.Identifier && tokens[parametersClose - 1].IsSymbol(":"))
            parametersClose -= 2;
        if (parametersClose < 2 || !tokens[parametersClose].IsSymbol(")"))
            return null;

        int parametersOpen = FindOpen(tokens, parametersClose, "(", ")");
        if (parametersOpen < 2 ||
            tokens[parametersOpen - 1].Kind != HlslTokenKind.Identifier ||
            tokens[parametersOpen - 2].Kind != HlslTokenKind.Identifier)
        {
            return null;
        }

        List<Parameter> parameters = [];
        int start = parametersOpen + 1;
        for (int index = start; index <= parametersClose; index++)
        {
            if (tokens[index].IsSymbol("(") || tokens[index].IsSymbol("["))
            {
                int end = FindMatch(tokens, index, tokens[index].Text, tokens[index].IsSymbol("(") ? ")" : "]");
                if (end < 0)
                    return null;
                index = end;
                continue;
            }

            if (!tokens[index].IsSymbol(",") && index != parametersClose)
                continue;
            if (index > start)
                parameters.Add(ReadParameter(tokens, start, index));
            start = index + 1;
        }

        return new Function(tokens[parametersOpen - 1].Text, parameters, parametersOpen, bodyOpen, bodyClose);
    }

    private static Parameter ReadParameter(List<HlslToken> tokens, int start, int end)
    {
        string modifier = "in";
        for (int index = start; index < end; index++)
        {
            HlslToken token = tokens[index];
            if (token.IsIdentifier("out") || token.IsIdentifier("inout"))
            {
                modifier = token.Text;
                continue;
            }

            if (token.Kind == HlslTokenKind.Identifier && !IsModifier(token.Text))
                return new Parameter(modifier, token.Text);
        }

        return new Parameter(modifier, string.Empty);
    }

    private static bool IsModifier(string text)
        => text is "in" or "const" or "uniform" or "precise" or "nointerpolation" or "linear" or "centroid" or "noperspective" or "sample";

    private static void RewriteBody(List<HlslToken> tokens, Function function, Dictionary<string, List<Function>> byName, List<Edit> edits, ref int temporaryCount)
    {
        Dictionary<string, string?> locals = CollectLocals(tokens, function);
        for (int index = function.BodyOpen + 1; index + 1 < function.BodyClose; index++)
        {
            HlslToken name = tokens[index];
            if (name.Kind != HlslTokenKind.Identifier || !tokens[index + 1].IsSymbol("(") || !byName.TryGetValue(name.Text, out List<Function>? overloads))
                continue;
            if (!IsStatementStart(tokens, index))
                continue;

            int close = FindMatch(tokens, index + 1, "(", ")");
            if (close < 0 || close + 1 >= function.BodyClose || !tokens[close + 1].IsSymbol(";"))
                continue;

            List<(int Start, int End)> arguments = SplitArguments(tokens, index + 1, close);
            Function? target = null;
            foreach (Function overload in overloads)
            {
                if (overload.Parameters.Count != arguments.Count)
                    continue;
                if (target is not null)
                {
                    target = null;
                    break;
                }

                target = overload;
            }

            if (target is not null)
                RewriteCall(tokens, target, arguments, locals, name.Start, tokens[close + 1].End, edits, ref temporaryCount);
            index = close;
        }
    }

    private static void RewriteCall(
        List<HlslToken> tokens,
        Function target,
        List<(int Start, int End)> arguments,
        Dictionary<string, string?> locals,
        int statementStart,
        int statementEnd,
        List<Edit> edits,
        ref int temporaryCount)
    {
        StringBuilder? before = null;
        StringBuilder? after = null;
        for (int argument = 0; argument < arguments.Count; argument++)
        {
            Parameter parameter = target.Parameters[argument];
            (int start, int end) = arguments[argument];
            if (parameter.Modifier == "in" || end - start != 1)
                continue;

            HlslToken value = tokens[start];
            if (value.Kind != HlslTokenKind.Identifier || !locals.TryGetValue(value.Text, out string? valueType) || valueType is null)
                continue;
            if (!NeedsConversion(valueType, parameter.Type))
                continue;

            string temporary = TemporaryPrefix + temporaryCount++;
            before ??= new StringBuilder("{ ");
            after ??= new StringBuilder();
            before.Append(parameter.Type).Append(' ').Append(temporary);
            if (parameter.Modifier == "inout")
                before.Append(" = ").Append(parameter.Type).Append('(').Append(value.Text).Append(')');
            before.Append("; ");
            after.Append(' ').Append(value.Text).Append(" = ").Append(valueType).Append('(').Append(temporary).Append(");");
            edits.Add(new Edit(value.Start, value.Text.Length, temporary, edits.Count));
        }

        if (before is null || after is null)
            return;

        edits.Add(new Edit(statementStart, 0, before.ToString(), edits.Count));
        edits.Add(new Edit(statementEnd, 0, after.Append(" }").ToString(), edits.Count));
    }

    private static bool NeedsConversion(string valueType, string parameterType)
    {
        Match value = NumericTypePattern().Match(valueType);
        Match parameter = NumericTypePattern().Match(parameterType);
        if (!value.Success || !parameter.Success)
            return false;

        string valueCount = value.Groups["count"].Success ? value.Groups["count"].Value : "1";
        string parameterCount = parameter.Groups["count"].Success ? parameter.Groups["count"].Value : "1";
        return valueCount == parameterCount &&
            NormalizeComponent(value.Groups["component"].Value) != NormalizeComponent(parameter.Groups["component"].Value);
    }

    private static string NormalizeComponent(string component)
        => component switch
        {
            "half" or "fixed" or "min16float" or "min10float" => "float",
            "min16int" or "min12int" => "int",
            "min16uint" or "dword" => "uint",
            _ => component,
        };

    /// <summary>
    /// Finds the type of each parameter and local variable of a function. A name with two different types maps to null.
    /// </summary>
    private static Dictionary<string, string?> CollectLocals(List<HlslToken> tokens, Function function)
    {
        Dictionary<string, string?> locals = new(StringComparer.Ordinal);
        for (int index = function.ParametersOpen + 1; index < function.BodyClose; index++)
        {
            if (index == function.BodyOpen)
                continue;

            HlslToken type = tokens[index];
            if (type.Kind != HlslTokenKind.Identifier || index + 2 >= function.BodyClose || !NumericTypePattern().IsMatch(type.Text))
                continue;

            HlslToken name = tokens[index + 1];
            HlslToken next = tokens[index + 2];
            if (name.Kind != HlslTokenKind.Identifier ||
                !(next.IsSymbol("=") || next.IsSymbol(";") || next.IsSymbol(",") || next.IsSymbol(")") || next.IsSymbol(":")))
            {
                continue;
            }

            if (locals.TryGetValue(name.Text, out string? existing) && existing != type.Text)
                locals[name.Text] = null;
            else
                locals[name.Text] = type.Text;
        }

        return locals;
    }

    private static bool IsStatementStart(List<HlslToken> tokens, int index)
    {
        HlslToken previous = tokens[index - 1];
        if (previous.IsSymbol(";") || previous.IsSymbol("{") || previous.IsSymbol("}") || previous.IsIdentifier("else"))
            return true;
        if (!previous.IsSymbol(")"))
            return false;

        int open = FindOpen(tokens, index - 1, "(", ")");
        return open > 0 && (tokens[open - 1].IsIdentifier("if") || tokens[open - 1].IsIdentifier("for") || tokens[open - 1].IsIdentifier("while"));
    }

    private static List<(int Start, int End)> SplitArguments(List<HlslToken> tokens, int open, int close)
    {
        List<(int Start, int End)> arguments = [];
        int start = open + 1;
        int depth = 0;
        for (int index = open + 1; index < close; index++)
        {
            HlslToken token = tokens[index];
            if (token.IsSymbol("(") || token.IsSymbol("[") || token.IsSymbol("{"))
                depth++;
            else if (token.IsSymbol(")") || token.IsSymbol("]") || token.IsSymbol("}"))
                depth--;
            else if (depth == 0 && token.IsSymbol(","))
            {
                arguments.Add((start, index));
                start = index + 1;
            }
        }

        if (close > start || arguments.Count > 0)
            arguments.Add((start, close));
        return arguments;
    }

    private static int FindMatch(List<HlslToken> tokens, int open, string openSymbol, string closeSymbol)
    {
        int depth = 0;
        for (int index = open; index < tokens.Count; index++)
        {
            if (tokens[index].IsSymbol(openSymbol))
                depth++;
            else if (tokens[index].IsSymbol(closeSymbol) && --depth == 0)
                return index;
        }

        return -1;
    }

    private static int FindOpen(List<HlslToken> tokens, int close, string openSymbol, string closeSymbol)
    {
        int depth = 0;
        for (int index = close; index >= 0; index--)
        {
            if (tokens[index].IsSymbol(closeSymbol))
                depth++;
            else if (tokens[index].IsSymbol(openSymbol) && --depth == 0)
                return index;
        }

        return -1;
    }

    [GeneratedRegex(@"^(?<component>float|half|fixed|double|int|uint|bool|dword|min16float|min10float|min16int|min12int|min16uint)(?<count>[1-4])?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericTypePattern();
}
