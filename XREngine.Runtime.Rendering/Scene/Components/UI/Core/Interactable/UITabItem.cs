namespace XREngine.Rendering.UI;

/// <summary>
/// Stable identity and display state of one retained tab. <paramref name="Icon"/> is a glyph string of the strip's
/// <see cref="UITabStripComponent.IconFont"/>, drawn before the title, or null for no icon.
/// </summary>
public readonly record struct UITabItem(ulong Id, string Title, bool IsOpen = true, string? Icon = null);
