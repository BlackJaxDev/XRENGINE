using System.Text;

namespace XREngine.Rendering.UI;

public partial class UITextComponent
{
    private bool _useShapedText;
    private string _shapeLanguage = "en-US";
    private UITextDirection _shapeDirection;
    private string? _openTypeFeatures;

    /// <summary>Use the installed shaping backend for this text run.</summary>
    public bool UseShapedText
    {
        get => _useShapedText;
        set => SetField(ref _useShapedText, value);
    }

    /// <summary>BCP 47 language for a single analyzed run.</summary>
    public string ShapeLanguage
    {
        get => _shapeLanguage;
        set => SetField(ref _shapeLanguage,
            !string.IsNullOrWhiteSpace(value) && value.Length <= 64 ? value :
                throw new ArgumentException("A language of at most 64 characters is required."));
    }

    /// <summary>Direction for one text run.</summary>
    public UITextDirection ShapeDirection
    {
        get => _shapeDirection;
        set => SetField(ref _shapeDirection, value);
    }

    /// <summary>Comma-separated OpenType features such as liga=0.</summary>
    public string? OpenTypeFeatures
    {
        get => _openTypeFeatures;
        set => SetField(ref _openTypeFeatures, value);
    }

    private static UIFontFaceSnapshot GetRequiredShapingFace(FontGlyphSet font)
    {
        if (font.TryGetRuntimeShapingFace(out UIFontFaceSnapshot face))
            return face;
        throw new NotSupportedException("UI.ShapingFaceUnavailable: this font has no glyph-ID face snapshot.");
    }

    private UITextShapingRequest CaptureShapingRequest(string text, FontGlyphSet font,
        string language, UITextDirection direction, string? featureSource,
        IUITextWorkerShapingBackend backend)
    {
        bool hardLines = UITextShapedLayoutKernel.HasLineBreak(text);
        if (WrapMode != FontGlyphSet.EWrapMode.None ||
            hardLines && backend is not IUITextHardLineShapingBackend)
            throw new NotSupportedException("UI.ShapedLineAnalysisUnavailable: this backend cannot place wrapped or hard-delimited text.");
        EnsureOneDirection(text, direction);
        UITextParagraphAnalysis? paragraph = null;
        if (UITextParagraphAnalyzerRegistry.Current is { } analyzer)
        {
            paragraph = analyzer.Analyze(text, language, direction);
            int expectedParity = direction == UITextDirection.RightToLeft ? 1 : 0;
            foreach (UITextParagraphRun run in paragraph.Runs.Span)
                if ((run.ResolvedBidiLevel & 1) != expectedParity)
                    throw new NotSupportedException(
                        "UI.BidiAnalysisUnavailable: resolved direction runs require paragraph placement.");
        }
        if (hardLines && paragraph is null)
            throw new NotSupportedException("UI.ShapedLineAnalysisUnavailable: hard lines require a registered paragraph analyzer.");
        if (featureSource is { Length: > 2048 })
            throw new NotSupportedException("UI.ShapingFeaturesExceeded: feature text exceeds 2048 characters.");
        int featureCount = 0;
        if (!string.IsNullOrWhiteSpace(featureSource))
        {
            bool hasContent = false;
            foreach (char value in featureSource)
            {
                if (value == ',')
                {
                    if (hasContent && ++featureCount > 32)
                        throw new NotSupportedException("UI.ShapingFeaturesExceeded: more than 32 features.");
                    hasContent = false;
                }
                else if (!char.IsWhiteSpace(value))
                    hasContent = true;
            }
            if (hasContent && ++featureCount > 32)
                throw new NotSupportedException("UI.ShapingFeaturesExceeded: more than 32 features.");
        }
        string[] features = string.IsNullOrWhiteSpace(featureSource) ? [] :
            featureSource.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new UITextShapingRequest(text, GetRequiredShapingFace(font), language, direction, features, paragraph);
    }

    internal static void EnsureOneDirection(string text, UITextDirection direction)
    {
        bool hasRtl = false;
        bool hasLtr = false;
        bool hasDigit = false;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int value = rune.Value;
            if (value is 0x061C or 0x200E or 0x200F or >= 0x202A and <= 0x202E or
                >= 0x2066 and <= 0x2069)
                throw new NotSupportedException("UI.BidiAnalysisUnavailable: bidi controls require paragraph analysis.");
            if (value is >= 0x0590 and <= 0x08FF or >= 0xFB1D and <= 0xFEFC)
                hasRtl = true;
            else if (Rune.IsLetter(rune))
                hasLtr = true;
            if (Rune.IsDigit(rune))
                hasDigit = true;
        }
        if (hasRtl && (hasLtr || hasDigit || direction != UITextDirection.RightToLeft) ||
            (hasLtr || hasDigit) && direction == UITextDirection.RightToLeft)
            throw new NotSupportedException("UI.BidiAnalysisUnavailable: split text into analyzed direction runs.");
    }
}
