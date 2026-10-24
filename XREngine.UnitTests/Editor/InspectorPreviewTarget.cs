using System.ComponentModel;

namespace XREngine.UnitTests.Editor;

/// <summary>Exposes observable and reentrant reads for inspector preview tests.</summary>
public sealed class InspectorPreviewTarget : INotifyPropertyChanged
{
    private string _value = string.Empty;
    public Action? OnRead { get; set; }
    public string Value
    {
        get { OnRead?.Invoke(); return _value; }
        set { _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
