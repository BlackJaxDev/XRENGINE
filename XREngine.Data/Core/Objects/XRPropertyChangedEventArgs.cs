using System.ComponentModel;

namespace XREngine.Data.Core
{
    public interface IXRPropertyChangedEventArgs
    {
        string? PropertyName { get; }
        object? PreviousValue { get; }
        object? NewValue { get; }
    }
    public class XRPropertyChangedEventArgs<T>(string? propertyName, T prev, T newValue) : PropertyChangedEventArgs(propertyName), IXRPropertyChangedEventArgs
    {
        public T PreviousValue { get; } = prev;
        public T NewValue { get; } = newValue;
        string? IXRPropertyChangedEventArgs.PropertyName { get; } = propertyName;
        object? IXRPropertyChangedEventArgs.PreviousValue { get; } = prev;
        object? IXRPropertyChangedEventArgs.NewValue { get; } = newValue;
    }
}
