using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>Immutable text result. Intrinsic size is measured before alignment.</summary>
public sealed class UITextLayoutResult
{
    public UITextLayoutResult(Vector2 intrinsicSize, UITextLayoutGlyph[] glyphs,
        UITextCaretStop[] carets, long textRevision, long fontRevision)
        : this(intrinsicSize, ((UITextLayoutGlyph[])glyphs.Clone()).AsMemory(),
            ((UITextCaretStop[])carets.Clone()).AsMemory(), textRevision, fontRevision)
    {
    }

    private UITextLayoutResult(Vector2 intrinsicSize, ReadOnlyMemory<UITextLayoutGlyph> glyphs,
        ReadOnlyMemory<UITextCaretStop> carets, long textRevision, long fontRevision)
    {
        IntrinsicSize = intrinsicSize;
        Glyphs = glyphs;
        Carets = carets;
        TextRevision = textRevision;
        FontRevision = fontRevision;
    }

    internal static UITextLayoutResult FromOwned(Vector2 intrinsicSize, UITextLayoutGlyph[] glyphs,
        UITextCaretStop[] carets, long textRevision, long fontRevision)
        => new(intrinsicSize, glyphs.AsMemory(), carets.AsMemory(), textRevision, fontRevision);

    public Vector2 IntrinsicSize { get; }
    public ReadOnlyMemory<UITextLayoutGlyph> Glyphs { get; }
    public ReadOnlyMemory<UITextCaretStop> Carets { get; }
    public long TextRevision { get; }
    public long FontRevision { get; }
}
