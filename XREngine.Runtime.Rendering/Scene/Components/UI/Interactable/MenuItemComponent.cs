namespace XREngine.Rendering.UI;

/// <summary>Publishes menu opening and closing notifications.</summary>
public class MenuItemComponent : UIButtonComponent, IMenuItem
{
    public event Action<MenuItemComponent>? Opening;
    public event Action<MenuItemComponent>? Closing;

    public virtual void OnClosing() => Closing?.Invoke(this);
    public virtual void OnOpening() => Opening?.Invoke(this);
}
