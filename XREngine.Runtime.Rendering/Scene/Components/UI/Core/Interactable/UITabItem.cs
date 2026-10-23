namespace XREngine.Rendering.UI;

/// <summary>Stable identity and display state of one retained tab.</summary>
public readonly record struct UITabItem(ulong Id, string Title, bool IsOpen = true);
