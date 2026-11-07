using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

// Tokenizer and parser for the script text.
public sealed partial class RenderPipelineScript
{
    private sealed partial class Parser
    {
        private readonly List<Token> _tokens;
        private readonly List<ScriptComment> _markerComments = [];
        private int _index;

        public Parser(string script)
            => _tokens = Tokenize(script, _markerComments);

        /// <summary>Gets each marker comment, such as <c>// unconverted:</c> or <c>// base-hash:</c>, in source order.</summary>
        public IReadOnlyList<ScriptComment> MarkerComments => _markerComments;

        public IReadOnlyList<IScriptStep> ParseScript()
        {
            List<IScriptStep> steps = [];
            while (!Is(TokenKind.EndOfFile))
                steps.Add(ParseStatement());
            return steps;
        }

        private IScriptStep ParseStatement()
        {
            if (IsKeyword("set"))
            {
                Advance();
                return ParseSetStatement();
            }

            if (IsKeyword("clear") && Peek(1).Kind != TokenKind.OpenParen)
            {
                Advance();
                return ParseClearStatement();
            }

            if (IsKeyword("if"))
            {
                Advance();
                return ParseIfStatement();
            }

            if (IsKeyword("switch"))
            {
                Advance();
                return ParseSwitchStatement();
            }

            if (IsKeyword("repeat"))
            {
                Advance();
                return ParseRepeatStatement();
            }

            if (IsKeyword("when"))
            {
                Advance();
                return ParseConditionalRenderStatement();
            }

            if (IsKeyword("foreach_cascade"))
            {
                Advance();
                return ParseForEachCascadeStatement();
            }

            return ParseCommandStatement();
        }

        private IScriptStep ParseSetStatement()
        {
            string variableName = Consume(TokenKind.Identifier, "Expected variable name after 'set'.").Text;
            Consume(TokenKind.Equals, "Expected '=' in set statement.");
            IExpressionNode expression = ParseExpression();
            Consume(TokenKind.Semicolon, "Expected ';' after set statement.");

            if (TryBuildConstantVariableStep(variableName, expression, out IScriptStep? step))
                return step;

            return new ExpressionSetVariableStep(variableName, expression);
        }

        private static bool TryBuildConstantVariableStep(string variableName, IExpressionNode expression, out IScriptStep step)
        {
            try
            {
                object? value = expression.GetConstantValue();
                step = new SetVariableStep(variableName, cmd => cmd.SetLiteralValue(value));
                return true;
            }
            catch
            {
                step = null!;
                return false;
            }
        }

        private IScriptStep ParseClearStatement()
        {
            string variableName = Consume(TokenKind.Identifier, "Expected variable name after 'clear'.").Text;
            Consume(TokenKind.Semicolon, "Expected ';' after clear statement.");
            return new SetVariableStep(variableName, cmd => cmd.ClearVariable = true);
        }

        private IScriptStep ParseIfStatement()
        {
            Consume(TokenKind.OpenParen, "Expected '(' after 'if'.");
            Func<bool>? evaluator = null;
            if (!TryParseDataCondition(forSwitch: false, out RenderBranchCondition? dataCondition))
            {
                IExpressionNode condition = ParseExpression();
                evaluator = () => ScriptRuntimeContext.ToBoolean(condition.Evaluate(ScriptRuntimeContext.CreateActive()));
            }

            Consume(TokenKind.CloseParen, "Expected ')' after if condition.");
            string? label = ParseBranchLabel();
            BranchDefinition trueBranch = ParseBranchDefinition();
            BranchDefinition? falseBranch = null;
            if (MatchKeyword("else"))
                falseBranch = ParseBranchDefinition();

            return new IfStep(evaluator, dataCondition, label, trueBranch, falseBranch, null);
        }

