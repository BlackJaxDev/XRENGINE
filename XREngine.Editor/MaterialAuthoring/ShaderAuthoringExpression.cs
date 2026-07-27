using System.Globalization;

namespace XREngine.Editor.MaterialAuthoring;

public interface IShaderAuthoringExpressionContext
{
    bool TryResolve(string operand, out ShaderAuthoringValue value);
}

public readonly record struct ShaderAuthoringValue(object? Value)
{
    public bool AsBoolean()
        => Value switch
        {
            null => false,
            bool value => value,
            string value when bool.TryParse(value, out bool parsed) => parsed,
            string value => value.Length > 0,
            IConvertible value => Math.Abs(value.ToDouble(CultureInfo.InvariantCulture)) > double.Epsilon,
            _ => true,
        };

    public double AsNumber()
        => Value switch
        {
            null => 0.0,
            bool value => value ? 1.0 : 0.0,
            string value when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            IConvertible value => value.ToDouble(CultureInfo.InvariantCulture),
            _ => double.NaN,
        };

    public string AsText() => Convert.ToString(Value, CultureInfo.InvariantCulture) ?? string.Empty;
}

public sealed class ShaderAuthoringExpression
{
    private readonly ExpressionNode _root;

    private ShaderAuthoringExpression(
        string source,
        ExpressionNode root,
        IReadOnlySet<string> dependencies)
    {
        Source = source;
        _root = root;
        Dependencies = dependencies;
    }

    public string Source { get; }
    public IReadOnlySet<string> Dependencies { get; }

    public ShaderAuthoringValue Evaluate(IShaderAuthoringExpressionContext context)
        => _root.Evaluate(context);

    public bool EvaluateBoolean(IShaderAuthoringExpressionContext context)
        => Evaluate(context).AsBoolean();

    public static bool TryCompile(
        string? source,
        out ShaderAuthoringExpression? expression,
        out string? diagnostic)
    {
        expression = null;
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(source))
            return true;

