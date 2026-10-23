using System.Collections.Frozen;
using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>Owner-captured values used by text measurement and glyph placement.</summary>
public sealed record UITextLayoutInput(
    string Text,
    FrozenDictionary<int, UIRuneMetrics> Runes,
    long TextRevision,
    long FontRevision,
    float EmSize,
    Vector2 AtlasSize,
    float? FontSize,
    FontGlyphSet.EWrapMode Wrap,
    float Spacing,
    float LineSpacing,
    EHorizontalAlignment HorizontalAlignment,
    EVerticalAlignment VerticalAlignment,
    Vector4 Margins,
    float DpiScale = 1.0f,
    bool TrimFirstBearing = true);
