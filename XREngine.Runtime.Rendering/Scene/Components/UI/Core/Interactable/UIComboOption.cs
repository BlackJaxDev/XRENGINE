namespace XREngine.Rendering.UI;

/// <summary>One stable option shown by a native combo box.</summary>
public readonly record struct UIComboOption(ulong Id, string Label, bool IsEnabled = true);
