namespace XREngine.Rendering.UI;

/// <summary>Owner-thread semantics for an item that is not yet realized.</summary>
public readonly record struct UIVirtualAccessibilityItem(
    ulong ItemId, string Name, EUIAccessibilityRole Role,
    bool IsSelected = false, bool IsExpanded = false,
    bool HasChildren = false, bool IsSection = false, int Depth = 0,
    bool IsActive = false);
