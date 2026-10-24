using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

internal sealed class UIRadioLifetimeBinding(ulong? value) : IUIValueBinding<ulong?>
{
    private Action<ulong?>? _changed;
    public bool ThrowOnRemove { get; set; }
    public ulong? Value { get; private set; } = value;
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
        Value = value;
        _changed?.Invoke(value);
    }
}
