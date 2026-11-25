using System.ComponentModel;

namespace XREngine.UnitTests.Editor;

/// <summary>Permits retirement from an application-defined subscription accessor.</summary>
public sealed class InspectorSubscriptionDropTarget : INotifyPropertyChanged
{
    private PropertyChangedEventHandler? _changed;
    public Action? OnSubscribe { get; set; }
    public object? Reference { get; set; }
    public int SubscriberCount => _changed?.GetInvocationList().Length ?? 0;
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { OnSubscribe?.Invoke(); _changed += value; }
        remove { _changed -= value; }
    }
}
