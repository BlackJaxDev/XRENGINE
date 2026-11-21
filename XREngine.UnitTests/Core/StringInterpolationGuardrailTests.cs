using System.Text;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Core;

/// <summary>
/// Enforces string interpolation across repository C# sources so multi-part
/// non-constant <c>+</c> concatenations and default-culture literal
/// <c>string.Format</c> calls do not return.
/// </summary>
[TestFixture]
public sealed class StringInterpolationGuardrailTests
{
    private const int MinimumFlaggedOperandCount = 4;

    [Test]
    public void RepositorySources_DoNotContainMultiPartNonInterpolatedStringConcatenations()
    {
        List<string> violations = [];

        foreach ((string relativePath, string source) in EnumerateFirstPartyCSharpFiles())
        {
            if (!source.Contains('+'))
                continue;

            CollectConcatenationViolations(relativePath, source, violations);
        }

        violations.ShouldBeEmpty(
            "Use C# string interpolation ($/$$) instead of 4+-operand non-constant '+' string concatenations.\n" +
            string.Join(Environment.NewLine, violations));
    }

    [Test]
    public void RepositorySources_DoNotContainDefaultCultureLiteralStringFormatCalls()
    {
        List<string> violations = [];

        foreach ((string relativePath, string source) in EnumerateFirstPartyCSharpFiles())
        {
            if (!source.Contains(".Format", StringComparison.Ordinal))
                continue;

            CollectLiteralStringFormatViolations(relativePath, source, violations);
        }

        violations.ShouldBeEmpty(
            "Use C# string interpolation ($/string.Create) instead of default-culture string.Format with a literal template.\n" +
            string.Join(Environment.NewLine, violations));
    }

    [Test]
    public void Scanner_FlagsDisallowedPatternsAndIgnoresAllowedForms()
    {
        const string sample = """"
            using System;
            using System.Globalization;

            namespace Sample;

            [Obsolete("Use " + nameof(SampleType) + " instead of " + LegacyName)]
            public sealed class SampleType
            {
                private const string LegacyName = "Old";
                private const string Sql = "SELECT " + LegacyName + " FROM " + "Items";

                public string AllowedMultiLineInterpolation(int a, int b, int c)
                    => "Header line " +
                       $"a={a} " +
                       $"b={b} " +
                       $"c={c}";

                public int AllowedNumericSum(int a, int b, int c, int d)
                    => a + b + c + d;

                public string AllowedInvariantFormat(double value)
                    => string.Format(CultureInfo.InvariantCulture, "{0:F2}", value);

                public string AllowedTemplateFormat(string template, int index)
                    => string.Format(template, index);

                public string DisallowedConcat(string target, string name, string value)
                    => "Register<" + target + ", " + name + ">(" + value + ")";

                public string DisallowedGenericCallConcat(int a, int b)
                    => "prefix=" + FormatPair<int, int>(a, b) + ", suffix=" + a;

                public string DisallowedNestedHoleConcat(string x, string y)
                    => $$"""{"msg":"{{"a=" + x + ", b=" + y}}"}""";

                public string DisallowedStringFormat(int second, float value)
                    => string.Format("{0} {1}", second, value);

                private static string FormatPair<TLeft, TRight>(TLeft left, TRight right)
                    => $"{left}:{right}";
            }
            """";

        List<string> concatViolations = [];
        CollectConcatenationViolations("Sample.cs", sample, concatViolations);
        concatViolations.Count.ShouldBe(3, string.Join(Environment.NewLine, concatViolations));
        concatViolations.ShouldContain(violation => violation.Contains("Sample.cs:28", StringComparison.Ordinal));
        concatViolations.ShouldContain(violation => violation.Contains("Sample.cs:31", StringComparison.Ordinal));
        concatViolations.ShouldContain(violation => violation.Contains("Sample.cs:34", StringComparison.Ordinal));

        List<string> formatViolations = [];
        CollectLiteralStringFormatViolations("Sample.cs", sample, formatViolations);
        formatViolations.Count.ShouldBe(1, string.Join(Environment.NewLine, formatViolations));
        formatViolations[0].ShouldContain("Sample.cs:37");
    }

