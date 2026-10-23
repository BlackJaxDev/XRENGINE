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
            !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("A language is required."));
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

    private UITextShapedRunInput CaptureShapedRun(string text, FontGlyphSet font,
        string language, UITextDirection direction, string? featureSource,
        IUITextShapingBackend backend)
    {
        if (WrapMode != FontGlyphSet.EWrapMode.None || UITextShapedLayoutKernel.HasLineBreak(text))
            throw new NotSupportedException("UI.ShapedLineAnalysisUnavailable: wrapped and multiline text require line analysis.");
        EnsureOneDirection(text, direction);
        string[] features = string.IsNullOrWhiteSpace(featureSource) ? [] :
            featureSource.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        UITextShapingResult shaped = backend.Shape(new UITextShapingRequest(text,
            GetRequiredShapingFace(font), language, direction, features));
        return new UITextShapedRunInput(shaped, featureSource);
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
