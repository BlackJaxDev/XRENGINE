namespace XREngine.Rendering.UI;

/// <summary>Supplies a typed value and reports changes to a retained control.</summary>
public interface IUIValueBinding<T>
{
    T Value { get; }
    event Action<T>? ValueChanged;
    void SetValue(T value);
}
