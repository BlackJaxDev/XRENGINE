namespace XREngine.Rendering.UI;

/// <summary>A shared divider between menu item groups.</summary>
public sealed class MenuDivider : MenuItemComponent, IMenuDivider
{
    public static MenuDivider Instance { get; } = new();
    private MenuDivider() { }
}
