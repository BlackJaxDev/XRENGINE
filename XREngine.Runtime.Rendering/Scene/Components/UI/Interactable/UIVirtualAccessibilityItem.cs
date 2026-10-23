namespace XREngine.Rendering.UI;

/// <summary>Owner-thread semantics for an item that is not yet realized.</summary>
public readonly record struct UIVirtualAccessibilityItem(
    ulong ItemId, string Name, EUIAccessibilityRole Role);
