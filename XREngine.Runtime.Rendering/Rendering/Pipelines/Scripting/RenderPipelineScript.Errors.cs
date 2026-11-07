namespace XREngine.Rendering;

// Script errors that carry a source location.
public sealed partial class RenderPipelineScript
{
    /// <summary>
    /// A script error at a known line and column. The message ends with the location, so callers that only show the
    /// message still see where the error is.
    /// </summary>
    internal sealed class ScriptSyntaxException(string reason, int line, int column)
        : InvalidOperationException(line > 0 ? $"{reason} (line {line}, column {column})" : reason)
    {
        /// <summary>Gets the error text without the location.</summary>
        public string Reason { get; } = reason;

        /// <summary>Gets the 1-based line, or 0 when the location is not known.</summary>
        public int Line { get; } = line;

        /// <summary>Gets the 1-based column, or 0 when the location is not known.</summary>
        public int Column { get; } = column;
    }

    /// <summary>
    /// Converts a script value for an assignment to <paramref name="targetType"/>. A failed conversion becomes a
    /// <see cref="ScriptSyntaxException"/> at the location of the assignment.
    /// </summary>
    private static object? ConvertAssignedValue(IValueNode value, Type targetType, string name, int line, int column)
    {
        try
        {
            return ScriptValueConverter.ConvertTo(value, targetType);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            if (ex is ScriptSyntaxException)
                throw;

            throw new ScriptSyntaxException($"Cannot set '{name}' to a {targetType.Name}: {ex.Message}", line, column);
        }
    }
}
