using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>A menu containing selectable items and nested menus.</summary>
public class MenuComponent : MenuItemComponent, IMenu
{
    public MenuItemComponent? HoveredMenuItem { get; set; }

    public void Show(UIComponent parent, Vector2 worldPosition, float z)
    {
    }

    public void Show(UICanvasTransform canvas, Vector2 worldPosition, float z)
        => Show(canvas, new Vector3(worldPosition, z));

    public void Show(UICanvasTransform canvas, Vector3 worldPosition)
    {
    }
}