        private IScriptStep ParseSwitchStatement()
        {
            Consume(TokenKind.OpenParen, "Expected '(' after 'switch'.");
            Func<int>? selector = null;
            if (!TryParseDataCondition(forSwitch: true, out RenderBranchCondition? dataCondition))
            {
                IExpressionNode selectorExpression = ParseExpression();
                selector = () => Convert.ToInt32(selectorExpression.Evaluate(ScriptRuntimeContext.CreateActive()), CultureInfo.InvariantCulture);
            }

            Consume(TokenKind.CloseParen, "Expected ')' after switch selector.");
            string? label = ParseBranchLabel();
            Consume(TokenKind.OpenBrace, "Expected '{' to begin switch body.");

            Dictionary<int, BranchDefinition> cases = [];
            BranchDefinition? defaultCase = null;
            while (!Match(TokenKind.CloseBrace))
            {
                if (MatchKeyword("case"))
                {
                    int value = Convert.ToInt32(ParseExpression().GetConstantValue(typeof(int)), CultureInfo.InvariantCulture);
                    Consume(TokenKind.Colon, "Expected ':' after switch case value.");
                    cases[value] = ParseBranchDefinition(requireBraces: true);
                }
                else if (MatchKeyword("default"))
                {
                    Consume(TokenKind.Colon, "Expected ':' after switch default.");
                    defaultCase = ParseBranchDefinition(requireBraces: true);
                }
                else
                {
                    throw Error(Peek(), "Expected 'case' or 'default' in switch block.");
                }
            }

            return new SwitchStep(selector, dataCondition, label, cases, defaultCase, null);
        }

        private IScriptStep ParseCommandStatement()
        {
            Token nameToken = ConsumeCommandName("Expected render command method name.");
            List<PropertyAssignment> properties = ParsePropertyAssignments();
            Consume(TokenKind.Semicolon, "Expected ';' after command statement.");
            return new CommandTypeStep(nameToken.Text, properties, nameToken.Line, nameToken.Column);
        }

        private IScriptStep ParseRepeatStatement()
        {
            Consume(TokenKind.OpenParen, "Expected '(' after 'repeat'.");
            IExpressionNode countExpression = ParseExpression();
            Consume(TokenKind.CloseParen, "Expected ')' after repeat count.");
            return new RepeatStep(countExpression, ParseBranchDefinition());
        }

        private IScriptStep ParseConditionalRenderStatement()
        {
            List<PropertyAssignment> properties = ParsePropertyAssignments();
            return new ConditionalRenderStep(properties, ParseBranchDefinition());
        }

        private IScriptStep ParseForEachCascadeStatement()
        {
            List<PropertyAssignment> properties = ParsePropertyAssignments();
            return new ForEachCascadeStep(properties, ParseBranchDefinition());
        }

        private BranchDefinition ParseBranchDefinition(bool requireBraces = true)
        {
            if (requireBraces)
                Consume(TokenKind.OpenBrace, "Expected '{' to begin branch body.");
            else if (!Match(TokenKind.OpenBrace))
                throw Error(Peek(), "Expected '{' to begin branch body.");

            List<IScriptStep> steps = [];
            while (!Match(TokenKind.CloseBrace))
                steps.Add(ParseStatement());
            return new BranchDefinition(steps, ViewportRenderCommandContainer.BranchResourceBehavior.PreserveResources);
        }

        private IValueNode ParseValue()
        {
            if (Match(TokenKind.OpenBracket))
            {
                List<IValueNode> items = [];
                if (!Match(TokenKind.CloseBracket))
                {
                    do items.Add(ParseValue());
                    while (Match(TokenKind.Comma));
                    Consume(TokenKind.CloseBracket, "Expected ']' after list literal.");
                }
                return new ArrayNode(items);
            }

            Token token = Peek();
            if (token.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.OpenParen)
            {
                string constructorName = Advance().Text;
                Consume(TokenKind.OpenParen, "Expected '(' after constructor name.");
                List<IValueNode> args = [];
                List<KeyValuePair<string, IValueNode>>? namedArgs = null;
                if (!Match(TokenKind.CloseParen))
                {
                    do
                    {
                        if (Peek().Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Equals)
                        {
                            string argumentName = Advance().Text;
                            Advance();
                            (namedArgs ??= []).Add(new KeyValuePair<string, IValueNode>(argumentName, ParseValue()));
                        }
                        else
                        {
                            args.Add(ParseValue());
                        }
                    }
                    while (Match(TokenKind.Comma));
                    Consume(TokenKind.CloseParen, "Expected ')' after constructor arguments.");
                }
                return new ConstructorNode(constructorName, args, namedArgs);
            }

            return ParseExpression();
        }

        private List<PropertyAssignment> ParsePropertyAssignments()
        {
            List<PropertyAssignment> properties = [];
            if (!Match(TokenKind.OpenParen))
                return properties;

            if (!Match(TokenKind.CloseParen))
            {
                do
                {
                    Token propertyToken = Consume(TokenKind.Identifier, "Expected command property name.");
                    Consume(TokenKind.Equals, "Expected '=' after command property name.");
                    IValueNode value = ParseValue();
                    properties.Add(new PropertyAssignment(propertyToken.Text, value, propertyToken.Line, propertyToken.Column));
                }
                while (Match(TokenKind.Comma));

                Consume(TokenKind.CloseParen, "Expected ')' after command property list.");
            }

            return properties;
        }

