namespace XREngine.Rendering.UI;

/// <summary>
/// Frozen child order and grid placement. Provider-backed list rows also carry their logical
/// main-axis start and extent, before the scroll offset is applied.
/// </summary>
internal readonly record struct UILayoutChildInput(int NodeIndex, int Row, int Column, int RowSpan, int ColumnSpan,
    float ListStart = 0, float ListExtent = 0);
