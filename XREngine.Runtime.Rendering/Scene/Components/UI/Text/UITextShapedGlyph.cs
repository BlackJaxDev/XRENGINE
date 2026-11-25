using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>One shaped glyph with its atlas data and source cluster.</summary>
public readonly record struct UITextShapedGlyph(uint GlyphId, UIRuneMetrics Metrics,
    Vector2 Advance, Vector2 Offset, int ClusterStart, int ClusterLength);
