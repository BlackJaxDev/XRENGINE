namespace XREngine.Rendering.UI;

/// <summary>Visual glyph order for one direction run.</summary>
public sealed class UITextShapingResult
{
    private readonly UITextShapedGlyph[] _glyphs;

    public UITextShapingResult(UITextShapingRequest request, UITextShapedGlyph[] glyphs)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        _glyphs = (UITextShapedGlyph[])glyphs.Clone();
    }

    public UITextShapingRequest Request { get; }
    public ReadOnlyMemory<UITextShapedGlyph> Glyphs => _glyphs;
}
