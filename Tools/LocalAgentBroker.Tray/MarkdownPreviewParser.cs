using System.Text;

namespace XREngine.LocalAgentBroker.Tray;

/// <summary>
/// Produces a readable RichTextBox preview for the CommonMark constructs most
/// frequently emitted by model responses, without introducing a browser or a
/// package dependency into the tray companion.
/// </summary>
internal static class MarkdownPreviewParser
{
    public static MarkdownPreviewDocument Parse(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return MarkdownPreviewDocument.Empty;

        string normalized = markdown.ReplaceLineEndings("\n");
        var text = new StringBuilder(normalized.Length);
        var runs = new List<MarkdownPreviewRun>();
        bool inFence = false;

        foreach (ReadOnlySpan<char> sourceLine in normalized.AsSpan().EnumerateLines())
        {
            ReadOnlySpan<char> line = sourceLine;
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            int lineStart = text.Length;
            MarkdownPreviewStyle blockStyle = MarkdownPreviewStyle.Normal;
            if (inFence)
            {
                text.Append(line);
                AddRun(runs, lineStart, line.Length, MarkdownPreviewStyle.Code);
                text.Append('\n');
                continue;
            }

            int headingLevel = HeadingLevel(line);
            if (headingLevel > 0)
            {
                line = line[(headingLevel + 1)..];
                blockStyle = headingLevel switch
                {
                    1 => MarkdownPreviewStyle.Heading1 | MarkdownPreviewStyle.Bold,
                    2 => MarkdownPreviewStyle.Heading2 | MarkdownPreviewStyle.Bold,
                    _ => MarkdownPreviewStyle.Heading3 | MarkdownPreviewStyle.Bold,
                };
            }
            else if (TryStripPrefix(ref line, "> "))
            {
                text.Append("│ ");
                blockStyle = MarkdownPreviewStyle.Quote | MarkdownPreviewStyle.Italic;
            }
            else if (TryStripListMarker(ref line))
            {
                text.Append("• ");
            }
            else if (IsHorizontalRule(line))
            {
                text.Append('─', 28);
                AddRun(runs, lineStart, 28, MarkdownPreviewStyle.Quote);
                text.Append('\n');
                continue;
            }

            int contentStart = text.Length;
            AppendInline(line, text, runs, blockStyle);
            if (blockStyle != MarkdownPreviewStyle.Normal)
                AddRun(runs, contentStart, text.Length - contentStart, blockStyle);
            text.Append('\n');
        }

        if (!normalized.EndsWith('\n') && text.Length > 0)
            text.Length--;
        return new MarkdownPreviewDocument(text.ToString(), NormalizeRuns(text.Length, runs));
    }

    private static void AppendInline(
        ReadOnlySpan<char> source,
        StringBuilder text,
        List<MarkdownPreviewRun> runs,
        MarkdownPreviewStyle inheritedStyle)
    {
        int index = 0;
        while (index < source.Length)
        {
            if (source[index] == '\\' && index + 1 < source.Length)
            {
                text.Append(source[index + 1]);
                index += 2;
                continue;
            }

            if (TryAppendLink(source, ref index, text, runs, inheritedStyle))
                continue;
            if (TryAppendDelimited(source, ref index, text, runs, "**", MarkdownPreviewStyle.Bold, inheritedStyle)
                || TryAppendDelimited(source, ref index, text, runs, "__", MarkdownPreviewStyle.Bold, inheritedStyle)
                || TryAppendDelimited(source, ref index, text, runs, "~~", MarkdownPreviewStyle.Strikeout, inheritedStyle)
                || TryAppendDelimited(source, ref index, text, runs, "`", MarkdownPreviewStyle.Code, inheritedStyle)
                || TryAppendDelimited(source, ref index, text, runs, "*", MarkdownPreviewStyle.Italic, inheritedStyle)
                || TryAppendDelimited(source, ref index, text, runs, "_", MarkdownPreviewStyle.Italic, inheritedStyle))
            {
                continue;
            }

            text.Append(source[index]);
            index++;
        }
    }

    private static bool TryAppendLink(
        ReadOnlySpan<char> source,
        ref int index,
        StringBuilder text,
        List<MarkdownPreviewRun> runs,
        MarkdownPreviewStyle inheritedStyle)
    {
        if (source[index] != '[')
            return false;
        int labelEnd = source[(index + 1)..].IndexOf(']');
        if (labelEnd < 0)
            return false;
        labelEnd += index + 1;
        if (labelEnd + 1 >= source.Length || source[labelEnd + 1] != '(')
            return false;
        int targetEnd = source[(labelEnd + 2)..].IndexOf(')');
        if (targetEnd < 0)
            return false;
        targetEnd += labelEnd + 2;

        int start = text.Length;
        ReadOnlySpan<char> label = source[(index + 1)..labelEnd];
        ReadOnlySpan<char> target = source[(labelEnd + 2)..targetEnd];
        AppendInline(label, text, runs, inheritedStyle | MarkdownPreviewStyle.Link);
        if (!label.SequenceEqual(target))
        {
            text.Append(" (");
            text.Append(target);
            text.Append(')');
        }
        AddRun(runs, start, text.Length - start, inheritedStyle | MarkdownPreviewStyle.Link);
        index = targetEnd + 1;
        return true;
    }

