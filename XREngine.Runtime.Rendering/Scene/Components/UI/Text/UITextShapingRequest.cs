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
        Language = !string.IsNullOrWhiteSpace(language) && language.Length <= 64 ? language :
            throw new ArgumentException("A language of at most 64 characters is required.", nameof(language));
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        Direction = direction;
        ImmutableArray<string>.Builder builder = ImmutableArray.CreateBuilder<string>();
        if (features is not null)
            foreach (string feature in features)
            {
                if (builder.Count == 32 || feature.Length > 64)
                    throw new NotSupportedException("UI.ShapingFeaturesExceeded: feature count or length exceeds its limit.");
                builder.Add(feature);
            }
        Features = builder.ToImmutable();
    }

    public string Text { get; }
    public UIFontFaceSnapshot Face { get; }
    public string Language { get; }
    public UITextDirection Direction { get; }
    public ImmutableArray<string> Features { get; }
}
