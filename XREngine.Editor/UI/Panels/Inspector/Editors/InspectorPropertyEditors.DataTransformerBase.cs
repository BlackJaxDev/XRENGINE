using System.Reflection;
using XREngine.Components;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public abstract class DataTransformerBase : XRComponent
    {
        private PropertyInfo? _property;
        /// <summary>
        /// This is the property that this component is editing.
        /// </summary>
        public PropertyInfo? Property
        {
            get => _property;
            set => SetField(ref _property, value);
        }

        private object?[]? _targets;
        /// <summary>
        /// These are the targets that this component is editing. The property will be set on each of these targets.
        /// </summary>
        public object?[]? Targets
        {
            get => _targets;
            set => SetField(ref _targets, value);
        }

        private bool _isFocused = false;
        public bool IsFocused
        {
            get => _isFocused;
            set => SetField(ref _isFocused, value);
        }

        private TimeSpan _updateinterval = TimeSpan.FromMilliseconds(500);
        public TimeSpan UpdateInterval
        {
            get => _updateinterval;
            set => SetField(ref _updateinterval, value);
        }

        protected DateTime _lastUpdate = DateTime.MinValue;

        public PropertyInfo GetTransformedProperty(string name)
            => GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new InvalidOperationException($"Property '{name}' not found.");
    }
    public abstract class DataTransformerBase<T> : DataTransformerBase
    {
        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateTick);
        }
        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UnregisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateTick);
        }

        private void UpdateTick()
        {
            if (IsFocused || DateTime.UtcNow - _lastUpdate < UpdateInterval)
                return; // If the property editor is focused, we don't update the values so the user can edit them without interruption.

            _lastUpdate = DateTime.UtcNow;

            //We need to get the quaternion from the targets and update the yaw, pitch, and roll properties accordingly.
            //If any quaternion does not match the others, all three properties will be set to null.
            if (Targets is null || Targets.Length == 0 || Property is null)
            {
                DisplayValue(default);
                return;
            }
            T? firstValue = default;
            foreach (var target in Targets)
            {
                if (target is null)
                    continue;

                if (Property.GetValue(target) is not T value)
                    continue;

                if (firstValue is null)
                    firstValue = value;
                else if (!Equal(firstValue, value))
                {
                    DisplayValue(default);
                    return;
                }
            }
            DisplayValue(firstValue);
        }

        protected abstract bool Equal(T lhs, T rhs);
        protected abstract void DisplayValue(T? value);
    }
}
