namespace XREngine.Rendering.UI;

/// <summary>Focusable tab with its selection exposed to accessibility services.</summary>
public sealed class UITabButtonComponent : UIButtonComponent
{
    public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.Tab;
    public override string? AccessibilityValue => IsSelected ? "Selected" : "Not selected";
}
