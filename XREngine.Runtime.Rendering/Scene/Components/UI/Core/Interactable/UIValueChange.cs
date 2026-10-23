namespace XREngine.Rendering.UI;

/// <summary>Reports the old value, new value, and source of one control change.</summary>
public readonly record struct UIValueChange<T>(T OldValue, T NewValue, EUIValueChangeOrigin Origin);
