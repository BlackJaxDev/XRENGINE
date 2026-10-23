using System.Reflection;
using XREngine.Rendering.UI;

namespace XREngine.Editor.UI;

/// <summary>Connects a retained checkbox to cached editor property access.</summary>
internal sealed class EditorTogglePropertyBinding : IUIValueBinding<UIToggleComponent.ECurrentState>
{
    private readonly EditorPropertyAccess _access;
    private Action<UIToggleComponent.ECurrentState>? _changed;

    public EditorTogglePropertyBinding(PropertyInfo property, object?[] targets)
    {
        if (property.PropertyType != typeof(bool))
            throw new ArgumentException("A Boolean property is required.", nameof(property));
        _access = new(property, targets);
    }

    public UIToggleComponent.ECurrentState Value
        => !_access.TryGetCommonValue(out object? value) ? UIToggleComponent.ECurrentState.Intermediate :
            value is true ? UIToggleComponent.ECurrentState.True : UIToggleComponent.ECurrentState.False;

    public event Action<UIToggleComponent.ECurrentState>? ValueChanged
    {
        add
        {
            if (_changed is null)
                _access.Changed += Notify;
            _changed += value;
        }
        remove
        {
            _changed -= value;
            if (_changed is null)
                _access.Changed -= Notify;
        }
    }

    public void SetValue(UIToggleComponent.ECurrentState value)
    {
        if (value != UIToggleComponent.ECurrentState.Intermediate)
            _access.SetValue(value == UIToggleComponent.ECurrentState.True);
    }

    private void Notify() => _changed?.Invoke(Value);
}
