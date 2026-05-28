using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Rendering;

internal static partial class UberShaderVariantBuilder
{
    private enum EKnownConditionValue
    {
        False,
        True,
        Unknown,
    }

    private readonly record struct ConditionalBranch(
        int DirectiveLine,
        int ContentStartLine,
        int ContentEndLine,
        EConditionalDirectiveKind Kind,
        string Expression);

    private readonly record struct ConditionalGroup(
        int IfLine,
        int EndIfLine,
        int EndExclusiveLine,
        ConditionalBranch[] Branches);

    private enum EConditionalDirectiveKind
    {
        If,
        Ifdef,
        Ifndef,
        Elif,
        Else,
        Endif,
    }

    private static string PruneKnownConditionalBlocks(
        string source,
        IReadOnlySet<string> knownMacros,
        IReadOnlySet<string> definedMacros)
    {
        if (string.IsNullOrEmpty(source) || knownMacros.Count == 0)
            return source;

        List<string> lines = SplitLinesPreservingNewlines(source);
        if (lines.Count == 0)
            return source;

        try
        {
            return ProcessConditionalRange(lines, 0, lines.Count, knownMacros, definedMacros);
        }
        catch
        {
            return source;
        }
    }

    private static List<string> SplitLinesPreservingNewlines(string source)
    {
        List<string> lines = [];
        int start = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] != '\n')
                continue;

            lines.Add(source[start..(i + 1)]);
            start = i + 1;
        }

        if (start < source.Length)
            lines.Add(source[start..]);
        return lines;
    }

    private static string ProcessConditionalRange(
        IReadOnlyList<string> lines,
        int startLine,
        int endLine,
        IReadOnlySet<string> knownMacros,
        IReadOnlySet<string> definedMacros)
    {
        StringBuilder builder = new();
        int line = startLine;
        while (line < endLine)
        {
            if (TryParseConditionalStart(lines[line], out EConditionalDirectiveKind kind, out string expression) &&
                TryFindConditionalGroup(lines, line, endLine, kind, expression, out ConditionalGroup group))
            {
                AppendProcessedConditionalGroup(builder, lines, group, knownMacros, definedMacros);
                line = group.EndExclusiveLine;
                continue;
            }

            builder.Append(lines[line]);
            line++;
        }

        return builder.ToString();
    }

    private static bool TryFindConditionalGroup(
        IReadOnlyList<string> lines,
        int ifLine,
        int endLine,
        EConditionalDirectiveKind ifKind,
        string ifExpression,
        out ConditionalGroup group)
    {
        List<ConditionalBranch> branches =
        [
            new(ifLine, ifLine + 1, ifLine + 1, ifKind, ifExpression),
        ];

        int depth = 0;
        for (int line = ifLine + 1; line < endLine; line++)
        {
            if (TryParseConditionalStart(lines[line], out _, out _))
            {
                depth++;
                continue;
            }

            if (TryParseConditionalDirective(lines[line], out EConditionalDirectiveKind directiveKind, out string expression))
            {
                if (directiveKind == EConditionalDirectiveKind.Endif)
                {
                    if (depth > 0)
                    {
                        depth--;
                        continue;
                    }

                    ConditionalBranch last = branches[^1];
                    branches[^1] = last with { ContentEndLine = line };
                    group = new ConditionalGroup(ifLine, line, line + 1, [.. branches]);
                    return true;
                }

                if (depth == 0 && directiveKind is EConditionalDirectiveKind.Elif or EConditionalDirectiveKind.Else)
                {
                    ConditionalBranch last = branches[^1];
                    branches[^1] = last with { ContentEndLine = line };
                    branches.Add(new(line, line + 1, line + 1, directiveKind, expression));
                }
            }
        }

        group = default;
        return false;
    }

    private static void AppendProcessedConditionalGroup(
        StringBuilder builder,
        IReadOnlyList<string> lines,
        ConditionalGroup group,
        IReadOnlySet<string> knownMacros,
        IReadOnlySet<string> definedMacros)
    {
        if (TrySelectKnownBranch(group.Branches, knownMacros, definedMacros, out ConditionalBranch selectedBranch))
        {
            builder.Append(ProcessConditionalRange(
                lines,
                selectedBranch.ContentStartLine,
                selectedBranch.ContentEndLine,
                knownMacros,
                definedMacros));
            return;
        }

        foreach (ConditionalBranch branch in group.Branches)
        {
            builder.Append(lines[branch.DirectiveLine]);
            builder.Append(ProcessConditionalRange(
                lines,
                branch.ContentStartLine,
                branch.ContentEndLine,
                knownMacros,
                definedMacros));
        }

        builder.Append(lines[group.EndIfLine]);
    }

    private static bool TrySelectKnownBranch(
        IReadOnlyList<ConditionalBranch> branches,
        IReadOnlySet<string> knownMacros,
        IReadOnlySet<string> definedMacros,
        out ConditionalBranch selectedBranch)
    {
        selectedBranch = default;

        foreach (ConditionalBranch branch in branches)
        {
            EKnownConditionValue value = EvaluateBranchCondition(branch, knownMacros, definedMacros);
            if (value == EKnownConditionValue.Unknown)
                return false;

            if (value == EKnownConditionValue.True)
            {
                selectedBranch = branch;
                return true;
            }
        }

        return true;
    }

    private static EKnownConditionValue EvaluateBranchCondition(
        ConditionalBranch branch,
        IReadOnlySet<string> knownMacros,
        IReadOnlySet<string> definedMacros)
    {
        return branch.Kind switch
        {
            EConditionalDirectiveKind.Ifdef => EvaluateMacroDefined(branch.Expression, knownMacros, definedMacros),
            EConditionalDirectiveKind.Ifndef => Not(EvaluateMacroDefined(branch.Expression, knownMacros, definedMacros)),
            EConditionalDirectiveKind.If or EConditionalDirectiveKind.Elif => new PreprocessorExpressionParser(branch.Expression, knownMacros, definedMacros).Parse(),
            EConditionalDirectiveKind.Else => EKnownConditionValue.True,
            _ => EKnownConditionValue.Unknown,
        };
    }

    private static EKnownConditionValue EvaluateMacroDefined(
        string macro,
        IReadOnlySet<string> knownMacros,
        IReadOnlySet<string> definedMacros)
    {
        string name = macro.Trim();
        if (!IsIdentifier(name) || !knownMacros.Contains(name))
            return EKnownConditionValue.Unknown;

        return definedMacros.Contains(name)
            ? EKnownConditionValue.True
            : EKnownConditionValue.False;
    }

    private static bool TryParseConditionalStart(
        string line,
        out EConditionalDirectiveKind kind,
        out string expression)
    {
        if (TryParseConditionalDirective(line, out kind, out expression) &&
            kind is EConditionalDirectiveKind.If or EConditionalDirectiveKind.Ifdef or EConditionalDirectiveKind.Ifndef)
        {
            return true;
        }

        kind = default;
        expression = string.Empty;
        return false;
    }

    private static bool TryParseConditionalDirective(
        string line,
        out EConditionalDirectiveKind kind,
        out string expression)
    {
        kind = default;
        expression = string.Empty;

        string trimmed = StripLineComment(line).Trim();
        if (trimmed.Length == 0 || trimmed[0] != '#')
            return false;

        ReadOnlySpan<char> directive = trimmed.AsSpan(1).TrimStart();
        if (directive.StartsWith("ifdef".AsSpan(), StringComparison.Ordinal) && IsDirectiveBoundary(directive, 5))
        {
            kind = EConditionalDirectiveKind.Ifdef;
            expression = directive[5..].Trim().ToString();
            return true;
        }

        if (directive.StartsWith("ifndef".AsSpan(), StringComparison.Ordinal) && IsDirectiveBoundary(directive, 6))
        {
            kind = EConditionalDirectiveKind.Ifndef;
            expression = directive[6..].Trim().ToString();
            return true;
        }

        if (directive.StartsWith("if".AsSpan(), StringComparison.Ordinal) && IsDirectiveBoundary(directive, 2))
        {
            kind = EConditionalDirectiveKind.If;
            expression = directive[2..].Trim().ToString();
            return true;
        }

        if (directive.StartsWith("elif".AsSpan(), StringComparison.Ordinal) && IsDirectiveBoundary(directive, 4))
        {
            kind = EConditionalDirectiveKind.Elif;
            expression = directive[4..].Trim().ToString();
            return true;
        }

        if (directive.StartsWith("else".AsSpan(), StringComparison.Ordinal) && IsDirectiveBoundary(directive, 4))
        {
            kind = EConditionalDirectiveKind.Else;
            return true;
        }

        if (directive.StartsWith("endif".AsSpan(), StringComparison.Ordinal) && IsDirectiveBoundary(directive, 5))
        {
            kind = EConditionalDirectiveKind.Endif;
            return true;
        }

        return false;
    }

    private static bool IsDirectiveBoundary(ReadOnlySpan<char> directive, int length)
        => directive.Length == length || char.IsWhiteSpace(directive[length]);

    private static string StripLineComment(string line)
    {
        int commentIndex = line.IndexOf("//", StringComparison.Ordinal);
        return commentIndex < 0 ? line : line[..commentIndex];
    }

    private static bool IsIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        if (!IsIdentifierStart(value[0]))
            return false;
        for (int i = 1; i < value.Length; i++)
        {
            if (!IsIdentifierPart(value[i]))
                return false;
        }
        return true;
    }

    private static bool IsIdentifierStart(char value)
        => value == '_' || char.IsAsciiLetter(value);

    private static bool IsIdentifierPart(char value)
        => value == '_' || char.IsAsciiLetterOrDigit(value);

    private static EKnownConditionValue Not(EKnownConditionValue value)
        => value switch
        {
            EKnownConditionValue.True => EKnownConditionValue.False,
            EKnownConditionValue.False => EKnownConditionValue.True,
            _ => EKnownConditionValue.Unknown,
        };

    private static EKnownConditionValue And(EKnownConditionValue left, EKnownConditionValue right)
    {
        if (left == EKnownConditionValue.False || right == EKnownConditionValue.False)
            return EKnownConditionValue.False;
        if (left == EKnownConditionValue.True && right == EKnownConditionValue.True)
            return EKnownConditionValue.True;
        return EKnownConditionValue.Unknown;
    }

    private static EKnownConditionValue Or(EKnownConditionValue left, EKnownConditionValue right)
    {
        if (left == EKnownConditionValue.True || right == EKnownConditionValue.True)
            return EKnownConditionValue.True;
        if (left == EKnownConditionValue.False && right == EKnownConditionValue.False)
            return EKnownConditionValue.False;
        return EKnownConditionValue.Unknown;
    }

    private static string PruneStaticIfBlocks(string source)
    {
        if (string.IsNullOrEmpty(source))
            return source;

        Dictionary<string, double> constants = CollectStaticNumericConstants(source);
        string current = source;
        for (int pass = 0; pass < 512; pass++)
        {
            if (!TryPruneNextStaticIf(current, constants, out string next))
                break;

            current = next;
        }

        return current;
    }

    private static Dictionary<string, double> CollectStaticNumericConstants(string source)
    {
        Dictionary<string, double> constants = new(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
            source,
            @"^[ \t]*#[ \t]*define[ \t]+(?<name>[A-Za-z_][A-Za-z0-9_]*)[ \t]+(?<value>[+-]?(?:(?:\d+(?:\.\d*)?)|(?:\.\d+))(?:[eE][+-]?\d+)?)[uUfF]?\b",
            RegexOptions.Multiline))
        {
            if (TryParseStaticNumber(match.Groups["value"].Value, out double value))
                constants[match.Groups["name"].Value] = value;
        }

        foreach (Match match in Regex.Matches(
            source,
            @"\bconst[ \t]+(?:float|int|uint|double)[ \t]+(?<name>[A-Za-z_][A-Za-z0-9_]*)[ \t]*=[ \t]*(?<value>[+-]?(?:(?:\d+(?:\.\d*)?)|(?:\.\d+))(?:[eE][+-]?\d+)?)[uUfF]?[ \t]*;",
            RegexOptions.Multiline))
        {
            if (TryParseStaticNumber(match.Groups["value"].Value, out double value))
                constants[match.Groups["name"].Value] = value;
        }

        return constants;
    }

    private static bool TryPruneNextStaticIf(string source, IReadOnlyDictionary<string, double> constants, out string next)
    {
        next = source;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '/' && i + 1 < source.Length)
            {
                if (source[i + 1] == '/')
                {
                    i = SkipLineComment(source, i + 2);
                    continue;
                }

                if (source[i + 1] == '*')
                {
                    i = SkipBlockComment(source, i + 2);
                    continue;
                }
            }

            if (c == '"')
            {
                i = SkipStringLiteral(source, i + 1);
                continue;
            }

            if (!IsKeywordAt(source, i, "if"))
                continue;

            if (IsPrecededByElseKeyword(source, i))
                continue;

            int conditionOpen = SkipTrivia(source, i + 2);
            if (conditionOpen >= source.Length || source[conditionOpen] != '(')
                continue;

            int conditionClose = FindMatchingDelimiter(source, conditionOpen, '(', ')');
            if (conditionClose < 0)
                continue;

            string expression = source[(conditionOpen + 1)..conditionClose];
            if (!TryEvaluateStaticBooleanExpression(expression, constants, out bool condition))
                continue;

            int thenStart = SkipTrivia(source, conditionClose + 1);
            if (thenStart >= source.Length || source[thenStart] != '{')
                continue;

            int thenEnd = FindMatchingDelimiter(source, thenStart, '{', '}');
            if (thenEnd < 0)
                continue;
            thenEnd++;

            int replacementEnd = thenEnd;
            string replacement = condition ? source[thenStart..thenEnd] : string.Empty;

            int elseKeyword = SkipTrivia(source, thenEnd);
            if (IsKeywordAt(source, elseKeyword, "else"))
            {
                int elseStart = SkipTrivia(source, elseKeyword + 4);
                if (IsKeywordAt(source, elseStart, "if"))
                    continue;

                if (elseStart < source.Length && source[elseStart] == '{')
                {
                    int elseEnd = FindMatchingDelimiter(source, elseStart, '{', '}');
                    if (elseEnd >= 0)
                    {
                        elseEnd++;
                        replacementEnd = elseEnd;
                        if (!condition)
                            replacement = source[elseStart..elseEnd];
                    }
                }
            }

            next = source[..i] + replacement + source[replacementEnd..];
            return true;
        }

        return false;
    }

    private static bool IsPrecededByElseKeyword(string source, int index)
    {
        int cursor = index - 1;
        while (cursor >= 0 && char.IsWhiteSpace(source[cursor]))
            cursor--;

        if (cursor < 0)
            return false;

        if (cursor >= 1 && source[cursor - 1] == '/' && source[cursor] == '/')
            return false;

        int end = cursor + 1;
        while (cursor >= 0 && IsIdentifierPart(source[cursor]))
            cursor--;

        int start = cursor + 1;
        return start < end &&
               string.Equals(source[start..end], "else", StringComparison.Ordinal);
    }

    private static bool TryEvaluateStaticBooleanExpression(
        string expression,
        IReadOnlyDictionary<string, double> constants,
        out bool value)
    {
        expression = StripOuterParentheses(expression.Trim());
        value = false;
        if (expression.Length == 0)
            return false;

        if (TrySplitTopLevel(expression, "||", out string left, out string right))
        {
            if (!TryEvaluateStaticBooleanExpression(left, constants, out bool leftValue) ||
                !TryEvaluateStaticBooleanExpression(right, constants, out bool rightValue))
            {
                return false;
            }

            value = leftValue || rightValue;
            return true;
        }

        if (TrySplitTopLevel(expression, "&&", out left, out right))
        {
            if (!TryEvaluateStaticBooleanExpression(left, constants, out bool leftValue) ||
                !TryEvaluateStaticBooleanExpression(right, constants, out bool rightValue))
            {
                return false;
            }

            value = leftValue && rightValue;
            return true;
        }

        if (expression[0] == '!')
        {
            if (!TryEvaluateStaticBooleanExpression(expression[1..], constants, out bool inner))
                return false;

            value = !inner;
            return true;
        }

        foreach (string op in new[] { "==", "!=", ">=", "<=", ">", "<" })
        {
            int opIndex = FindTopLevelOperator(expression, op);
            if (opIndex < 0)
                continue;

            string lhs = expression[..opIndex];
            string rhs = expression[(opIndex + op.Length)..];
            if (!TryEvaluateStaticNumberExpression(lhs, constants, out double leftNumber) ||
                !TryEvaluateStaticNumberExpression(rhs, constants, out double rightNumber))
            {
                return false;
            }

            value = op switch
            {
                "==" => Math.Abs(leftNumber - rightNumber) <= 0.0000001,
                "!=" => Math.Abs(leftNumber - rightNumber) > 0.0000001,
                ">=" => leftNumber >= rightNumber,
                "<=" => leftNumber <= rightNumber,
                ">" => leftNumber > rightNumber,
                "<" => leftNumber < rightNumber,
                _ => false,
            };
            return true;
        }

        if (TryEvaluateStaticNumberExpression(expression, constants, out double number))
        {
            value = Math.Abs(number) > 0.0000001;
            return true;
        }

        return false;
    }

    private static bool TryEvaluateStaticNumberExpression(
        string expression,
        IReadOnlyDictionary<string, double> constants,
        out double value)
    {
        expression = StripOuterParentheses(expression.Trim());
        value = 0.0;
        if (expression.Length == 0)
            return false;

        if (expression[0] == '+')
            return TryEvaluateStaticNumberExpression(expression[1..], constants, out value);

        if (expression[0] == '-')
        {
            if (!TryEvaluateStaticNumberExpression(expression[1..], constants, out value))
                return false;

            value = -value;
            return true;
        }

        if (expression.StartsWith("abs", StringComparison.Ordinal) &&
            expression.Length > 3 &&
            !IsIdentifierPart(expression[3]))
        {
            int open = SkipTrivia(expression, 3);
            if (open < expression.Length && expression[open] == '(')
            {
                int close = FindMatchingDelimiter(expression, open, '(', ')');
                if (close == expression.Length - 1 &&
                    TryEvaluateStaticNumberExpression(expression[(open + 1)..close], constants, out value))
                {
                    value = Math.Abs(value);
                    return true;
                }
            }
        }

        if (IsIdentifier(expression) && constants.TryGetValue(expression, out value))
            return true;

        return TryParseStaticNumber(expression, out value);
    }

    private static bool TryParseStaticNumber(string value, out double parsed)
    {
        value = value.Trim().TrimEnd('u', 'U', 'f', 'F');
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
    }

    private static bool TrySplitTopLevel(string expression, string op, out string left, out string right)
    {
        int index = FindTopLevelOperator(expression, op);
        if (index < 0)
        {
            left = string.Empty;
            right = string.Empty;
            return false;
        }

        left = expression[..index];
        right = expression[(index + op.Length)..];
        return true;
    }

    private static int FindTopLevelOperator(string expression, string op)
    {
        int depth = 0;
        for (int i = 0; i <= expression.Length - op.Length; i++)
        {
            char c = expression[i];
            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth == 0 && expression.AsSpan(i).StartsWith(op.AsSpan(), StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static string StripOuterParentheses(string expression)
    {
        while (expression.Length >= 2 && expression[0] == '(')
        {
            int close = FindMatchingDelimiter(expression, 0, '(', ')');
            if (close != expression.Length - 1)
                break;

            expression = expression[1..^1].Trim();
        }

        return expression;
    }

    private static int FindMatchingDelimiter(string source, int openIndex, char open, char close)
    {
        int depth = 0;
        for (int i = openIndex; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '/' && i + 1 < source.Length)
            {
                if (source[i + 1] == '/')
                {
                    i = SkipLineComment(source, i + 2);
                    continue;
                }

                if (source[i + 1] == '*')
                {
                    i = SkipBlockComment(source, i + 2);
                    continue;
                }
            }

            if (c == '"')
            {
                i = SkipStringLiteral(source, i + 1);
                continue;
            }

            if (c == open)
                depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static int SkipTrivia(string source, int index)
    {
        while (index < source.Length)
        {
            if (char.IsWhiteSpace(source[index]))
            {
                index++;
                continue;
            }

            if (source[index] == '/' && index + 1 < source.Length)
            {
                if (source[index + 1] == '/')
                {
                    index = SkipLineComment(source, index + 2) + 1;
                    continue;
                }

                if (source[index + 1] == '*')
                {
                    index = SkipBlockComment(source, index + 2) + 1;
                    continue;
                }
            }

            break;
        }

        return index;
    }

    private static int SkipLineComment(string source, int index)
    {
        while (index < source.Length && source[index] != '\n')
            index++;

        return Math.Min(index, source.Length - 1);
    }

    private static int SkipBlockComment(string source, int index)
    {
        while (index + 1 < source.Length)
        {
            if (source[index] == '*' && source[index + 1] == '/')
                return index + 1;

            index++;
        }

        return source.Length - 1;
    }

    private static int SkipStringLiteral(string source, int index)
    {
        while (index < source.Length)
        {
            if (source[index] == '\\' && index + 1 < source.Length)
            {
                index += 2;
                continue;
            }

            if (source[index] == '"')
                return index;

            index++;
        }

        return source.Length - 1;
    }

    private static bool IsKeywordAt(string source, int index, string keyword)
    {
        if (index < 0 || index + keyword.Length > source.Length)
            return false;

        if (!source.AsSpan(index).StartsWith(keyword.AsSpan(), StringComparison.Ordinal))
            return false;

        bool leftBoundary = index == 0 || !IsIdentifierPart(source[index - 1]);
        int rightIndex = index + keyword.Length;
        bool rightBoundary = rightIndex >= source.Length || !IsIdentifierPart(source[rightIndex]);
        return leftBoundary && rightBoundary;
    }

    private struct PreprocessorExpressionParser
    {
        private readonly string _expression;
        private readonly IReadOnlySet<string> _knownMacros;
        private readonly IReadOnlySet<string> _definedMacros;
        private int _position;
        private bool _failed;

        public PreprocessorExpressionParser(
            string expression,
            IReadOnlySet<string> knownMacros,
            IReadOnlySet<string> definedMacros)
        {
            _expression = StripLineComment(expression);
            _knownMacros = knownMacros;
            _definedMacros = definedMacros;
        }

        public EKnownConditionValue Parse()
        {
            EKnownConditionValue value = ParseOr();
            SkipWhitespace();
            return _failed || _position < _expression.Length
                ? EKnownConditionValue.Unknown
                : value;
        }

        private EKnownConditionValue ParseOr()
        {
            EKnownConditionValue value = ParseAnd();
            while (TryConsume("||"))
                value = Or(value, ParseAnd());
            return value;
        }

        private EKnownConditionValue ParseAnd()
        {
            EKnownConditionValue value = ParseUnary();
            while (TryConsume("&&"))
                value = And(value, ParseUnary());
            return value;
        }

        private EKnownConditionValue ParseUnary()
        {
            if (TryConsume("!"))
                return Not(ParseUnary());

            return ParsePrimary();
        }

        private EKnownConditionValue ParsePrimary()
        {
            SkipWhitespace();
            if (_position >= _expression.Length)
            {
                _failed = true;
                return EKnownConditionValue.Unknown;
            }

            if (TryConsume("("))
            {
                EKnownConditionValue value = ParseOr();
                if (!TryConsume(")"))
                    _failed = true;
                return value;
            }

            if (TryReadIdentifier(out string identifier))
            {
                if (string.Equals(identifier, "defined", StringComparison.Ordinal))
                    return ParseDefinedOperator();

                if (!_knownMacros.Contains(identifier))
                    return EKnownConditionValue.Unknown;

                return _definedMacros.Contains(identifier)
                    ? EKnownConditionValue.True
                    : EKnownConditionValue.False;
            }

            if (TryReadNumber(out bool isNonZero))
                return isNonZero ? EKnownConditionValue.True : EKnownConditionValue.False;

            _failed = true;
            return EKnownConditionValue.Unknown;
        }

        private EKnownConditionValue ParseDefinedOperator()
        {
            SkipWhitespace();
            string macro;
            if (TryConsume("("))
            {
                if (!TryReadIdentifier(out macro) || !TryConsume(")"))
                {
                    _failed = true;
                    return EKnownConditionValue.Unknown;
                }
            }
            else if (!TryReadIdentifier(out macro))
            {
                _failed = true;
                return EKnownConditionValue.Unknown;
            }

            if (!_knownMacros.Contains(macro))
                return EKnownConditionValue.Unknown;

            return _definedMacros.Contains(macro)
                ? EKnownConditionValue.True
                : EKnownConditionValue.False;
        }

        private bool TryConsume(string token)
        {
            SkipWhitespace();
            ReadOnlySpan<char> tokenSpan = token.AsSpan();
            if (!_expression.AsSpan(_position).StartsWith(tokenSpan, StringComparison.Ordinal))
                return false;

            _position += token.Length;
            return true;
        }

        private bool TryReadIdentifier(out string identifier)
        {
            SkipWhitespace();
            identifier = string.Empty;
            if (_position >= _expression.Length || !IsIdentifierStart(_expression[_position]))
                return false;

            int start = _position++;
            while (_position < _expression.Length && IsIdentifierPart(_expression[_position]))
                _position++;

            identifier = _expression[start.._position];
            return true;
        }

        private bool TryReadNumber(out bool isNonZero)
        {
            SkipWhitespace();
            isNonZero = false;
            if (_position >= _expression.Length || !char.IsAsciiDigit(_expression[_position]))
                return false;

            bool sawNonZero = false;
            while (_position < _expression.Length && char.IsAsciiDigit(_expression[_position]))
            {
                sawNonZero |= _expression[_position] != '0';
                _position++;
            }

            isNonZero = sawNonZero;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_position < _expression.Length && char.IsWhiteSpace(_expression[_position]))
                _position++;
        }
    }
}