        private IExpressionNode ParseExpression()
            => ParseLogicalOr();

        private IExpressionNode ParseLogicalOr()
        {
            IExpressionNode expr = ParseLogicalAnd();
            while (Match(TokenKind.OrOr))
                expr = new BinaryExpressionNode("||", expr, ParseLogicalAnd());
            return expr;
        }

        private IExpressionNode ParseLogicalAnd()
        {
            IExpressionNode expr = ParseEquality();
            while (Match(TokenKind.AndAnd))
                expr = new BinaryExpressionNode("&&", expr, ParseEquality());
            return expr;
        }

        private IExpressionNode ParseEquality()
        {
            IExpressionNode expr = ParseComparison();
            while (true)
            {
                if (Match(TokenKind.EqualEqual))
                    expr = new BinaryExpressionNode("==", expr, ParseComparison());
                else if (Match(TokenKind.BangEqual))
                    expr = new BinaryExpressionNode("!=", expr, ParseComparison());
                else
                    break;
            }
            return expr;
        }

        private IExpressionNode ParseComparison()
        {
            IExpressionNode expr = ParseTerm();
            while (true)
            {
                if (Match(TokenKind.Less))
                    expr = new BinaryExpressionNode("<", expr, ParseTerm());
                else if (Match(TokenKind.LessEqual))
                    expr = new BinaryExpressionNode("<=", expr, ParseTerm());
                else if (Match(TokenKind.Greater))
                    expr = new BinaryExpressionNode(">", expr, ParseTerm());
                else if (Match(TokenKind.GreaterEqual))
                    expr = new BinaryExpressionNode(">=", expr, ParseTerm());
                else
                    break;
            }
            return expr;
        }

        private IExpressionNode ParseTerm()
        {
            IExpressionNode expr = ParseFactor();
            while (true)
            {
                if (Match(TokenKind.Plus))
                    expr = new BinaryExpressionNode("+", expr, ParseFactor());
                else if (Match(TokenKind.Minus))
                    expr = new BinaryExpressionNode("-", expr, ParseFactor());
                else
                    break;
            }
            return expr;
        }

        private IExpressionNode ParseFactor()
        {
            IExpressionNode expr = ParseUnary();
            while (true)
            {
                if (Match(TokenKind.Star))
                    expr = new BinaryExpressionNode("*", expr, ParseUnary());
                else if (Match(TokenKind.Slash))
                    expr = new BinaryExpressionNode("/", expr, ParseUnary());
                else
                    break;
            }
            return expr;
        }

        private IExpressionNode ParseUnary()
        {
            if (Match(TokenKind.Bang))
                return new UnaryExpressionNode("!", ParseUnary());
            if (Match(TokenKind.Minus))
                return new UnaryExpressionNode("-", ParseUnary());
            if (Match(TokenKind.Plus))
                return new UnaryExpressionNode("+", ParseUnary());
            return ParsePostfix();
        }

        private IExpressionNode ParsePostfix()
        {
            IExpressionNode expression = ParsePrimary();
            while (Match(TokenKind.OpenBracket))
            {
                IExpressionNode index = ParseExpression();
                Consume(TokenKind.CloseBracket, "Expected ']' after index expression.");
                expression = new IndexExpressionNode(expression, index);
            }

            return expression;
        }

        private IExpressionNode ParsePrimary()
        {
            if (Match(TokenKind.OpenParen))
            {
                IExpressionNode expr = ParseExpression();
                Consume(TokenKind.CloseParen, "Expected ')' after expression.");
                return expr;
            }

            Token token = Advance();
            return token.Kind switch
            {
                TokenKind.Number => new LiteralNode(ParseNumericLiteral(token.Text)),
                TokenKind.String => new LiteralNode(token.Text),
                TokenKind.Keyword when string.Equals(token.Text, "true", StringComparison.OrdinalIgnoreCase) => new LiteralNode(true),
                TokenKind.Keyword when string.Equals(token.Text, "false", StringComparison.OrdinalIgnoreCase) => new LiteralNode(false),
                TokenKind.Keyword when string.Equals(token.Text, "null", StringComparison.OrdinalIgnoreCase) => new LiteralNode(null),
                TokenKind.Identifier => new IdentifierNode(token.Text),
                _ => throw Error(token, "Unexpected token in expression.")
            };
        }

