using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XREngine.Data.Core
{
    public interface IXRNotifyPropertyChanged
    {
        event XRPropertyChangedEventHandler? PropertyChanged;
    }
    public interface IXRNotifyPropertyChanging
    {
        event XRPropertyChangingEventHandler? PropertyChanging;
    }
    public delegate void XRPropertyChangedEventHandler(object? sender, IXRPropertyChangedEventArgs e);
    public delegate void XRPropertyChangingEventHandler(object? sender, IXRPropertyChangingEventArgs e);
    /// <summary>
    /// Common base class for objects. Contains special handling for setting fields and notifying listeners of changes.
    /// </summary>
    [Serializable]
    public abstract class XRBase : IXRNotifyPropertyChanged, IXRNotifyPropertyChanging
    {
        /// <summary>
        /// This event is called after the value of a property's backing field changes.
        /// </summary>
        public event XRPropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// This event is called before the value of a property's backing field changes.
        /// </summary>
        public event XRPropertyChangingEventHandler? PropertyChanging;

        /// <summary>
        /// Helper method to set a field.
        /// Verifies if the value is changing and calls PropertyChanging, which checks if PropertyChanged should be called.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="field"></param>
        /// <param name="value"></param>
        /// <param name="propertyName"></param>
        /// <returns></returns>
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (ReferenceEquals(field, value))
                return false;

            if (!OnPropertyChanging(propertyName, field, value))
                return false;

            T prev = field;
            field = value;
            OnPropertyChanged(propertyName, prev, field);
            return true;
        }

        protected bool SetFieldUnchecked<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            OnPropertyChanging(propertyName, field, value);
            T prev = field;
            field = value;
            OnPropertyChanged(propertyName, prev, field);
            return true;
        }

        protected T SetFieldReturn<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (ReferenceEquals(field, value))
                return field;

            if (!OnPropertyChanging(propertyName, field, value))
                return field;

            T prev = field;
            field = value;
            OnPropertyChanged(propertyName, prev, field);
            return field;
        }

        protected T SetFieldReturn<T>(ref T field, T value, Action<T> beforeChanged, Action<T> afterChanged, [CallerMemberName] string? propertyName = null)
        {
            if (ReferenceEquals(field, value))
                return field;

            if (!OnPropertyChanging(propertyName, field, value))
                return field;

            T prev = field;
            beforeChanged?.Invoke(prev);
            field = value;
            OnPropertyChanged(propertyName, prev, field);

            if (field is not null)
                afterChanged?.Invoke(field);

            return field;
        }

        protected bool SetField<T>(ref T field, T value, Action<T>? beforeChanged, [CallerMemberName] string? propertyName = null)
        {
            if (ReferenceEquals(field, value))
                return false;

            if (!OnPropertyChanging(propertyName, field, value))
                return false;

            T prev = field;
            beforeChanged?.Invoke(prev);
            field = value;
            OnPropertyChanged(propertyName, prev, field);
            return true;
        }

        protected bool SetField<T>(ref T field, T value, Action<T>? beforeChanged, Action<T>? afterChanged, [CallerMemberName] string? propertyName = null)
        {
            if (ReferenceEquals(field, value))
                return false;

            if (!OnPropertyChanging(propertyName, field, value))
                return false;

            T prev = field;
            beforeChanged?.Invoke(prev);
            field = value;
            OnPropertyChanged(propertyName, prev, field);

            if (field is not null)
                afterChanged?.Invoke(field);

            return true;
        }

        protected virtual void OnPropertyChanged<T>(string? propName, T prev, T field)
            => PropertyChanged?.Invoke(this, new XRPropertyChangedEventArgs<T>(propName, prev, field));

        protected virtual bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            var args = new XRPropertyChangingEventArgs<T>(propName, field, @new);
            PropertyChanging?.Invoke(this, args);
            return args.AllowChange;
        }
    }
}
