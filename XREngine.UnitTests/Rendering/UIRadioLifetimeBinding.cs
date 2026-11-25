using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

internal sealed class UIRadioLifetimeBinding(ulong? value) : IUIValueBinding<ulong?>
{
    private Action<ulong?>? _changed;
    public bool ThrowOnRemove { get; set; }
    private ulong? _value = value;
    public Action? OnRead { get; set; }
    public ulong? Value { get { OnRead?.Invoke(); return _value; } }
    public Action<ulong?>? CaptureDelivery() => _changed;

    public event Action<ulong?>? ValueChanged
    {
        add => _changed += value;
        remove
        {
            _changed -= value;
            if (ThrowOnRemove)
                throw new InvalidOperationException("Injected binding removal failure.");
        }
    }

    public void SetValue(ulong? value)
    {
        _value = value;
        _changed?.Invoke(value);
    }
}
