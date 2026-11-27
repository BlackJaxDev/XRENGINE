namespace XREngine.Rendering.UI;

/// <summary>
/// Per-element navigation settings and directional links editable by a UI inspector or flow-map editor.
/// </summary>
public sealed class UINavigationNodeFlow
{
    /// <summary>
    /// When false, gamepad and directional navigation skip this interactable.
    /// </summary>
    public bool AllowGamepadFocus { get; set; } = true;

    public UINavigationLink Up { get; set; }
    public UINavigationLink Down { get; set; }
    public UINavigationLink Left { get; set; }
    public UINavigationLink Right { get; set; }
    public UINavigationLink UpLeft { get; set; }
    public UINavigationLink UpRight { get; set; }
    public UINavigationLink DownLeft { get; set; }
    public UINavigationLink DownRight { get; set; }

    /// <summary>
    /// Gets the configured link for <paramref name="direction"/>.
    /// </summary>
    public UINavigationLink GetLink(EUINavigationDirection direction)
        => direction switch
        {
            EUINavigationDirection.Up => Up,
            EUINavigationDirection.Down => Down,
            EUINavigationDirection.Left => Left,
            EUINavigationDirection.Right => Right,
            EUINavigationDirection.UpLeft => UpLeft,
            EUINavigationDirection.UpRight => UpRight,
            EUINavigationDirection.DownLeft => DownLeft,
            EUINavigationDirection.DownRight => DownRight,
            _ => default,
        };

    /// <summary>
    /// Assigns the configured link for <paramref name="direction"/>.
    /// </summary>
    public void SetLink(EUINavigationDirection direction, UINavigationLink link)
    {
        switch (direction)
        {
            case EUINavigationDirection.Up: Up = link; break;
            case EUINavigationDirection.Down: Down = link; break;
            case EUINavigationDirection.Left: Left = link; break;
            case EUINavigationDirection.Right: Right = link; break;
            case EUINavigationDirection.UpLeft: UpLeft = link; break;
            case EUINavigationDirection.UpRight: UpRight = link; break;
            case EUINavigationDirection.DownLeft: DownLeft = link; break;
            case EUINavigationDirection.DownRight: DownRight = link; break;
        }
    }

    /// <summary>
    /// Sets an explicit target for <paramref name="direction"/>, or resets to <see cref="EUINavigationRule.Auto"/> when null.
    /// </summary>
    public void SetExplicitTarget(EUINavigationDirection direction, UIInteractableComponent? target)
        => SetLink(direction, target is not null ? UINavigationLink.Explicit(target) : UINavigationLink.Auto);

    /// <summary>
    /// Marks <paramref name="direction"/> as explicitly blocked on this element.
    /// </summary>
    public void Block(EUINavigationDirection direction)
        => SetLink(direction, UINavigationLink.Blocked);
}
