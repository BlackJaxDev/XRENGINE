using System.Globalization;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

// Data branch conditions and branch labels.
public sealed partial class RenderPipelineScript
{
    /// <summary>The condition text of a branch that has code and no data condition.</summary>
    internal const string CodeConditionText = "code";

    private sealed partial class Parser
    {
        /// <summary>
        /// Parses a data condition: <c>true</c>, <c>false</c>, <c>code</c>, <c>var Name</c>, <c>setting Name</c>,
        /// <c>stage Key enabled</c>, or <c>capability Name</c>, with an optional <c>!</c> or comparison. A switch takes
        /// only the source. When the tokens are not a data condition, the method restores the position and returns false.
        /// </summary>
        /// <param name="forSwitch">True for a switch selector, which takes no operator.</param>
        /// <param name="condition">The condition, or null for <c>code</c>.</param>
        private bool TryParseDataCondition(bool forSwitch, out RenderBranchCondition? condition)
        {
            condition = null;
            int start = _index;

            if (IsContextual(CodeConditionText) && Peek(1).Kind == TokenKind.CloseParen)
            {
                Advance();
                return true;
            }

            if (!forSwitch && Peek().Kind == TokenKind.Keyword && Peek(1).Kind == TokenKind.CloseParen &&
                (IsKeyword("true") || IsKeyword("false")))
            {
                condition = RenderBranchCondition.Constant(IsKeyword("true"));
                Advance();
                return true;
            }

            bool negate = !forSwitch && Match(TokenKind.Bang);
            bool parenthesized = negate && Match(TokenKind.OpenParen);
            if (!TryParseConditionSource(out ERenderBranchConditionSource source, out string key))
            {
                _index = start;
                return false;
            }

            if (parenthesized)
                Consume(TokenKind.CloseParen, "Expected ')' after the negated condition.");

            ERenderBranchConditionOperator op = negate ? ERenderBranchConditionOperator.IsFalse : ERenderBranchConditionOperator.IsTrue;
            double value = 0.0;
            if (!negate && !forSwitch && TryMatchComparison(out ERenderBranchConditionOperator comparison))
            {
                op = comparison;
                value = ParseConditionNumber();
            }

            condition = new RenderBranchCondition { Source = source, Key = key, Operator = op, Value = value };
            return true;
        }

        private bool TryParseConditionSource(out ERenderBranchConditionSource source, out string key)
        {
            source = default;
            key = string.Empty;
            Token next = Peek(1);
            if (IsContextual("var") && next.Kind == TokenKind.Identifier)
            {
                Advance();
                source = ERenderBranchConditionSource.PipelineVariable;
                key = Advance().Text;
                return true;
            }

            if (IsContextual("setting") && next.Kind == TokenKind.Identifier)
            {
                Advance();
                source = ERenderBranchConditionSource.PipelineSetting;
                key = Advance().Text;
                return true;
            }

            if (IsContextual("stage") && next.Kind is TokenKind.Identifier or TokenKind.String &&
                Peek(2).Kind == TokenKind.Identifier && string.Equals(Peek(2).Text, "enabled", StringComparison.Ordinal))
            {
                Advance();
                source = ERenderBranchConditionSource.StageEnabled;
                key = Advance().Text;
                Advance();
                return true;
            }

            if (IsContextual("capability") && next.Kind == TokenKind.Identifier)
            {
                Advance();
                Token capability = Advance();
                if (!Enum.TryParse(capability.Text, ignoreCase: true, out ERenderBranchCapability parsed))
                    throw Error(capability, $"Unknown capability '{capability.Text}'. Known capabilities: {string.Join(", ", Enum.GetNames<ERenderBranchCapability>())}.");

                source = ERenderBranchConditionSource.Capability;
                key = parsed.ToString();
                return true;
            }

            return false;
        }

        private bool TryMatchComparison(out ERenderBranchConditionOperator op)
        {
            op = Peek().Kind switch
            {
                TokenKind.EqualEqual => ERenderBranchConditionOperator.Equal,
                TokenKind.BangEqual => ERenderBranchConditionOperator.NotEqual,
                TokenKind.Greater => ERenderBranchConditionOperator.Greater,
                TokenKind.GreaterEqual => ERenderBranchConditionOperator.GreaterOrEqual,
                TokenKind.Less => ERenderBranchConditionOperator.Less,
                TokenKind.LessEqual => ERenderBranchConditionOperator.LessOrEqual,
                _ => ERenderBranchConditionOperator.IsTrue,
            };

            if (op == ERenderBranchConditionOperator.IsTrue)
                return false;

            Advance();
            return true;
        }

        private double ParseConditionNumber()
        {
            if (MatchKeyword("true"))
                return 1.0;
            if (MatchKeyword("false"))
                return 0.0;

            bool negative = Match(TokenKind.Minus);
            Token number = Consume(TokenKind.Number, "Expected a number after the comparison.");
            double value = double.Parse(number.Text.TrimEnd('f', 'F', 'u', 'U'), NumberStyles.Float, CultureInfo.InvariantCulture);
            return negative ? -value : value;
        }

        /// <summary>Parses an optional <c>as "Label"</c> after a branch condition.</summary>
        private string? ParseBranchLabel()
        {
            if (!IsContextual("as") || Peek(1).Kind != TokenKind.String)
                return null;

            Advance();
            return Advance().Text;
        }

        /// <summary>Returns true when the current token is the identifier <paramref name="word"/>.</summary>
        private bool IsContextual(string word)
            => Peek().Kind == TokenKind.Identifier && string.Equals(Peek().Text, word, StringComparison.Ordinal);
    }
}
