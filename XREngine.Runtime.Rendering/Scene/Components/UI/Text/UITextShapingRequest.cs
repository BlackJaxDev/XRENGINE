using System.Collections.Immutable;

namespace XREngine.Rendering.UI;

/// <summary>Owner-captured inputs for one font and direction run.</summary>
public sealed class UITextShapingRequest
{
    public UITextShapingRequest(string text, UIFontFaceSnapshot face, string language,
        UITextDirection direction, IEnumerable<string>? features = null)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Face = face ?? throw new ArgumentNullException(nameof(face));
        Language = !string.IsNullOrWhiteSpace(language) ? language :
            throw new ArgumentException("A language is required.", nameof(language));
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        Direction = direction;
        Features = features?.ToImmutableArray() ?? ImmutableArray<string>.Empty;
    }

    public string Text { get; }
    public UIFontFaceSnapshot Face { get; }
    public string Language { get; }
    public UITextDirection Direction { get; }
    public ImmutableArray<string> Features { get; }
}
