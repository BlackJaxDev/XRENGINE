namespace XREngine.Editor.UI;

/// <summary>Retains primary dictionary order and fixed-height read-only row text.</summary>
internal sealed class EditorDictionaryRowPlan
{
    private readonly string[] _labels;
    private readonly string?[] _readOnlyText;

    private EditorDictionaryRowPlan(string[] labels, string?[] readOnlyText)
    {
        _labels = labels;
        _readOnlyText = readOnlyText;
    }

    internal int Count => _labels.Length;
    internal string Label(int index) => _labels[index];
    internal string? ReadOnlyText(int index) => _readOnlyText[index];

    internal static bool TryCreate(IReadOnlyList<EditorDictionarySnapshot> sources,
        out EditorDictionaryRowPlan? plan)
    {
        plan = null;
        if (sources.Count == 0) return false;
        EditorDictionarySnapshot primary = sources[0];
        string[] labels = new string[primary.Keys.Length];
        string?[] readOnlyText = new string?[primary.Keys.Length];
        for (int index = 0; index < primary.Keys.Length; index++)
        {
            object key = primary.Keys[index];
            string label = key.ToString() ?? "<null>";
            if (!IsSingleLine(label)) return false;
            labels[index] = label;
            bool present = true;
            for (int source = 1; source < sources.Count; source++)
                if (sources[source].IndexOf(key) < 0)
                {
                    present = false;
                    break;
                }
            if (present) continue;
            // Admission must inspect the complete text before choosing a fixed-height
            // view. Retain it so row recycling does not format the value again.
            string text = primary.Values[index]?.ToString() ?? "<null>";
            if (!IsSingleLine(text)) return false;
            readOnlyText[index] = text;
        }
        plan = new(labels, readOnlyText);
        return true;
    }

    private static bool IsSingleLine(string text)
        => text.AsSpan().IndexOfAny('\r', '\n', '\u0085') < 0 &&
           text.AsSpan().IndexOfAny('\u2028', '\u2029') < 0;
}