    private static bool TryAppendDelimited(
        ReadOnlySpan<char> source,
        ref int index,
        StringBuilder text,
        List<MarkdownPreviewRun> runs,
        string delimiter,
        MarkdownPreviewStyle style,
        MarkdownPreviewStyle inheritedStyle)
    {
        ReadOnlySpan<char> marker = delimiter.AsSpan();
        if (!source[index..].StartsWith(marker, StringComparison.Ordinal))
            return false;
        if (!CanOpenDelimiter(source, index, marker))
            return false;
        int contentStart = index + marker.Length;
        int contentEnd = FindClosingDelimiter(source, contentStart, marker);
        if (contentEnd < 0)
            return false;

        int start = text.Length;
        ReadOnlySpan<char> content = source[contentStart..contentEnd];
        MarkdownPreviewStyle combinedStyle = inheritedStyle | style;
        if (style == MarkdownPreviewStyle.Code)
            text.Append(content);
        else
            AppendInline(content, text, runs, combinedStyle);
        AddRun(runs, start, text.Length - start, combinedStyle);
        index = contentEnd + marker.Length;
        return true;
    }

    private static bool CanOpenDelimiter(
        ReadOnlySpan<char> source,
        int index,
        ReadOnlySpan<char> marker)
    {
        int contentStart = index + marker.Length;
        if (contentStart >= source.Length || char.IsWhiteSpace(source[contentStart]))
            return false;
        return marker[0] != '_'
            || index == 0
            || !char.IsLetterOrDigit(source[index - 1]);
    }

    private static int FindClosingDelimiter(
        ReadOnlySpan<char> source,
        int contentStart,
        ReadOnlySpan<char> marker)
    {
        int searchIndex = contentStart;
        while (searchIndex <= source.Length - marker.Length)
        {
            int relativeIndex = source[searchIndex..].IndexOf(marker, StringComparison.Ordinal);
            if (relativeIndex < 0)
                return -1;

            int candidate = searchIndex + relativeIndex;
            bool escaped = candidate > 0 && source[candidate - 1] == '\\';
            bool precededByWhitespace = candidate == contentStart
                || char.IsWhiteSpace(source[candidate - 1]);
            bool intrawordUnderscore = marker[0] == '_'
                && candidate + marker.Length < source.Length
                && char.IsLetterOrDigit(source[candidate + marker.Length]);
            if (!escaped && !precededByWhitespace && !intrawordUnderscore)
                return candidate;
            searchIndex = candidate + marker.Length;
        }
        return -1;
    }

    private static int HeadingLevel(ReadOnlySpan<char> line)
    {
        int level = 0;
        while (level < line.Length && level < 6 && line[level] == '#')
            level++;
        return level > 0 && level < line.Length && line[level] == ' ' ? level : 0;
    }

    private static bool TryStripPrefix(ref ReadOnlySpan<char> line, string prefix)
    {
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        line = line[prefix.Length..];
        return true;
    }

    private static bool TryStripListMarker(ref ReadOnlySpan<char> line)
    {
        int indentation = 0;
        while (indentation < line.Length && line[indentation] == ' ')
            indentation++;
        ReadOnlySpan<char> trimmed = line[indentation..];
        if (trimmed.Length < 2
            || (trimmed[0] is not ('-' or '*' or '+'))
            || trimmed[1] != ' ')
        {
            return false;
        }

        line = trimmed[2..];
        return true;
    }

    private static bool IsHorizontalRule(ReadOnlySpan<char> line)
    {
        ReadOnlySpan<char> trimmed = line.Trim();
        if (trimmed.Length < 3)
            return false;
        char marker = trimmed[0];
        if (marker is not ('-' or '*' or '_'))
            return false;
        foreach (char character in trimmed)
        {
            if (character != marker && character != ' ')
                return false;
        }
        return true;
    }

    private static void AddRun(
        List<MarkdownPreviewRun> runs,
        int start,
        int length,
        MarkdownPreviewStyle style)
    {
        if (length > 0 && style != MarkdownPreviewStyle.Normal)
            runs.Add(new MarkdownPreviewRun(start, length, style));
    }

    private static IReadOnlyList<MarkdownPreviewRun> NormalizeRuns(
        int textLength,
        IReadOnlyList<MarkdownPreviewRun> sourceRuns)
    {
        if (sourceRuns.Count == 0 || textLength == 0)
            return [];

        var styles = new MarkdownPreviewStyle[textLength];
        foreach (MarkdownPreviewRun run in sourceRuns)
        {
            int end = Math.Min(textLength, run.Start + run.Length);
            for (int index = Math.Max(0, run.Start); index < end; index++)
                styles[index] |= run.Style;
        }

        var normalized = new List<MarkdownPreviewRun>();
        int runStart = 0;
        MarkdownPreviewStyle current = styles[0];
        for (int index = 1; index <= styles.Length; index++)
        {
            if (index < styles.Length && styles[index] == current)
                continue;
            if (current != MarkdownPreviewStyle.Normal)
                normalized.Add(new MarkdownPreviewRun(runStart, index - runStart, current));
            if (index < styles.Length)
            {
                runStart = index;
                current = styles[index];
            }
        }
        return normalized;
    }
}
