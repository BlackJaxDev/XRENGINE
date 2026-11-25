using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

internal sealed class ReentrantToggleValueBinding(UIToggleComponent.ECurrentState value)
    : IUIValueBinding<UIToggleComponent.ECurrentState>
{
    private Action<UIToggleComponent.ECurrentState>? _changed;
    private UIToggleComponent.ECurrentState _value = value;

    public Action? OnAdd { get; set; }
    public Action? OnRemove { get; set; }
    public int SubscriberCount => _changed?.GetInvocationList().Length ?? 0;
    public UIToggleComponent.ECurrentState Value => _value;

    public event Action<UIToggleComponent.ECurrentState>? ValueChanged
    {
        add
        {
            _changed += value;
            OnAdd?.Invoke();
        }
        remove
        {
            _changed -= value;
            OnRemove?.Invoke();
        }
    }

    public void SetValue(UIToggleComponent.ECurrentState value)
    {
        if (_value == value)
            return;
        _value = value;
        _changed?.Invoke(value);
    }
}
