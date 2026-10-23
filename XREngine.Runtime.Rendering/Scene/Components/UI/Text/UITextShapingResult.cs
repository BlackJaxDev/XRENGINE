namespace XREngine.Rendering.UI;

/// <summary>Visual glyph order for one direction run.</summary>
public sealed class UITextShapingResult
{
    private readonly UITextShapedGlyph[] _glyphs;

    public UITextShapingResult(UITextShapingRequest request, UITextShapedGlyph[] glyphs)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        ArgumentNullException.ThrowIfNull(glyphs);
        if (glyphs.Length > (long)request.Text.Length * 4)
            throw new NotSupportedException("UI.ShapedGlyphCountExceeded: one run exceeds its glyph budget.");
        _glyphs = (UITextShapedGlyph[])glyphs.Clone();
    }

    public UITextShapingRequest Request { get; }
    public ReadOnlyMemory<UITextShapedGlyph> Glyphs => _glyphs;
}
