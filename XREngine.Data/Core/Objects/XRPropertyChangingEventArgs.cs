using System.ComponentModel;

namespace XREngine.Data.Core
{
    public interface IXRPropertyChangingEventArgs
    {
        string? PropertyName { get; }
        object? CurrentValue { get; }
        object? NewValue { get; }
    }
    public class XRPropertyChangingEventArgs<T>(string? propertyName, T currentValue, T newValue) : PropertyChangingEventArgs(propertyName), IXRPropertyChangingEventArgs
    {
        public T CurrentValue { get; } = currentValue;
        public T NewValue { get; } = newValue;
        public bool AllowChange { get; set; } = true;
        string? IXRPropertyChangingEventArgs.PropertyName { get; } = propertyName;
        object? IXRPropertyChangingEventArgs.CurrentValue { get; } = currentValue;
        object? IXRPropertyChangingEventArgs.NewValue { get; } = newValue;
    }
}
