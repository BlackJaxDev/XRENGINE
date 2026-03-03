using MemoryPack;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

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
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> CloneablePropertiesByType = new();
        private static readonly ConcurrentDictionary<Type, MethodInfo> CloneWithMemoryPackByType = new();

        private static readonly AsyncLocal<int> PropertyNotificationSuppressionDepth = new();

        public static bool ArePropertyNotificationsSuppressed => PropertyNotificationSuppressionDepth.Value > 0;

        public static IDisposable SuppressPropertyNotifications()
            => new PropertyNotificationSuppressionScope();

        private readonly struct PropertyNotificationSuppressionScope : IDisposable
        {
            public PropertyNotificationSuppressionScope()
                => PropertyNotificationSuppressionDepth.Value++;

            public void Dispose()
                => PropertyNotificationSuppressionDepth.Value--;
        }

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

            if (ArePropertyNotificationsSuppressed)
            {
                field = value;
                return true;
            }

            if (!OnPropertyChanging(propertyName, field, value))
                return false;

            T prev = field;
            field = value;
            OnPropertyChanged(propertyName, prev, field);
            return true;
        }

        protected bool SetFieldUnchecked<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (ArePropertyNotificationsSuppressed)
            {
                field = value;
                return true;
            }

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

            if (ArePropertyNotificationsSuppressed)
            {
                field = value;
                return field;
            }

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

            if (ArePropertyNotificationsSuppressed)
            {
                field = value;
                return field;
            }

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

            if (ArePropertyNotificationsSuppressed)
            {
                field = value;
                return true;
            }

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

            if (ArePropertyNotificationsSuppressed)
            {
                field = value;
                return true;
            }

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
            var pc = PropertyChanging;
            if (pc is null)
                return true; // No subscribers, allow change by default.

            var args = new XRPropertyChangingEventArgs<T>(propName, field, @new);
            pc(this, args);
            return args.AllowChange;
        }

        protected static void CopyDeclaredCloneableProperties<T>(T source, T target)
            where T : XRBase
        {
            Type type = typeof(T);
            PropertyInfo[] properties = CloneablePropertiesByType.GetOrAdd(type, static runtimeType =>
                [.. runtimeType
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Where(x => x.CanRead && x.CanWrite && x.GetIndexParameters().Length == 0)]);

            foreach (PropertyInfo property in properties)
            {
                object? value = property.GetValue(source);
                object? cloneValue = ClonePropertyValue(value, property.PropertyType);
                property.SetValue(target, cloneValue);
            }
        }

        private static object? ClonePropertyValue(object? value, Type declaredType)
        {
            if (value is null)
                return null;

            if (declaredType.IsValueType || declaredType == typeof(string))
                return value;

            // Prefer ICloneable — avoids MemoryPack round-trip for types that
            // know how to copy themselves (e.g. OverrideableSetting<T>).
            if (value is ICloneable cloneable)
                return cloneable.Clone();

            Type cloneType = declaredType;
            if (cloneType == typeof(object) || cloneType.IsInterface || cloneType.IsAbstract)
                cloneType = value.GetType();

            if (cloneType.IsValueType || cloneType == typeof(string) || cloneType.IsInterface || cloneType.IsAbstract)
                return value;

            MethodInfo cloneMethod = CloneWithMemoryPackByType.GetOrAdd(cloneType, static type =>
                typeof(XRBase)
                    .GetMethod(nameof(CloneWithMemoryPack), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(type));
            return cloneMethod.Invoke(null, [value]);
        }

        private static T? CloneWithMemoryPack<T>(T value)
        {
            byte[] bytes = MemoryPackSerializer.Serialize(value);
            return MemoryPackSerializer.Deserialize<T>(bytes);
        }
    }
}
