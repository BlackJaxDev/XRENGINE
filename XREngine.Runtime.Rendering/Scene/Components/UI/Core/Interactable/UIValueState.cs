namespace XREngine.Rendering.UI;

/// <summary>Stores one typed UI value on the UI owner thread.</summary>
public sealed class UIValueState<T> : IUIValueBinding<T>
{
    private T _value;

    public UIValueState(T value)
    {
        _value = value;
    }

    public T Value => _value;
    public event Action<T>? ValueChanged;

    public void SetValue(T value)
    {
        if (EqualityComparer<T>.Default.Equals(_value, value))
            return;
        _value = value;
        ValueChanged?.Invoke(value);
    }
}
