namespace XREngine.Editor.UI;

/// <summary>
/// Retains primary dictionary order and the display text of each row label and read-only value.
/// Multiline text is normalized to line feeds and limited to <see cref="MaxDisplayLines"/> lines,
/// so each row has a known line count before a virtual row binds it.
/// </summary>
internal sealed class EditorDictionaryRowPlan
{
    /// <summary>The largest number of lines that one label or read-only value displays.</summary>
    internal const int MaxDisplayLines = 32;

    private readonly string[] _labels;
    private readonly string?[] _readOnlyText;
    private readonly int[] _lineCounts;

    private EditorDictionaryRowPlan(string[] labels, string?[] readOnlyText, int[] lineCounts, bool hasMultilineRows)
    {
        _labels = labels;
        _readOnlyText = readOnlyText;
        _lineCounts = lineCounts;
        HasMultilineRows = hasMultilineRows;
    }

    internal int Count => _labels.Length;
    internal bool HasMultilineRows { get; }
    internal string Label(int index) => _labels[index];
    internal string? ReadOnlyText(int index) => _readOnlyText[index];

    /// <summary>Gets the larger display line count of the row label and its read-only value.</summary>
    internal int LineCount(int index) => _lineCounts[index];

    internal static bool TryCreate(IReadOnlyList<EditorDictionarySnapshot> sources,
        out EditorDictionaryRowPlan? plan)
    {
        plan = null;
        if (sources.Count == 0) return false;
        EditorDictionarySnapshot primary = sources[0];
        string[] labels = new string[primary.Keys.Length];
        string?[] readOnlyText = new string?[primary.Keys.Length];
        int[] lineCounts = new int[primary.Keys.Length];
        bool hasMultilineRows = false;
        for (int index = 0; index < primary.Keys.Length; index++)
        {
            object key = primary.Keys[index];
            labels[index] = ToDisplayText(key.ToString() ?? "<null>", out int lines);
            bool present = true;
            for (int source = 1; source < sources.Count; source++)
                if (sources[source].IndexOf(key) < 0)
                {
                    present = false;
                    break;
                }
            if (!present)
            {
                // Retain the formatted value so row recycling does not format it again.
                readOnlyText[index] = ToDisplayText(primary.Values[index]?.ToString() ?? "<null>", out int valueLines);
                lines = Math.Max(lines, valueLines);
            }
            lineCounts[index] = lines;
            hasMultilineRows |= lines > 1;
        }
        plan = new(labels, readOnlyText, lineCounts, hasMultilineRows);
        return true;
    }

    /// <summary>
    /// Converts every line separator to a line feed and limits the text to
    /// <see cref="MaxDisplayLines"/> lines. A CR LF pair is one separator.
    /// </summary>
    internal static string ToDisplayText(string text, out int lineCount)
    {
        lineCount = 1;
        if (!HasLineSeparator(text))
            return text;
        System.Text.StringBuilder builder = new(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            char value = text[index];
            if (!IsLineSeparator(value))
            {
                builder.Append(value);
                continue;
            }
            if (value == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                index++;
            if (lineCount == MaxDisplayLines - 1)
            {
                int remaining = CountLines(text, index + 1);
                if (remaining > 1)
                {
                    builder.Append('\n').Append($"... ({remaining} more lines)");
                    lineCount = MaxDisplayLines;
                    return builder.ToString();
                }
            }
            builder.Append('\n');
            lineCount++;
        }
        return builder.ToString();
    }

    private static int CountLines(string text, int start)
    {
        int lines = 1;
        for (int index = start; index < text.Length; index++)
        {
            char value = text[index];
            if (!IsLineSeparator(value))
                continue;
            if (value == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                index++;
            lines++;
        }
        return lines;
    }

    private static bool HasLineSeparator(string text)
        => text.AsSpan().IndexOfAny('\r', '\n', '\u0085') >= 0 ||
           text.AsSpan().IndexOfAny('\u2028', '\u2029') >= 0;

    private static bool IsLineSeparator(char value)
        => value is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029';
}