    private static void CollectConcatenationViolations(
        string relativePath,
        string source,
        List<string> violations)
    {
        List<Token> tokens = [];
        TokenizeAndInspectHoles(
            relativePath,
            source,
            baseLine: 1,
            tokens,
            concatViolations: violations,
            formatViolations: null);
        InspectScopeForConcatenations(relativePath, tokens, 0, tokens.Count, violations);
    }

    private static void CollectLiteralStringFormatViolations(
        string relativePath,
        string source,
        List<string> violations)
    {
        List<Token> tokens = [];
        TokenizeAndInspectHoles(
            relativePath,
            source,
            baseLine: 1,
            tokens,
            concatViolations: null,
            formatViolations: violations);
        InspectTokensForLiteralStringFormat(relativePath, tokens, violations);
    }

    private static void InspectTokensForLiteralStringFormat(
        string relativePath,
        List<Token> tokens,
        List<string> violations)
    {
        for (int i = 0; i + 4 < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.Identifier ||
                (tokens[i].Text != "string" && tokens[i].Text != "String"))
            {
                continue;
            }

            if (tokens[i + 1].Kind != TokenKind.Dot ||
                tokens[i + 2].Kind != TokenKind.Identifier ||
                tokens[i + 2].Text != "Format" ||
                tokens[i + 3].Kind != TokenKind.OpenParen)
            {
                continue;
            }

            Token firstArgument = tokens[i + 4];
            if (firstArgument.Kind is TokenKind.StringLiteral or TokenKind.InterpolatedString)
                violations.Add($"{relativePath}:{tokens[i].Line} uses default-culture string.Format with a literal template.");
        }
    }

    private static void InspectScopeForConcatenations(
        string relativePath,
        List<Token> tokens,
        int start,
        int end,
        List<string> violations)
    {
        bool inConstDeclaration = false;
        List<OperandKind> chainOperands = [];
        int chainStartLine = 0;
        bool currentOperandHasTokens = false;
        int currentOperandTokenCount = 0;
        TokenKind currentSingleTokenKind = TokenKind.Other;
        bool lastTokenCanEndOperand = false;

        void FlushCurrentOperand()
        {
            if (!currentOperandHasTokens)
                return;

            OperandKind kind = currentOperandTokenCount == 1
                ? currentSingleTokenKind switch
                {
                    TokenKind.StringLiteral => OperandKind.StringLiteral,
                    TokenKind.InterpolatedString => OperandKind.InterpolatedString,
                    _ => OperandKind.Expression,
                }
                : OperandKind.Expression;
            chainOperands.Add(kind);
            currentOperandHasTokens = false;
            currentOperandTokenCount = 0;
            currentSingleTokenKind = TokenKind.Other;
        }

        void FlushChain()
        {
            FlushCurrentOperand();
            if (!inConstDeclaration && chainOperands.Count >= MinimumFlaggedOperandCount)
            {
                int stringLiterals = 0;
                int interpolatedStrings = 0;
                int expressions = 0;
                for (int k = 0; k < chainOperands.Count; k++)
                {
                    switch (chainOperands[k])
                    {
                        case OperandKind.StringLiteral:
                            stringLiterals++;
                            break;
                        case OperandKind.InterpolatedString:
                            interpolatedStrings++;
                            break;
                        default:
                            expressions++;
                            break;
                    }
                }

                if ((stringLiterals + interpolatedStrings) > 0 && expressions > 0)
                {
                    violations.Add(
                        $"{relativePath}:{chainStartLine} has a {chainOperands.Count}-operand '+' string concatenation " +
                        $"(literals={stringLiterals}, interpolated={interpolatedStrings}, expressions={expressions}).");
                }
            }

            chainOperands.Clear();
            chainStartLine = 0;
            lastTokenCanEndOperand = false;
        }

        void RecordOperandToken(Token token, bool canEndOperand)
        {
            if (!currentOperandHasTokens)
            {
                currentOperandHasTokens = true;
                if (chainOperands.Count == 0)
                    chainStartLine = token.Line;
            }

            currentOperandTokenCount++;
            currentSingleTokenKind = currentOperandTokenCount == 1 ? token.Kind : TokenKind.Other;
            lastTokenCanEndOperand = canEndOperand;
        }

        int i = start;
        while (i < end)
        {
            Token token = tokens[i];

            if (token.Kind == TokenKind.Identifier && token.Text == "const")
            {
                FlushChain();
                inConstDeclaration = true;
                i++;
                continue;
            }

            if (token.Kind == TokenKind.Semicolon)
            {
                FlushChain();
                inConstDeclaration = false;
                i++;
                continue;
            }

            if (token.Kind is TokenKind.OpenBrace or TokenKind.OpenParen or TokenKind.OpenBracket)
            {
                int matchingClose = FindMatchingBracket(tokens, i, end);
                if (token.Kind == TokenKind.OpenBrace)
                {
                    FlushChain();
                    inConstDeclaration = false;
                    InspectScopeForConcatenations(relativePath, tokens, i + 1, matchingClose, violations);
                }
                else if (token.Kind == TokenKind.OpenBracket && IsAttributeBracket(tokens, start, i, matchingClose, end))
                {
                    FlushChain();
                }
                else
                {
                    InspectScopeForConcatenations(relativePath, tokens, i + 1, matchingClose, violations);
                    RecordOperandToken(token, canEndOperand: true);
                    currentOperandTokenCount++;
                }

                i = matchingClose + 1;
                continue;
            }

            if (token.Kind == TokenKind.LessThan &&
                lastTokenCanEndOperand &&
                TryFindGenericArgumentClose(tokens, i, end, out int genericClose))
            {
                RecordOperandToken(token, canEndOperand: true);
                currentOperandTokenCount++;
                i = genericClose + 1;
                continue;
            }

            if (token.Kind == TokenKind.Plus)
            {
                if (lastTokenCanEndOperand && currentOperandHasTokens)
                {
                    FlushCurrentOperand();
                    lastTokenCanEndOperand = false;
                }
                else
                {
                    RecordOperandToken(token, canEndOperand: false);
                }

                i++;
                continue;
            }

            if (IsChainBoundary(token))
            {
                FlushChain();
                i++;
                continue;
            }

            bool canEnd = token.Kind is TokenKind.StringLiteral or TokenKind.InterpolatedString or TokenKind.Literal ||
                (token.Kind == TokenKind.Identifier && !IsKeywordPrefixOperator(token.Text));
            RecordOperandToken(token, canEndOperand: canEnd);
            i++;
        }

        FlushChain();
    }

    private static bool IsChainBoundary(Token token)
        => token.Kind is TokenKind.Comma or TokenKind.ChainDelimiter ||
           (token.Kind == TokenKind.Identifier && token.Text is "return" or "throw" or "case" or "when" or "yield" or "in" or "is" or "as" or "switch" or "with");

    private static bool IsKeywordPrefixOperator(string identifier)
        => identifier is "new" or "typeof" or "nameof" or "sizeof" or "await" or "checked" or "unchecked" or "default" or "stackalloc" or "ref" or "out";

    private static bool IsAttributeBracket(
        List<Token> tokens,
        int scopeStart,
        int openBracketIndex,
        int closeBracketIndex,
        int scopeEnd)
    {
        if (openBracketIndex > scopeStart)
        {
            Token previous = tokens[openBracketIndex - 1];
            if (previous.Kind is TokenKind.Identifier or TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.Literal or TokenKind.StringLiteral or TokenKind.InterpolatedString)
                return false;
        }

        if (closeBracketIndex + 1 >= scopeEnd)
            return false;

        Token afterClose = tokens[closeBracketIndex + 1];
        return afterClose.Kind is TokenKind.OpenBracket or TokenKind.Identifier;
    }

    private static int FindMatchingBracket(List<Token> tokens, int openIndex, int limit)
    {
        TokenKind openKind = tokens[openIndex].Kind;
        TokenKind closeKind = openKind switch
        {
            TokenKind.OpenParen => TokenKind.CloseParen,
            TokenKind.OpenBracket => TokenKind.CloseBracket,
            _ => TokenKind.CloseBrace,
        };

        int depth = 1;
        for (int i = openIndex + 1; i < limit; i++)
        {
            if (tokens[i].Kind == openKind)
            {
                depth++;
            }
            else if (tokens[i].Kind == closeKind)
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return limit - 1;
    }

    private static bool TryFindGenericArgumentClose(
        List<Token> tokens,
        int openAngleIndex,
        int limit,
        out int closeAngleIndex)
    {
        int depth = 1;
        for (int i = openAngleIndex + 1; i < limit; i++)
        {
            TokenKind kind = tokens[i].Kind;
            if (kind == TokenKind.LessThan)
            {
                depth++;
            }
            else if (kind == TokenKind.GreaterThan)
            {
                depth--;
                if (depth == 0)
                {
                    closeAngleIndex = i;
                    if (i + 1 >= limit)
                        return false;

                    Token next = tokens[i + 1];
                    return next.Kind is TokenKind.OpenParen or TokenKind.Dot or TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.Comma or TokenKind.Semicolon;
                }
            }
            else if (kind is TokenKind.Identifier or TokenKind.Dot or TokenKind.Comma or TokenKind.OpenBracket or TokenKind.CloseBracket)
            {
                continue;
            }
            else if (kind == TokenKind.ChainDelimiter && tokens[i].Text == "?")
            {
                continue;
            }
            else
            {
                break;
            }
        }

        closeAngleIndex = -1;
        return false;
    }

    /// <summary>
    /// Tokenizes C# source into lexical tokens while recursively inspecting expressions inside interpolated string holes.
    /// </summary>
    private static void TokenizeAndInspectHoles(
        string relativePath,
        string source,
        int baseLine,
        List<Token> tokens,
        List<string>? concatViolations,
        List<string>? formatViolations)
    {
        int line = baseLine;
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                i += 2;
                while (i < source.Length && source[i] != '\n')
                    i++;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    if (source[i] == '\n')
                        line++;
                    i++;
                }

                i = Math.Min(source.Length, i + 2);
                continue;
            }

            if (c == '#' && IsPreprocessorLineStart(source, i))
            {
                while (i < source.Length && source[i] != '\n')
                    i++;
                continue;
            }

            if (c == '\'')
            {
                int tokenLine = line;
                i = ScanCharLiteral(source, i, ref line);
                tokens.Add(new Token(TokenKind.Literal, "'c'", tokenLine));
                continue;
            }

            if (TryMatchStringPrefix(source, i, out int dollarCount, out bool isVerbatim, out int quoteStart, out int quoteCount))
            {
                int stringLine = line;
                i = ScanStringAndInspectHoles(
                    relativePath,
                    source,
                    dollarCount,
                    isVerbatim,
                    quoteStart,
                    quoteCount,
                    ref line,
                    concatViolations,
                    formatViolations);
                tokens.Add(new Token(
                    dollarCount > 0 ? TokenKind.InterpolatedString : TokenKind.StringLiteral,
                    dollarCount > 0 ? "$\"\"" : "\"\"",
                    stringLine));
                continue;
            }

            if (char.IsLetter(c) || c == '_' || (c == '@' && i + 1 < source.Length && (char.IsLetter(source[i + 1]) || source[i + 1] == '_')))
            {
                int idStart = c == '@' ? i + 1 : i;
                i = idStart + 1;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                    i++;
                tokens.Add(new Token(TokenKind.Identifier, source[idStart..i], line));
                continue;
            }

            if (char.IsDigit(c))
            {
                int numLine = line;
                i++;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] is '_' or '.'))
                {
                    if (source[i] == '.' && (i + 1 >= source.Length || !char.IsDigit(source[i + 1])))
                        break;
                    i++;
                }

                tokens.Add(new Token(TokenKind.Literal, "0", numLine));
                continue;
            }

            switch (c)
            {
                case '(':
                    tokens.Add(new Token(TokenKind.OpenParen, "(", line));
                    i++;
                    break;
                case ')':
                    tokens.Add(new Token(TokenKind.CloseParen, ")", line));
                    i++;
                    break;
                case '[':
                    tokens.Add(new Token(TokenKind.OpenBracket, "[", line));
                    i++;
                    break;
                case ']':
                    tokens.Add(new Token(TokenKind.CloseBracket, "]", line));
                    i++;
                    break;
                case '{':
                    tokens.Add(new Token(TokenKind.OpenBrace, "{", line));
                    i++;
                    break;
                case '}':
                    tokens.Add(new Token(TokenKind.CloseBrace, "}", line));
                    i++;
                    break;
                case ';':
                    tokens.Add(new Token(TokenKind.Semicolon, ";", line));
                    i++;
                    break;
                case ',':
                    tokens.Add(new Token(TokenKind.Comma, ",", line));
                    i++;
                    break;
                case '.':
                    tokens.Add(new Token(TokenKind.Dot, ".", line));
                    i++;
                    break;
                case '+':
                    if (i + 1 < source.Length && source[i + 1] == '+')
                    {
                        tokens.Add(new Token(TokenKind.Other, "++", line));
                        i += 2;
                    }
                    else if (i + 1 < source.Length && source[i + 1] == '=')
                    {
                        tokens.Add(new Token(TokenKind.ChainDelimiter, "+=", line));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token(TokenKind.Plus, "+", line));
                        i++;
                    }

                    break;
                case '<':
                    if (i + 1 < source.Length && source[i + 1] is '=' or '<')
                    {
                        tokens.Add(new Token(TokenKind.Other, "<=", line));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token(TokenKind.LessThan, "<", line));
                        i++;
                    }

                    break;
                case '>':
                    if (i + 1 < source.Length && source[i + 1] == '=')
                    {
                        tokens.Add(new Token(TokenKind.Other, ">=", line));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token(TokenKind.GreaterThan, ">", line));
                        i++;
                    }

                    break;
                case '=':
                    if (i + 1 < source.Length && source[i + 1] is '=' or '>')
                    {
                        tokens.Add(new Token(TokenKind.ChainDelimiter, source.Substring(i, 2), line));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token(TokenKind.ChainDelimiter, "=", line));
                        i++;
                    }

                    break;
                case '!':
                    if (i + 1 < source.Length && source[i + 1] == '=')
                    {
                        tokens.Add(new Token(TokenKind.ChainDelimiter, "!=", line));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token(TokenKind.Other, "!", line));
                        i++;
                    }

                    break;
                case '&':
                    if (i + 1 < source.Length && source[i + 1] is '&' or '=')
                    {
                        tokens.Add(new Token(TokenKind.ChainDelimiter, source.Substring(i, 2), line));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token(TokenKind.Other, "&", line));
                        i++;
                    }

                    break;
                case '|':
                    if (i + 1 < source.Length && source[i + 1] is '|' or '=')
                    {
                        tokens.Add(new Token(TokenKind.ChainDelimiter, source.Substring(i, 2), line));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token(TokenKind.Other, "|", line));
                        i++;
                    }

                    break;
                case '?':
                    if (i + 1 < source.Length && source[i + 1] == '?')
                    {
                        if (i + 2 < source.Length && source[i + 2] == '=')
                        {
                            tokens.Add(new Token(TokenKind.ChainDelimiter, "??=", line));
                            i += 3;
                        }
                        else
                        {
                            tokens.Add(new Token(TokenKind.ChainDelimiter, "??", line));
                            i += 2;
                        }
                    }
                    else if (i + 1 < source.Length && source[i + 1] is '.' or '[')
                    {
                        tokens.Add(new Token(TokenKind.Dot, "?.", line));
                        i += source[i + 1] == '.' ? 2 : 1;
                    }
                    else
                    {
                        // Nullable annotations inside generic arguments stay as '?' while ternary '?' bounds '+' chains.
                        tokens.Add(new Token(TokenKind.ChainDelimiter, "?", line));
                        i++;
                    }

                    break;
                case ':':
                    if (i + 1 < source.Length && source[i + 1] == ':')
                    {
                        tokens.Add(new Token(TokenKind.Dot, "::", line));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token(TokenKind.ChainDelimiter, ":", line));
                        i++;
                    }

                    break;
                default:
                    tokens.Add(new Token(TokenKind.Other, c.ToString(), line));
                    i++;
                    break;
            }
        }
    }

    private static bool IsPreprocessorLineStart(string source, int hashIndex)
    {
        for (int i = hashIndex - 1; i >= 0 && source[i] != '\n'; i--)
        {
            if (!char.IsWhiteSpace(source[i]))
                return false;
        }

        return true;
    }

    private static int ScanCharLiteral(string source, int start, ref int line)
    {
        int i = start + 1;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '\n')
                line++;
            if (c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == '\'')
                return i + 1;

            i++;
        }

        return source.Length;
    }

    private static bool TryMatchStringPrefix(
        string source,
        int start,
        out int dollarCount,
        out bool isVerbatim,
        out int quoteStart,
        out int quoteCount)
    {
        dollarCount = 0;
        isVerbatim = false;
        quoteStart = -1;
        quoteCount = 0;

        int i = start;
        if (i < source.Length && source[i] == '@')
        {
            isVerbatim = true;
            i++;
        }

        while (i < source.Length && source[i] == '$')
        {
            dollarCount++;
            i++;
        }

        if (!isVerbatim && i < source.Length && source[i] == '@')
        {
            isVerbatim = true;
            i++;
        }

        if (i >= source.Length || source[i] != '"')
            return false;

        if (isVerbatim && dollarCount > 1)
            return false;

        quoteStart = i;
        while (i < source.Length && source[i] == '"')
        {
            quoteCount++;
            i++;
        }

        if (quoteCount == 2)
            quoteCount = isVerbatim || dollarCount <= 1 ? 1 : 0;

        if (isVerbatim && quoteCount >= 3)
            quoteCount = 1;

        return quoteCount >= 1;
    }

    private static int ScanStringAndInspectHoles(
        string relativePath,
        string source,
        int dollarCount,
        bool isVerbatim,
        int quoteStart,
        int quoteCount,
        ref int line,
        List<string>? concatViolations,
        List<string>? formatViolations)
    {
        bool isRaw = quoteCount >= 3;
        int i = isRaw ? quoteStart + quoteCount : quoteStart + 1;

        while (i < source.Length)
        {
            char c = source[i];
            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            if (isRaw)
            {
                if (c == '"')
                {
                    int run = CountRepeatedChar(source, i, '"');
                    if (run >= quoteCount)
                        return SkipUtf8Suffix(source, i + run);

                    i += run;
                    continue;
                }

                if (dollarCount > 0 && c == '{')
                {
                    int run = CountRepeatedChar(source, i, '{');
                    if (run >= dollarCount)
                    {
                        int holeExprStart = i + run;
                        int holeLine = line;
                        i = ScanInterpolationHole(
                            relativePath,
                            source,
                            holeExprStart,
                            dollarCount,
                            ref line,
                            holeLine,
                            concatViolations,
                            formatViolations);
                        continue;
                    }

                    i += run;
                    continue;
                }

                i++;
                continue;
            }

            if (!isVerbatim && c == '\\')
            {
                if (i + 1 < source.Length && source[i + 1] == '\n')
                    line++;
                i = Math.Min(source.Length, i + 2);
                continue;
            }

            if (c == '"')
            {
                if (isVerbatim && i + 1 < source.Length && source[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return SkipUtf8Suffix(source, i + 1);
            }

            if (dollarCount > 0 && c == '{')
            {
                if (i + 1 < source.Length && source[i + 1] == '{')
                {
                    i += 2;
                    continue;
                }

                int holeExprStart = i + 1;
                int holeLine = line;
                i = ScanInterpolationHole(
                    relativePath,
                    source,
                    holeExprStart,
                    closingBraceCount: 1,
                    ref line,
                    holeLine,
                    concatViolations,
                    formatViolations);
                continue;
            }

            i++;
        }

        return source.Length;
    }

    private static int ScanInterpolationHole(
        string relativePath,
        string source,
        int holeExprStart,
        int closingBraceCount,
        ref int line,
        int holeStartLine,
        List<string>? concatViolations,
        List<string>? formatViolations)
    {
        int i = holeExprStart;
        int innerBraceDepth = 0;
        int parenDepth = 0;
        int bracketDepth = 0;
        int exprEnd = -1;
        bool inFormatSpecifier = false;

        while (i < source.Length)
        {
            char c = source[i];
            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            if (inFormatSpecifier)
            {
                if (closingBraceCount == 1)
                {
                    if (c == '}' && i + 1 < source.Length && source[i + 1] == '}')
                    {
                        i += 2;
                        continue;
                    }

                    if (c == '}')
                    {
                        i++;
                        break;
                    }
                }
                else if (c == '}')
                {
                    int run = CountRepeatedChar(source, i, '}');
                    if (run >= closingBraceCount)
                    {
                        i += closingBraceCount;
                        break;
                    }

                    i += run;
                    continue;
                }

                i++;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                i += 2;
                while (i < source.Length && source[i] != '\n')
                    i++;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    if (source[i] == '\n')
                        line++;
                    i++;
                }

                i = Math.Min(source.Length, i + 2);
                continue;
            }

            if (c == '\'')
            {
                i = ScanCharLiteral(source, i, ref line);
                continue;
            }

            if (TryMatchStringPrefix(source, i, out int nestedDollars, out bool nestedVerbatim, out int nestedQuoteStart, out int nestedQuotes))
            {
                i = ScanStringAndInspectHoles(
                    relativePath,
                    source,
                    nestedDollars,
                    nestedVerbatim,
                    nestedQuoteStart,
                    nestedQuotes,
                    ref line,
                    concatViolations,
                    formatViolations);
                continue;
            }

            if (c == '(')
            {
                parenDepth++;
                i++;
                continue;
            }

            if (c == ')' && parenDepth > 0)
            {
                parenDepth--;
                i++;
                continue;
            }

            if (c == '[')
            {
                bracketDepth++;
                i++;
                continue;
            }

            if (c == ']' && bracketDepth > 0)
            {
                bracketDepth--;
                i++;
                continue;
            }

            if (c == ':' && innerBraceDepth == 0 && parenDepth == 0 && bracketDepth == 0 &&
                !(i + 1 < source.Length && source[i + 1] == ':'))
            {
                exprEnd = i;
                inFormatSpecifier = true;
                i++;
                continue;
            }

            if (c == '{')
            {
                innerBraceDepth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                if (innerBraceDepth > 0)
                {
                    innerBraceDepth--;
                    i++;
                    continue;
                }

                int run = CountRepeatedChar(source, i, '}');
                if (run >= closingBraceCount)
                {
                    exprEnd = i;
                    i += closingBraceCount;
                    break;
                }

                i += run;
                continue;
            }

            i++;
        }

        if (exprEnd < 0)
            exprEnd = Math.Min(i, source.Length);

        if (exprEnd > holeExprStart && (concatViolations is not null || formatViolations is not null))
        {
            string holeSource = source[holeExprStart..exprEnd];
            List<Token> holeTokens = [];
            TokenizeAndInspectHoles(
                relativePath,
                holeSource,
                holeStartLine,
                holeTokens,
                concatViolations,
                formatViolations);
            if (concatViolations is not null)
                InspectScopeForConcatenations(relativePath, holeTokens, 0, holeTokens.Count, concatViolations);
            if (formatViolations is not null)
                InspectTokensForLiteralStringFormat(relativePath, holeTokens, formatViolations);
        }

        return i;
    }

    private static int CountRepeatedChar(string source, int start, char expected)
    {
        int count = 0;
        while (start + count < source.Length && source[start + count] == expected)
            count++;
        return count;
    }

    private static int SkipUtf8Suffix(string source, int index)
        => index + 1 < source.Length &&
           (source[index] is 'u' or 'U') &&
           source[index + 1] == '8'
            ? index + 2
            : index;

    private static IEnumerable<(string RelativePath, string Source)> EnumerateFirstPartyCSharpFiles()
    {
        string repositoryRoot = FindRepositoryRoot();
        Stack<string> pending = new();
        pending.Push(repositoryRoot);

        List<string> files = [];
        while (pending.TryPop(out string? directory))
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly))
            {
                if (file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                files.Add(file);
            }

            foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(child);
                bool isTopLevel = string.Equals(directory, repositoryRoot, StringComparison.OrdinalIgnoreCase);
                if ((isTopLevel && (name.Equals("Build", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("ThirdParty", StringComparison.OrdinalIgnoreCase))) ||
                    name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("_AgentValidation", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Submodules", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".codex", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".agents", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                pending.Push(child);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            string relativePath = Path.GetRelativePath(repositoryRoot, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            yield return (relativePath, File.ReadAllText(file, Encoding.UTF8));
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the XRENGINE repository root from '{AppContext.BaseDirectory}'.");
    }

    private enum TokenKind
    {
        StringLiteral,
        InterpolatedString,
        Identifier,
        Literal,
        OpenParen,
        CloseParen,
        OpenBracket,
        CloseBracket,
        OpenBrace,
        CloseBrace,
        Plus,
        Comma,
        Semicolon,
        Dot,
        LessThan,
        GreaterThan,
        ChainDelimiter,
        Other,
    }

    private enum OperandKind
    {
        StringLiteral,
        InterpolatedString,
        Expression,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Line);
}
