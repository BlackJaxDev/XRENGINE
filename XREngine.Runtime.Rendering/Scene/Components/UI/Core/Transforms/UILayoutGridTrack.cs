namespace XREngine.Rendering.UI;

/// <summary>Frozen sizing rule for one grid row or column.</summary>
internal readonly record struct UILayoutGridTrack(
    ESizingMode Mode, float Value,
    ESizingMode? MinMode, float MinValue,
    ESizingMode? MaxMode, float MaxValue);
