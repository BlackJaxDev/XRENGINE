namespace XREngine.Editor;

public enum EVRActionCategory
{
    /// <summary>
    /// Global actions are always available.
    /// </summary>
    Global,
    /// <summary>
    /// Actions that are only available when one controller is off.
    /// </summary>
    OneHanded,
    /// <summary>
    /// Actions that are enabled when the quick menu (the menu on the controller) is open.
    /// </summary>
    QuickMenu,
    /// <summary>
    /// Actions that are enabled when the main menu is fully open.
    /// </summary>
    Menu,
    /// <summary>
    /// Actions that are enabled when the avatar's menu is open.
    /// </summary>
    AvatarMenu,
}
