namespace XREngine.Rendering.UI;

/// <summary>Names virtual items without realizing rows; queried on the canvas owner thread.</summary>
public interface IUIVirtualAccessibilityProvider
{
    int AccessibilityItemCount { get; }
    bool TryGetAccessibilityItem(int index, out UIVirtualAccessibilityItem item);
}

public readonly record struct UIVirtualAccessibilityItem(
    ulong ItemId, string Name, EUIAccessibilityRole Role);
