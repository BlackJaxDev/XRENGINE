using System.Collections.Immutable;

namespace XREngine.Rendering.UI;

/// <summary>Numeric glyph data for one analyzed direction run.</summary>
public sealed class UITextShapedRunInput
{
    public UITextShapedRunInput(UITextShapingResult result, string? featureSource = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        FaceContentIdentity = result.Request.Face.ContentIdentity;
        Language = result.Request.Language;
        Direction = result.Request.Direction;
        Features = result.Request.Features;
        FeatureSource = featureSource;
        Glyphs = ImmutableArray.CreateRange(result.Glyphs.ToArray());
    }

    public string FaceContentIdentity { get; }
    public string Language { get; }
    public UITextDirection Direction { get; }
    public ImmutableArray<string> Features { get; }
    public string? FeatureSource { get; }
    public ImmutableArray<UITextShapedGlyph> Glyphs { get; }
}