        try
        {
            Parser parser = new(source);
            ExpressionNode root = parser.Parse();
            expression = new(source, root, parser.Dependencies);
            return true;
        }
        catch (ExpressionParseException exception)
        {
            diagnostic = exception.Message;
            return false;
        }
    }

    private abstract class ExpressionNode
    {
        public abstract ShaderAuthoringValue Evaluate(IShaderAuthoringExpressionContext context);
    }

    private sealed class LiteralNode(ShaderAuthoringValue value) : ExpressionNode
    {
        public override ShaderAuthoringValue Evaluate(IShaderAuthoringExpressionContext context) => value;
    }

    private sealed class OperandNode(string name) : ExpressionNode
    {
        public override ShaderAuthoringValue Evaluate(IShaderAuthoringExpressionContext context)
            => context.TryResolve(name, out ShaderAuthoringValue value) ? value : default;
    }

    private sealed class UnaryNode(TokenKind operation, ExpressionNode operand) : ExpressionNode
    {
        public override ShaderAuthoringValue Evaluate(IShaderAuthoringExpressionContext context)
        {
            ShaderAuthoringValue value = operand.Evaluate(context);
            return operation switch
            {
                TokenKind.Bang => new(!value.AsBoolean()),
                TokenKind.Minus => new(-value.AsNumber()),
                TokenKind.Plus => new(value.AsNumber()),
                _ => default,
            };
        }
    }

    private sealed class BinaryNode(TokenKind operation, ExpressionNode left, ExpressionNode right) : ExpressionNode
    {
        public override ShaderAuthoringValue Evaluate(IShaderAuthoringExpressionContext context)
        {
            ShaderAuthoringValue a = left.Evaluate(context);
            if (operation == TokenKind.And && !a.AsBoolean())
                return new(false);
            if (operation == TokenKind.Or && a.AsBoolean())
                return new(true);

            ShaderAuthoringValue b = right.Evaluate(context);
            double an = a.AsNumber();
            double bn = b.AsNumber();
            return operation switch
            {
                TokenKind.And => new(a.AsBoolean() && b.AsBoolean()),
                TokenKind.Or => new(a.AsBoolean() || b.AsBoolean()),
                TokenKind.Equal => new(AreEqual(a, b)),
                TokenKind.NotEqual => new(!AreEqual(a, b)),
                TokenKind.Less => new(an < bn),
                TokenKind.LessEqual => new(an <= bn),
                TokenKind.Greater => new(an > bn),
                TokenKind.GreaterEqual => new(an >= bn),
                TokenKind.Plus => new(an + bn),
                TokenKind.Minus => new(an - bn),
                TokenKind.Star => new(an * bn),
                TokenKind.Slash => new(Math.Abs(bn) <= double.Epsilon ? double.NaN : an / bn),
                TokenKind.Percent => new(Math.Abs(bn) <= double.Epsilon ? double.NaN : an % bn),
                TokenKind.Power => new(Math.Pow(an, bn)),
                _ => default,
            };
        }

        private static bool AreEqual(ShaderAuthoringValue a, ShaderAuthoringValue b)
        {
            double an = a.AsNumber();
            double bn = b.AsNumber();
            if (!double.IsNaN(an) && !double.IsNaN(bn))
                return Math.Abs(an - bn) <= 1e-6;
            return string.Equals(a.AsText(), b.AsText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class Parser
    {
        private readonly Lexer _lexer;
        private Token _current;
        private readonly HashSet<string> _dependencies = new(StringComparer.Ordinal);

        public Parser(string source)
        {
            _lexer = new(source);
            _current = _lexer.Next();
        }

        public IReadOnlySet<string> Dependencies => _dependencies;

        public ExpressionNode Parse()
        {
            ExpressionNode value = ParseOr();
            Expect(TokenKind.End);
            return value;
        }

        private ExpressionNode ParseOr()
        {
            ExpressionNode left = ParseAnd();
            while (_current.Kind == TokenKind.Or)
            {
                TokenKind operation = Take().Kind;
                left = new BinaryNode(operation, left, ParseAnd());
            }
            return left;
        }

        private ExpressionNode ParseAnd()
        {
            ExpressionNode left = ParseEquality();
            while (_current.Kind == TokenKind.And)
            {
                TokenKind operation = Take().Kind;
                left = new BinaryNode(operation, left, ParseEquality());
            }
            return left;
        }

        private ExpressionNode ParseEquality()
        {
            ExpressionNode left = ParseComparison();
            while (_current.Kind is TokenKind.Equal or TokenKind.NotEqual)
            {
                TokenKind operation = Take().Kind;
                left = new BinaryNode(operation, left, ParseComparison());
            }
            return left;
        }

        private ExpressionNode ParseComparison()
        {
            ExpressionNode left = ParseTerm();
            while (_current.Kind is TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual)
            {
                TokenKind operation = Take().Kind;
                left = new BinaryNode(operation, left, ParseTerm());
            }
            return left;
        }

        private ExpressionNode ParseTerm()
        {
            ExpressionNode left = ParseFactor();
            while (_current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                TokenKind operation = Take().Kind;
                left = new BinaryNode(operation, left, ParseFactor());
            }
            return left;
        }

        private ExpressionNode ParseFactor()
        {
            ExpressionNode left = ParsePower();
            while (_current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
            {
                TokenKind operation = Take().Kind;
                left = new BinaryNode(operation, left, ParsePower());
            }
            return left;
        }

        private ExpressionNode ParsePower()
        {
            ExpressionNode left = ParseUnary();
            if (_current.Kind == TokenKind.Power)
                left = new BinaryNode(Take().Kind, left, ParsePower());
            return left;
        }

        private ExpressionNode ParseUnary()
        {
            if (_current.Kind is TokenKind.Bang or TokenKind.Plus or TokenKind.Minus)
                return new UnaryNode(Take().Kind, ParseUnary());
            return ParsePrimary();
        }

        private ExpressionNode ParsePrimary()
        {
            Token token = Take();
            switch (token.Kind)
            {
                case TokenKind.Number:
                    if (!double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                        throw new ExpressionParseException($"Invalid numeric literal '{token.Text}'.");
                    return new LiteralNode(new(number));
                case TokenKind.String:
                    return new LiteralNode(new(token.Text));
                case TokenKind.True:
                    return new LiteralNode(new(true));
                case TokenKind.False:
                    return new LiteralNode(new(false));
                case TokenKind.Identifier:
                    _dependencies.Add(token.Text);
                    return new OperandNode(token.Text);
                case TokenKind.LeftParen:
                    ExpressionNode nested = ParseOr();
                    Expect(TokenKind.RightParen);
                    return nested;
                default:
                    throw Error($"Expected a value but found '{token.Text}'.");
            }
        }

        private Token Take()
        {
            Token current = _current;
            _current = _lexer.Next();
            return current;
        }

        private void Expect(TokenKind kind)
        {
            if (_current.Kind != kind)
                throw Error($"Expected {kind} but found '{_current.Text}'.");
            Take();
        }

        private ExpressionParseException Error(string message)
            => new($"{message} At character {_lexer.Position}.");
    }

    private sealed class Lexer(string source)
    {
        private int _position;
        public int Position => _position;

        public Token Next()
        {
            while (_position < source.Length && char.IsWhiteSpace(source[_position]))
                _position++;
            if (_position >= source.Length)
                return new(TokenKind.End, string.Empty);

            int start = _position;
            char value = source[_position++];
            switch (value)
            {
                case '(': return new(TokenKind.LeftParen, "(");
                case ')': return new(TokenKind.RightParen, ")");
                case '+': return new(TokenKind.Plus, "+");
                case '-': return new(TokenKind.Minus, "-");
                case '*': return new(TokenKind.Star, "*");
                case '/': return new(TokenKind.Slash, "/");
                case '%': return new(TokenKind.Percent, "%");
                case '^': return new(TokenKind.Power, "^");
                case '!':
                    return Match('=') ? new(TokenKind.NotEqual, "!=") : new(TokenKind.Bang, "!");
                case '=':
                    Match('=');
                    return new(TokenKind.Equal, "==");
                case '<':
                    return Match('=') ? new(TokenKind.LessEqual, "<=") : new(TokenKind.Less, "<");
                case '>':
                    return Match('=') ? new(TokenKind.GreaterEqual, ">=") : new(TokenKind.Greater, ">");
                case '&':
                    Match('&');
                    return new(TokenKind.And, "&&");
                case '|':
                    Match('|');
                    return new(TokenKind.Or, "||");
                case '"':
                case '\'':
                    return ReadString(value);
            }

            if (char.IsDigit(value) || value == '.')
            {
                while (_position < source.Length &&
                       (char.IsDigit(source[_position]) || source[_position] is '.' or 'e' or 'E' or '+' or '-'))
                {
                    char current = source[_position];
                    if ((current is '+' or '-') && source[_position - 1] is not ('e' or 'E'))
                        break;
                    _position++;
                }
                return new(TokenKind.Number, source[start.._position]);
            }

            if (char.IsLetter(value) || value is '_' or '$')
            {
                while (_position < source.Length &&
                       (char.IsLetterOrDigit(source[_position]) || source[_position] is '_' or '.' or ':' or '$'))
                    _position++;
                string text = source[start.._position];
                return text.ToLowerInvariant() switch
                {
                    "true" => new(TokenKind.True, text),
                    "false" => new(TokenKind.False, text),
                    _ => new(TokenKind.Identifier, text),
                };
            }

            throw new ExpressionParseException($"Unexpected character '{value}' at character {start}.");
        }

        private Token ReadString(char quote)
        {
            int start = _position;
            while (_position < source.Length && source[_position] != quote)
            {
                if (source[_position] == '\\' && _position + 1 < source.Length)
                    _position += 2;
                else
                    _position++;
            }
            if (_position >= source.Length)
                throw new ExpressionParseException($"Unterminated string at character {start - 1}.");
            string value = source[start.._position];
            _position++;
            return new(TokenKind.String, value);
        }

        private bool Match(char expected)
        {
            if (_position >= source.Length || source[_position] != expected)
                return false;
            _position++;
            return true;
        }
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    private enum TokenKind
    {
        End,
        Identifier,
        Number,
        String,
        True,
        False,
        LeftParen,
        RightParen,
        Plus,
        Minus,
        Star,
        Slash,
        Percent,
        Power,
        Bang,
        Equal,
        NotEqual,
        Less,
        LessEqual,
        Greater,
        GreaterEqual,
        And,
        Or,
    }

    private sealed class ExpressionParseException(string message) : Exception(message);
}
