using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

internal sealed class UITextReentrantDraftBinding(string value) : IUIValueBinding<string>
{
    private string _value = value;
    public Action? OnGet { get; set; }
    public Action? OnSet { get; set; }
    public string Value
    {
        get
        {
            OnGet?.Invoke();
            return _value;
        }
    }
    public event Action<string>? ValueChanged;

    public void SetValue(string value)
    {
        _value = value;
        OnSet?.Invoke();
        ValueChanged?.Invoke(value);
    }
}
