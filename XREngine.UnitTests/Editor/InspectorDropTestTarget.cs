using XREngine.Data.Core;

namespace XREngine.UnitTests.Editor;

/// <summary>Publishes reference edits and permits reentry at getter and setter boundaries.</summary>
public sealed class InspectorDropTestTarget : XRBase
{
    private object? _reference;
    public Action? OnRead { get; set; }
    public Action? AfterWrite { get; set; }
    public object? Reference
    {
        get { OnRead?.Invoke(); return _reference; }
        set { SetField(ref _reference, value); AfterWrite?.Invoke(); }
    }
}
