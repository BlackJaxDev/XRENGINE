using System.Reflection;
using XREngine.Input.Devices;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Handles DateTime input from the user.
    /// </summary>
    public class UIDateTimeInputComponent : UIInteractableComponent
    {
        public UIDateTimeInputComponent() : base()
        {

        }

        private PropertyInfo? _property;
        public PropertyInfo? Property
        {
            get => _property;
            set => SetField(ref _property, value);
        }

        public DateTime? GetValue()
        {
            var prop = Property;
            var targets = Targets;
            if (prop is null || targets is null)
                return null;

            //Make sure all targets have the same value.
            //If they don't, return null.
            DateTime? time = null;
            foreach (var target in targets)
            {
                var value = prop.GetValue(target);
                if (value is DateTime dt)
                {
                    if (time.HasValue && time.Value != dt)
                        return null; //Inconsistent values, return null
                    time = dt;
                }
                else
                {
                    return null; //Invalid value type, return null
                }
            }
            return time;
        }

        public void SetValue(DateTime value)
        {
            var prop = Property;
            var targets = Targets;
            if (prop is null || targets is null)
                return;

            foreach (var target in targets)
                prop.SetValue(target, value);
        }

        private object?[]? _targets;
        public object?[]? Targets
        {
            get => _targets;
            set => SetField(ref _targets, value);
        }

        protected override void OnGotFocus()
        {
            base.OnGotFocus();
        }
        protected override void OnLostFocus()
        {
            base.OnLostFocus();
        }
        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
        }
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
        }
        public override void RegisterInput(InputInterface input)
        {
            base.RegisterInput(input);
            
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
        }
    }
}
