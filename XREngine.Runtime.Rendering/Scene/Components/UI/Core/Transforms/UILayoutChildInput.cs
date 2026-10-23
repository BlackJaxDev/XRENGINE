namespace XREngine.Rendering.UI;

/// <summary>Frozen child order and grid placement.</summary>
internal readonly record struct UILayoutChildInput(int NodeIndex, int Row, int Column, int RowSpan, int ColumnSpan);
