using XREngine.Input.Devices;

namespace XREngine.Rendering.UI;

/// <summary>A menu action bound to engine keyboard keys.</summary>
public interface IMenuOption : IMenuItem, IMenu
{
    string Text { get; set; }
    EKey[] HotKeys { get; set; }
    void ExecuteAction();
}
