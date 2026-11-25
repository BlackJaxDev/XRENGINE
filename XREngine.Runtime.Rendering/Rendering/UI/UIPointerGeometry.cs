using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>Control geometry captured with one accepted UI hit.</summary>
public readonly record struct UIPointerGeometry(
    Vector2 LocalSize,
    UIScrollbarPointerMetrics? Scrollbar = null,
    UISplitterPointerMetrics? Splitter = null,
    object? Metadata = null);
