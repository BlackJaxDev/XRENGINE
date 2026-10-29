namespace XREngine.Rendering.UI;

/// <summary>Split hard line separators without changing source UTF-16 offsets.</summary>
public static class UITextHardLineBreaks
{
    public static UITextSourceLine[] Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<UITextSourceLine> lines = [];
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char value = text[i];
            if (value is not ('\r' or '\n' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029'))
                continue;
            int breakLength = value == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            lines.Add(new UITextSourceLine(start, i - start, breakLength));
            i += breakLength - 1;
            start = i + 1;
        }
        lines.Add(new UITextSourceLine(start, text.Length - start, 0));
        return [.. lines];
    }
}
