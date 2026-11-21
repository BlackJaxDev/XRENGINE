using System.Text;

namespace XREngine.Scene.Importers;

/// <summary>
/// Keeps the full text of Unity YAML values that contain " #". Unity writes such values without
/// quotes (for example the FBX material name <c>name: Material #356</c> in a ModelImporter
/// <c>externalObjects</c> list), and its own reader keeps the whole text. A YAML 1.1 reader
/// treats " #" as the start of a comment and reads only <c>Material</c>, so every
/// <c>Material #NNN</c> remap would collapse to one key. Unity YAML has no comments, so quoting
/// these values before parsing gives the same text that Unity reads.
/// </summary>
internal static class UnityYamlPlainScalars
{
    /// <summary>
    /// Returns <paramref name="text"/> with each plain <c>key: value</c> value that contains " #"
    /// (or starts with "#") wrapped in single quotes. Quoted, flow, block and tagged values stay
    /// as they are.
    /// </summary>
    public static string QuoteValuesWithHash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('#'))
            return text;

        StringBuilder result = new(text.Length + 64);
        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start);
            int lineEnd = end < 0 ? text.Length : end;
            int contentEnd = lineEnd > start && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            AppendLine(result, text.AsSpan(start, contentEnd - start));
            result.Append(text.AsSpan(contentEnd, lineEnd - contentEnd));
            if (end < 0)
                break;
            result.Append('\n');
            start = end + 1;
        }

        return result.ToString();
    }

    private static void AppendLine(StringBuilder result, ReadOnlySpan<char> line)
    {
        int separator = line.IndexOf(": ");
        if (separator < 0)
        {
            result.Append(line);
            return;
        }

        ReadOnlySpan<char> key = line[..separator];
        ReadOnlySpan<char> value = line[(separator + 2)..];
        ReadOnlySpan<char> trimmed = value.TrimStart(' ');
        bool hasHash = trimmed.StartsWith("#") || trimmed.Contains(" #", StringComparison.Ordinal);
        if (key.Contains('#') || !hasHash || trimmed.IsEmpty || "'\"[{|>&*!%@`".Contains(trimmed[0]))
        {
            result.Append(line);
            return;
        }

        result.Append(key).Append(": '");
        foreach (char c in trimmed.TrimEnd(' '))
        {
            if (c == '\'')
                result.Append('\'');
            result.Append(c);
        }
        result.Append('\'');
    }
}
