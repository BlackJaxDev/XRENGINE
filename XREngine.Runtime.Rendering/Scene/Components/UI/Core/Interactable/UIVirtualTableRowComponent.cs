namespace XREngine.Rendering.UI;

/// <summary>Focusable table row; the virtual list resets it before a row lease is reused.</summary>
public sealed class UIVirtualTableRowComponent : UIButtonComponent
{
    public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.TableRow;
}
