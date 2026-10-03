namespace XREngine.Rendering.UI;

/// <summary>Receives menu opening and closing notifications.</summary>
public interface IMenuItem
{
    event Action<MenuItemComponent> Opening;
    event Action<MenuItemComponent> Closing;

    void OnOpening();
    void OnClosing();
}
