using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

internal sealed class UITextLifetimeBinding(string value) : IUIValueBinding<string>
{
    private Action<string>? _changed;
    public bool ThrowOnRemove { get; set; }
    public bool ThrowOnAdd { get; set; }
    public Action? OnAdd { get; set; }
    public Action? OnRemove { get; set; }
    public string Value { get; private set; } = value;
    public int ListenerCount => _changed?.GetInvocationList().Length ?? 0;
    public Action<string>? CaptureDelivery() => _changed;

    public event Action<string>? ValueChanged
    {
        add
        {
            _changed += value;
            OnAdd?.Invoke();
            if (ThrowOnAdd)
                throw new InvalidOperationException("Injected text binding add failure.");
        }
        remove
        {
            _changed -= value;
            OnRemove?.Invoke();
            if (ThrowOnRemove)
                throw new InvalidOperationException("Injected text binding removal failure.");
        }
    }

    public void SetValue(string value)
    {
        Value = value;
        _changed?.Invoke(value);
    }
}
