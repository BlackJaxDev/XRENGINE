using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>Numeric metrics for one rune. No font or atlas object is retained.</summary>
public readonly record struct UIRuneMetrics(Vector2 Bearing, Vector2 Size, float Advance, Vector4 AtlasRect);
