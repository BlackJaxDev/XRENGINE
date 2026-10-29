using System.Collections.Immutable;

namespace XREngine.Rendering.UI;

/// <summary>Numeric glyph data for one analyzed direction run.</summary>
public sealed class UITextShapedRunInput
{
    public UITextShapedRunInput(UITextShapingResult result, string? featureSource = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        SourceText = result.Request.Text;
        FaceContentIdentity = result.Request.Face.ContentIdentity;
        Language = result.Request.Language;
        Direction = result.Request.Direction;
        ParagraphAnalysis = result.Request.ParagraphAnalysis;
        HasMeasuredHardLines = result.HasMeasuredHardLines;
        Features = result.Request.Features;
        FeatureSource = featureSource;
        Glyphs = ImmutableArray.CreateRange(result.Glyphs.ToArray());
    }

    private UITextShapedRunInput(UITextShapedRunInput parent, string sourceText,
        UITextShapedGlyph[] glyphs)
    {
        SourceText = sourceText;
        FaceContentIdentity = parent.FaceContentIdentity;
        Language = parent.Language;
        Direction = parent.Direction;
        Features = parent.Features;
        FeatureSource = parent.FeatureSource;
        Glyphs = ImmutableArray.CreateRange(glyphs);
    }

    /// <summary>Rebase one independently shaped hard line for the single-line placement kernel.</summary>
    internal UITextShapedRunInput SliceLine(UITextSourceLine line, int glyphStart, int glyphCount)
    {
        UITextShapedGlyph[] glyphs = new UITextShapedGlyph[glyphCount];
        for (int i = 0; i < glyphs.Length; i++)
            glyphs[i] = Glyphs[glyphStart + i] with
            {
                ClusterStart = Glyphs[glyphStart + i].ClusterStart - line.Start
            };
        return new UITextShapedRunInput(this, SourceText.Substring(line.Start, line.Length), glyphs);
    }

    public string SourceText { get; }
    public string FaceContentIdentity { get; }
    public string Language { get; }
    public UITextDirection Direction { get; }
    /// <summary>Captured UTF-16 analysis used to admit one visual direction run.</summary>
    public UITextParagraphAnalysis? ParagraphAnalysis { get; }
    public bool HasMeasuredHardLines { get; }
    public ImmutableArray<string> Features { get; }
    public string? FeatureSource { get; }
    public ImmutableArray<UITextShapedGlyph> Glyphs { get; }
}
