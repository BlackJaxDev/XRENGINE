using Silk.NET.Input;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine;

public sealed class XRMenuSeparator : XRMenuItem { }
public abstract class XRMenuButton : XRInteractableMenuItem
{
    public abstract void Execute();
}
public abstract class XRMenuToggle : XRInteractableMenuItem
{
    public abstract void Toggled(bool on);
}
public abstract class XRInteractableMenuItem : XRMenuItem
{
    private bool _isEnabled;
    /// <summary>
    /// Whether the menu item is clickable.
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    private XRTexture2D? _icon;
    /// <summary>
    /// The icon to display for the menu item.
    /// </summary>
    public XRTexture2D? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    private string? _tooltip;
    /// <summary>
    /// The tooltip to display for the menu item.
    /// </summary>
    public string? Tooltip
    {
        get => _tooltip;
        set => SetField(ref _tooltip, value);
    }

    private Key[]? _shortcutKeys;
    /// <summary>
    /// The shortcut keys to trigger the menu item.
    /// </summary>
    public Key[]? ShortcutKeys
    {
        get => _shortcutKeys;
        set => SetField(ref _shortcutKeys, value);
    }

    public abstract void OnHoverEnter();
    public abstract void OnHoverExit();
    public abstract void OnMouseDown();
    public abstract void OnMouseUp();
    public abstract void OnGamepadDown();
    public abstract void OnGamepadUp();
    public abstract void OnFocusEnter();
    public abstract void OnFocusExit();
}
public abstract class XRMenuItem : XRBase
{
    private string? _path;
    /// <summary>
    /// The path to the menu item.
    /// </summary>
    public string? Path
    {
        get => _path;
        set => SetField(ref _path, value);
    }

    private bool _isVisible = true;
    /// <summary>
    /// Whether the menu item is visible.
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetField(ref _isVisible, value);
    }

    private int _order = 0;
    /// <summary>
    /// The order of the menu item.
    /// Prioritizes the order of the menu item in the menu;
    /// 0 is the highest priority and will be displayed first, followed by 1, 2, etc.
    /// Matches are sorted alphabetically.
    /// </summary>
    public int Order 
    {
        get => _order;
        set => SetField(ref _order, value);
    }
}
