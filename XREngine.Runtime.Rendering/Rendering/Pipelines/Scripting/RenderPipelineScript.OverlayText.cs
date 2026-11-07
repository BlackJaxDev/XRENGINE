using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

// Value and condition text that derived pipeline overlays store and apply.
public sealed partial class RenderPipelineScript
{
    /// <summary>Sets property <paramref name="propertyName"/> of <paramref name="command"/> from script value text, such as <c>AgX</c> or <c>0.5</c>.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the text does not parse or the property does not take the value.</exception>
    internal static void AssignPropertyText(ViewportRenderCommand command, string propertyName, string valueText)
    {
        IValueNode value = ParseValueText(valueText);
        new PropertyAssignment(propertyName, value).Apply(command);
    }

    /// <summary>
    /// Converts script value text to <paramref name="targetType"/>. A null type gives the literal value: a number
    /// becomes a <see cref="float"/>, and <c>true</c> or <c>false</c> becomes a <see cref="bool"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the text does not parse or does not convert.</exception>
    internal static object? ConvertValueText(string valueText, Type? targetType)
    {
        IValueNode value = ParseValueText(valueText);
        if (targetType is not null)
            return ConvertAssignedValue(value, targetType, valueText, 0, 0);

        object? literal = value.GetConstantValue();
        return literal switch
        {
            int or long or double or uint or ulong => Convert.ToSingle(literal, System.Globalization.CultureInfo.InvariantCulture),
            _ => literal,
        };
    }

    /// <summary>Returns the script text of <paramref name="value"/>, or null when the script cannot hold it.</summary>
    public static string? FormatValueText(object? value)
        => ScriptExporter.TrySerializeValue(value, out string? text) ? text : null;

    /// <summary>Returns the script text of <paramref name="condition"/>, such as <c>stage bloom enabled</c>.</summary>
    public static string FormatConditionText(RenderBranchCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return ScriptExporter.FormatCondition(condition);
    }

    /// <summary>Parses condition text, such as <c>var Stereo == 1</c>. The text holds no parentheses around the condition.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the text is not a data condition.</exception>
    public static RenderBranchCondition ParseConditionText(string text)
    {
        Parser parser = new(string.Concat("(", text, ")"));
        return parser.ParseConditionToEnd();
    }

    private static IValueNode ParseValueText(string valueText)
    {
        Parser parser = new(valueText ?? string.Empty);
        return parser.ParseValueToEnd();
    }

    private sealed partial class Parser
    {
        /// <summary>Parses one value and requires the end of the text after it.</summary>
        public IValueNode ParseValueToEnd()
        {
            IValueNode value = ParseValue();
            if (!Is(TokenKind.EndOfFile))
                throw Error(Peek(), "Expected the end of the value.");
            return value;
        }

        /// <summary>Parses <c>(condition)</c> and requires the end of the text after it.</summary>
        public RenderBranchCondition ParseConditionToEnd()
        {
            RenderBranchCondition condition = ParseParenthesizedDataCondition();
            if (!Is(TokenKind.EndOfFile))
                throw Error(Peek(), "Expected the end of the condition.");
            return condition;
        }

        /// <summary>Parses <c>(condition)</c>. A code condition is not allowed, because a patch holds data only.</summary>
        private RenderBranchCondition ParseParenthesizedDataCondition()
        {
            Token start = Consume(TokenKind.OpenParen, "Expected '(' before the condition.");
            if (!TryParseDataCondition(forSwitch: false, out RenderBranchCondition? condition) || condition is null)
                throw Error(start, "Expected a data condition, such as 'stage bloom enabled' or 'var Name == 1'.");
            Consume(TokenKind.CloseParen, "Expected ')' after the condition.");
            return condition;
        }
    }
}
