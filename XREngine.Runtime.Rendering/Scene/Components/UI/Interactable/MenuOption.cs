using XREngine.Input.Devices;

namespace XREngine.Rendering.UI;

/// <summary>A selectable menu action with portable engine key bindings.</summary>
public class MenuOption(string? text = null, Action? action = null, params EKey[] hotKeys) : MenuComponent, IMenuOption
{
    private string _text = text ?? string.Empty;
    private EKey[] _hotKeys = hotKeys;
    private Action? _action = action;

    public string Text
    {
        get => _text;
        set => SetField(ref _text, value);
    }

    public EKey[] HotKeys
    {
        get => _hotKeys;
        set => SetField(ref _hotKeys, value);
    }

    public Action? Action
    {
        get => _action;
        set => SetField(ref _action, value);
    }

    public void ExecuteAction() => Action?.Invoke();
}
