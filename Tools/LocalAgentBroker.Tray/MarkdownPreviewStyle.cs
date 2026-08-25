namespace XREngine.LocalAgentBroker.Tray;

/// <summary>Visual intent for one Markdown preview run.</summary>
[Flags]
internal enum MarkdownPreviewStyle
{
    Normal = 0,
    Bold = 1 << 0,
    Italic = 1 << 1,
    Strikeout = 1 << 2,
    Code = 1 << 3,
    Link = 1 << 4,
    Quote = 1 << 5,
    Heading1 = 1 << 6,
    Heading2 = 1 << 7,
    Heading3 = 1 << 8,
}