        private static object ParseNumericLiteral(string text)
        {
            if (text.EndsWith("u", StringComparison.OrdinalIgnoreCase))
                return uint.Parse(text[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (text.Contains('.') || text.Contains('e', StringComparison.OrdinalIgnoreCase))
                return float.Parse(text.TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture);
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
                return intValue;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
                return longValue;
            return ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        private bool MatchKeyword(string keyword)
        {
            if (Peek().Kind != TokenKind.Keyword || !string.Equals(Peek().Text, keyword, StringComparison.OrdinalIgnoreCase))
                return false;

            _index++;
            return true;
        }

        private bool IsKeyword(string keyword)
            => Peek().Kind == TokenKind.Keyword && string.Equals(Peek().Text, keyword, StringComparison.OrdinalIgnoreCase);

        private bool Match(TokenKind kind)
        {
            if (!Is(kind))
                return false;
            _index++;
            return true;
        }

        private bool Is(TokenKind kind)
            => Peek().Kind == kind;

        private Token Consume(TokenKind kind, string message)
        {
            if (Is(kind))
                return Advance();
            throw Error(Peek(), message);
        }

        private Token ConsumeCommandName(string message)
        {
            Token token = Peek();
            if (token.Kind is TokenKind.Identifier or TokenKind.Keyword)
                return Advance();

            throw Error(token, message);
        }

        private Token Advance()
            => _tokens[_index++];

        private Token Peek(int offset = 0)
        {
            int index = Math.Min(_index + offset, _tokens.Count - 1);
            return _tokens[index];
        }

        private static ScriptSyntaxException Error(Token token, string message)
            => new(message, token.Line, token.Column);

        private static List<Token> Tokenize(string script, List<ScriptComment> markerComments)
        {
            List<Token> tokens = [];
            int index = 0;
            int line = 1;
            int column = 1;

            void AdvancePosition(char ch)
            {
                index++;
                if (ch == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }

            while (index < script.Length)
            {
                char ch = script[index];
                if (char.IsWhiteSpace(ch))
                {
                    AdvancePosition(ch);
                    continue;
                }

                if (ch == '/' && index + 1 < script.Length && script[index + 1] == '/')
                {
                    int commentStart = index;
                    int commentLine = line;
                    int commentColumn = column;
                    while (index < script.Length && script[index] != '\n')
                        AdvancePosition(script[index]);

                    ReadOnlySpan<char> comment = script.AsSpan(commentStart + 2, index - commentStart - 2).Trim();
                    if (comment.StartsWith(UnconvertedCommentPrefix, StringComparison.OrdinalIgnoreCase))
                        markerComments.Add(new ScriptComment(UnconvertedCommentPrefix, comment[UnconvertedCommentPrefix.Length..].Trim().ToString(), commentLine, commentColumn));
                    else if (comment.StartsWith(BaseHashCommentPrefix, StringComparison.OrdinalIgnoreCase))
                        markerComments.Add(new ScriptComment(BaseHashCommentPrefix, comment[BaseHashCommentPrefix.Length..].Trim().ToString(), commentLine, commentColumn));

                    continue;
                }

                int tokenLine = line;
                int tokenColumn = column;

                if (char.IsLetter(ch) || ch == '_')
                {
                    int start = index;
                    while (index < script.Length && (char.IsLetterOrDigit(script[index]) || script[index] == '_'))
                        AdvancePosition(script[index]);
                    string text = script[start..index];
                    TokenKind kind = IsKeywordText(text) ? TokenKind.Keyword : TokenKind.Identifier;
                    tokens.Add(new Token(kind, text, tokenLine, tokenColumn));
                    continue;
                }

                if (char.IsDigit(ch) || (ch == '.' && index + 1 < script.Length && char.IsDigit(script[index + 1])))
                {
                    int start = index;
                    bool hasDot = ch == '.';
                    AdvancePosition(ch);
                    while (index < script.Length)
                    {
                        char current = script[index];
                        if (char.IsDigit(current))
                        {
                            AdvancePosition(current);
                            continue;
                        }
                        if (!hasDot && current == '.')
                        {
                            hasDot = true;
                            AdvancePosition(current);
                            continue;
                        }
                        if ((current == 'e' || current == 'E') && index + 1 < script.Length)
                        {
                            AdvancePosition(current);
                            if (index < script.Length && (script[index] == '+' || script[index] == '-'))
                                AdvancePosition(script[index]);
                            continue;
                        }
                        if (current == 'f' || current == 'F' || current == 'u' || current == 'U')
                        {
                            AdvancePosition(current);
                        }
                        break;
                    }
                    tokens.Add(new Token(TokenKind.Number, script[start..index], tokenLine, tokenColumn));
                    continue;
                }

                if (ch == '"')
                {
                    AdvancePosition(ch);
                    System.Text.StringBuilder builder = new();
                    while (index < script.Length)
                    {
                        char current = script[index];
                        AdvancePosition(current);
                        if (current == '"')
                            break;
                        if (current == '\\' && index < script.Length)
                        {
                            char escaped = script[index];
                            AdvancePosition(escaped);
                            builder.Append(escaped switch
                            {
                                'n' => '\n',
                                'r' => '\r',
                                't' => '\t',
                                '"' => '"',
                                '\\' => '\\',
                                _ => escaped
                            });
                        }
                        else
                        {
                            builder.Append(current);
                        }
                    }
                    tokens.Add(new Token(TokenKind.String, builder.ToString(), tokenLine, tokenColumn));
                    continue;
                }

                if (index + 1 < script.Length)
                {
                    string twoChars = script.Substring(index, 2);
                    TokenKind? compoundKind = twoChars switch
                    {
                        "&&" => TokenKind.AndAnd,
                        "||" => TokenKind.OrOr,
                        "==" => TokenKind.EqualEqual,
                        "!=" => TokenKind.BangEqual,
                        "<=" => TokenKind.LessEqual,
                        ">=" => TokenKind.GreaterEqual,
                        _ => null
                    };
                    if (compoundKind.HasValue)
                    {
                        tokens.Add(new Token(compoundKind.Value, twoChars, tokenLine, tokenColumn));
                        AdvancePosition(script[index]);
                        AdvancePosition(script[index]);
                        continue;
                    }
                }

                TokenKind singleCharKind = ch switch
                {
                    '(' => TokenKind.OpenParen,
                    ')' => TokenKind.CloseParen,
                    '{' => TokenKind.OpenBrace,
                    '}' => TokenKind.CloseBrace,
                    '[' => TokenKind.OpenBracket,
                    ']' => TokenKind.CloseBracket,
                    ',' => TokenKind.Comma,
                    ':' => TokenKind.Colon,
                    ';' => TokenKind.Semicolon,
                    '=' => TokenKind.Equals,
                    '!' => TokenKind.Bang,
                    '+' => TokenKind.Plus,
                    '-' => TokenKind.Minus,
                    '*' => TokenKind.Star,
                    '/' => TokenKind.Slash,
                    '<' => TokenKind.Less,
                    '>' => TokenKind.Greater,
                    _ => throw new ScriptSyntaxException($"Unexpected character '{ch}' in render pipeline script.", line, column)
                };
                tokens.Add(new Token(singleCharKind, ch.ToString(), tokenLine, tokenColumn));
                AdvancePosition(ch);
            }

            tokens.Add(new Token(TokenKind.EndOfFile, string.Empty, line, column));
            return tokens;
        }
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);

    /// <summary>Returns true when <paramref name="text"/> is a reserved word of the script language.</summary>
    private static bool IsKeywordText(string text)
        => text is "set" or "clear" or "if" or "else" or "switch" or "case" or "default" or "repeat" or "when" or "foreach_cascade" or "true" or "false" or "null";

    /// <summary>A comment that the parser keeps, such as an <c>// unconverted:</c> note.</summary>
    internal readonly record struct ScriptComment(string Prefix, string Text, int Line, int Column);

    /// <summary>The comment prefix that marks an item an export could not convert. A load reports each one.</summary>
    internal const string UnconvertedCommentPrefix = "unconverted:";

    /// <summary>The comment prefix of the structure hash of the base of a derived script.</summary>
    internal const string BaseHashCommentPrefix = "base-hash:";

    private enum TokenKind
    {
        Identifier,
        Keyword,
        Number,
        String,
        OpenParen,
        CloseParen,
        OpenBrace,
        CloseBrace,
        OpenBracket,
        CloseBracket,
        Comma,
        Colon,
        Semicolon,
        Equals,
        Bang,
        Plus,
        Minus,
        Star,
        Slash,
        Less,
        Greater,
        LessEqual,
        GreaterEqual,
        EqualEqual,
        BangEqual,
        AndAnd,
        OrOr,
        EndOfFile
    }
}
