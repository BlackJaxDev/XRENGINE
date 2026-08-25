namespace XREngine.LocalAgentBroker.Tray;

/// <summary>Applies a parsed Markdown preview to a RichTextBox range.</summary>
internal sealed class MarkdownRichTextRenderer : IDisposable
{
    private readonly RichTextBox _textBox;
    private readonly Font _normalFont;
    private readonly Dictionary<(FontStyle Style, int SizeDelta, bool Monospace), Font> _fonts = [];
    private bool _dark;

    public MarkdownRichTextRenderer(RichTextBox textBox)
    {
        _textBox = textBox ?? throw new ArgumentNullException(nameof(textBox));
        _normalFont = textBox.Font;
    }

    public void SetTheme(bool dark)
        => _dark = dark;

    public IReadOnlyList<RichTextFadeRun> Apply(
        MarkdownPreviewDocument document,
        int documentOffset,
        int previewStart,
        int fadeFromPreviewOffset)
    {
        int suffixLength = document.Text.Length - previewStart;
        if (suffixLength <= 0)
            return [];

        _textBox.Select(documentOffset + previewStart, suffixLength);
        _textBox.SelectionFont = _normalFont;
        _textBox.SelectionColor = ForegroundColor(MarkdownPreviewStyle.Normal);
        _textBox.SelectionBackColor = _textBox.BackColor;

        int fadeStart = Math.Max(previewStart, fadeFromPreviewOffset);
        Color[]? fadeColors = fadeStart < document.Text.Length
            ? Enumerable.Repeat(
                ForegroundColor(MarkdownPreviewStyle.Normal),
                document.Text.Length - fadeStart).ToArray()
            : null;

        foreach (MarkdownPreviewRun run in document.Runs)
        {
            int runStart = Math.Max(run.Start, previewStart);
            int runEnd = Math.Min(run.Start + run.Length, document.Text.Length);
            if (runEnd <= runStart)
                continue;

            _textBox.Select(documentOffset + runStart, runEnd - runStart);
            _textBox.SelectionFont = FontFor(run.Style);
            _textBox.SelectionColor = ForegroundColor(run.Style);
            if (run.Style.HasFlag(MarkdownPreviewStyle.Code))
                _textBox.SelectionBackColor = CodeBackgroundColor();

            if (fadeColors is not null)
            {
                int styledFadeStart = Math.Max(runStart, fadeStart);
                for (int index = styledFadeStart; index < runEnd; index++)
                    fadeColors[index - fadeStart] = ForegroundColor(run.Style);
            }
        }
        return BuildFadeRuns(documentOffset + fadeStart, fadeColors);
    }

    public void Dispose()
    {
        foreach (Font font in _fonts.Values)
            font.Dispose();
        _fonts.Clear();
    }

    private Font FontFor(MarkdownPreviewStyle style)
    {
        FontStyle fontStyle = FontStyle.Regular;
        if (style.HasFlag(MarkdownPreviewStyle.Bold))
            fontStyle |= FontStyle.Bold;
        if (style.HasFlag(MarkdownPreviewStyle.Italic))
            fontStyle |= FontStyle.Italic;
        if (style.HasFlag(MarkdownPreviewStyle.Strikeout))
            fontStyle |= FontStyle.Strikeout;
        if (style.HasFlag(MarkdownPreviewStyle.Link))
            fontStyle |= FontStyle.Underline;

        int sizeDelta = style.HasFlag(MarkdownPreviewStyle.Heading1)
            ? 6
            : style.HasFlag(MarkdownPreviewStyle.Heading2)
                ? 3
                : style.HasFlag(MarkdownPreviewStyle.Heading3) ? 1 : 0;
        bool monospace = style.HasFlag(MarkdownPreviewStyle.Code);
        var key = (fontStyle, sizeDelta, monospace);
        if (_fonts.TryGetValue(key, out Font? font))
            return font;

        font = new Font(
            monospace ? "Cascadia Mono" : _normalFont.FontFamily.Name,
            _normalFont.Size + sizeDelta,
            fontStyle,
            GraphicsUnit.Point);
        _fonts.Add(key, font);
        return font;
    }

    private Color ForegroundColor(MarkdownPreviewStyle style)
    {
        if (style.HasFlag(MarkdownPreviewStyle.Link))
            return _dark ? Color.FromArgb(105, 169, 245) : Color.FromArgb(28, 91, 166);
        if (style.HasFlag(MarkdownPreviewStyle.Quote))
            return _dark ? Color.FromArgb(158, 166, 181) : Color.FromArgb(95, 104, 120);
        if (style.HasFlag(MarkdownPreviewStyle.Code))
            return _dark ? Color.FromArgb(229, 197, 120) : Color.FromArgb(122, 77, 24);
        return _textBox.ForeColor;
    }

    private Color CodeBackgroundColor()
        => _dark ? Color.FromArgb(42, 46, 54) : Color.FromArgb(242, 244, 247);

    private static IReadOnlyList<RichTextFadeRun> BuildFadeRuns(
        int documentStart,
        IReadOnlyList<Color>? colors)
    {
        if (colors is null || colors.Count == 0)
            return [];

        var result = new List<RichTextFadeRun>();
        int runStart = 0;
        Color current = colors[0];
        for (int index = 1; index <= colors.Count; index++)
        {
            if (index < colors.Count && colors[index] == current)
                continue;
            result.Add(new RichTextFadeRun(documentStart + runStart, index - runStart, current));
            if (index < colors.Count)
            {
                runStart = index;
                current = colors[index];
            }
        }
        return result;
    }
}
