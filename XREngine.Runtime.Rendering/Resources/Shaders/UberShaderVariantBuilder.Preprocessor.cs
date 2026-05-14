using System.Text;

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
