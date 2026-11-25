using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>Immutable text result. Intrinsic size is measured before alignment.</summary>
public sealed class UITextLayoutResult
{
    public UITextLayoutResult(Vector2 intrinsicSize, UITextLayoutGlyph[] glyphs,
        UITextCaretStop[] carets, long textRevision, long fontRevision, bool hasVisualCarets = false,
        bool usesShapedGlyphs = false)
        : this(intrinsicSize, ((UITextLayoutGlyph[])glyphs.Clone()).AsMemory(),
            ((UITextCaretStop[])carets.Clone()).AsMemory(), textRevision, fontRevision,
            hasVisualCarets, usesShapedGlyphs)
    {
    }

    private UITextLayoutResult(Vector2 intrinsicSize, ReadOnlyMemory<UITextLayoutGlyph> glyphs,
        ReadOnlyMemory<UITextCaretStop> carets, long textRevision, long fontRevision,
        bool hasVisualCarets, bool usesShapedGlyphs)
    {
        IntrinsicSize = intrinsicSize;
        Glyphs = glyphs;
        Carets = carets;
        TextRevision = textRevision;
        FontRevision = fontRevision;
        HasVisualCarets = hasVisualCarets;
        UsesShapedGlyphs = usesShapedGlyphs;
    }

    internal static UITextLayoutResult FromOwned(Vector2 intrinsicSize, UITextLayoutGlyph[] glyphs,
        UITextCaretStop[] carets, long textRevision, long fontRevision, bool hasVisualCarets = false,
        bool usesShapedGlyphs = false)
        => new(intrinsicSize, glyphs.AsMemory(), carets.AsMemory(), textRevision, fontRevision,
            hasVisualCarets, usesShapedGlyphs);

    /// <summary>Returns a result that shares this glyph and caret storage and carries other revisions.</summary>
    internal UITextLayoutResult WithRevisions(long textRevision, long fontRevision)
        => new(IntrinsicSize, Glyphs, Carets, textRevision, fontRevision, HasVisualCarets, UsesShapedGlyphs);

    public Vector2 IntrinsicSize { get; }
    public ReadOnlyMemory<UITextLayoutGlyph> Glyphs { get; }
    public ReadOnlyMemory<UITextCaretStop> Carets { get; }
    public long TextRevision { get; }
    public long FontRevision { get; }
    public bool HasVisualCarets { get; }
    public bool UsesShapedGlyphs { get; }
    public bool HasCompleteGraphemeCaretStops => !UsesShapedGlyphs || HasVisualCarets;
}
