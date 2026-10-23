using XREngine.Data.Core;
using XREngine.Input;
using XREngine.Input.Devices;

namespace XREngine.Rendering.UI
{
    public class UIToggleComponent : UIInspectorEditorComponent
    {
        public UIToggleComponent()
        {
            // Use the canvas's accepted interaction so focus does not reset a
            // pending mouse press by registering another release callback.
            InteractAction += OnToggleChecked;
        }

        public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.CheckBox;
        public override string? AccessibilityValue => CurrentState.ToString();
        public override bool AccessibilityActivate() => Toggle();
        public XREvent<ECurrentState>? OnStateChanged;
        public event Action<UIValueChange<ECurrentState>>? ValueCommitted;

        private IUIValueBinding<ECurrentState>? _binding;
        /// <summary>Typed state source. A binding stays subscribed only while this control is active.</summary>
        public IUIValueBinding<ECurrentState>? Binding
        {
            get => _binding;
            set
            {
                if (ReferenceEquals(_binding, value))
                    return;
                if (IsActiveInHierarchy && _binding is not null)
                    _binding.ValueChanged -= OnBindingChanged;
                SetField(ref _binding, value);
                if (IsActiveInHierarchy && _binding is not null)
                    _binding.ValueChanged += OnBindingChanged;
                SynchronizeBinding();
            }
        }

        private ECurrentState _retainedState;
        private bool _writingBinding;

        private void OnBindingChanged(ECurrentState value)
        {
            if (!_writingBinding)
                ChangeState(value, EUIValueChangeOrigin.Binding);
        }

        private void SynchronizeBinding()
        {
            if (_binding is not null)
                ChangeState(_binding.Value, EUIValueChangeOrigin.Binding);
        }

        private void ChangeState(ECurrentState value, EUIValueChangeOrigin origin)
        {
            ECurrentState old = _retainedState;
            if (old == value)
                return;
            SetField(ref _retainedState, value, nameof(CurrentState));
            LastState = value;
            OnStateChanged?.Invoke(value);
            if (origin == EUIValueChangeOrigin.User)
                ValueCommitted?.Invoke(new(old, value, origin));
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (_binding is not null)
            {
                _binding.ValueChanged += OnBindingChanged;
                SynchronizeBinding();
            }
            RegisterTick(Components.ETickGroup.Late, Components.ETickOrder.Scene, UpdateBox);
        }
        protected override void OnComponentDeactivated()
        {
            if (_binding is not null)
                _binding.ValueChanged -= OnBindingChanged;
            UnregisterTick(Components.ETickGroup.Late, Components.ETickOrder.Scene, UpdateBox);
            base.OnComponentDeactivated();
        }

        private DateTime _lastUpdate = DateTime.MinValue;

        private TimeSpan _updateInterval = TimeSpan.FromMilliseconds(500);
        public TimeSpan UpdateInterval
        {
            get => _updateInterval;
            set => SetField(ref _updateInterval, value);
        }

        private ECurrentState _lastState = ECurrentState.False;
        public ECurrentState LastState
        {
            get => _lastState;
            private set => SetField(ref _lastState, value);
        }

        private void UpdateBox()
        {
            if (_binding is not null)
                return;
            if (DateTime.Now - _lastUpdate < UpdateInterval)
                return;

            _lastUpdate = DateTime.Now;

            if (Property == null || Targets == null || Targets.Length == 0)
                return;

            // Update the toggle state based on the current property value
            ECurrentState currentState = CurrentState;
            if (currentState != LastState)
                ChangeState(currentState, EUIValueChangeOrigin.Binding);
        }

        public ECurrentState CurrentState
        {
            get
            {
                if (_binding is not null || Property is null)
                    return _retainedState;
                if (Property == null || Targets == null || Targets.Length == 0)
                    return ECurrentState.False;

                bool? firstValue = null;
                foreach (var target in Targets)
                {
                    if (target == null || !Property.CanRead)
                        continue;
                    var value = Property.GetValue(target);
                    if (value is bool booleanValue)
                    {
                        if (firstValue is null)
                            firstValue = booleanValue;
                        else if (firstValue != booleanValue)
                            return ECurrentState.Intermediate; // Mixed values
                    }
                    else
                    {
                        return ECurrentState.Intermediate; // Property is not a boolean
                    }
                }
                return firstValue == true ? ECurrentState.True : ECurrentState.False;
            }
            set
            {
                if (_binding is not null || Property is null)
                {
                    SetRetainedState(value, EUIValueChangeOrigin.Programmatic);
                    return;
                }
                if (Property == null || Targets == null || Targets.Length == 0)
                    return;

                // Set the value of the property on each target based on the current state
                bool newValue = value switch
                {
                    ECurrentState.True => true,
                    ECurrentState.False => false,
                    _ => GetMajorityValue() // For Intermediate, use majority value logic
                };
                SetValue(newValue);
            }
        }

        public enum ECurrentState
        {
            /// <summary>
            /// All targets have false as their property value.
            /// </summary>
            False,
            /// <summary>
            /// All targets have true as their property value.
            /// </summary>
            True,
            /// <summary>
            /// This state indicates that the targets have mixed values for the property.
            /// </summary>
            Intermediate
        }

        public bool GetMajorityValue()
        {
            if (Property == null || Targets == null || Targets.Length == 0)
                return false;

            // Count the number of true and false values
            int trueCount = 0;
            int falseCount = 0;
            foreach (var target in Targets)
            {
                if (target == null || !Property.CanRead)
                    continue;

                var value = Property.GetValue(target);
                if (value is bool booleanValue)
                {
                    if (booleanValue)
                        trueCount++;
                    else
                        falseCount++;
                }
            }

            // Return true if the majority of values are true, otherwise false
            return trueCount > falseCount;
        }

        public bool? GetValue()
        {
            if (Property == null || Targets == null || Targets.Length == 0)
                return null;

            //Verify all targets have the same property value
            //If not, return null
            bool? firstValue = null;
            foreach (var target in Targets)
            {
                if (target == null || !Property.CanRead)
                    return null;

                var value = Property.GetValue(target);
                if (value is bool booleanValue)
                {
                    if (firstValue is null)
                        firstValue = booleanValue;
                    else if (firstValue != booleanValue)
                        return null; // Values are inconsistent
                }
                else
                {
                    return null; // Property is not a boolean
                }
            }
            return firstValue;
        }

        public void SetValue(bool value)
        {
            if (_binding is not null || Property is null)
            {
                SetRetainedState(value ? ECurrentState.True : ECurrentState.False,
                    EUIValueChangeOrigin.Programmatic);
                return;
            }
            if (Property == null || Targets == null || Targets.Length == 0)
                return;

            // Set the value of the property on each target
            foreach (var target in Targets)
                if (target != null && Property.CanWrite)
                    Property.SetValue(target, value);
        }

        public bool Toggle()
        {
            if (!CanInteract || IsReadOnly)
                return false;
            if (_binding is not null || Property is null)
            {
                SetRetainedState(CurrentState == ECurrentState.True ? ECurrentState.False : ECurrentState.True,
                    EUIValueChangeOrigin.User);
                return true;
            }
            if (Property == null || Targets == null || Targets.Length == 0)
                return false;

            bool majorityValue = GetMajorityValue();
            bool newValue = !majorityValue; // Toggle the value
            SetValue(newValue);
            ChangeState(newValue ? ECurrentState.True : ECurrentState.False, EUIValueChangeOrigin.User);
            return true;
        }

        /// <summary>Sets retained state and writes it to the typed source when present.</summary>
        public void SetRetainedState(ECurrentState value, EUIValueChangeOrigin origin)
        {
            if (_binding is null || origin == EUIValueChangeOrigin.Binding)
            {
                ChangeState(value, origin);
                return;
            }
            _writingBinding = true;
            try
            {
                _binding.SetValue(value);
            }
            finally
            {
                _writingBinding = false;
            }
            ChangeState(_binding.Value, origin);
        }

        private void OnToggleChecked(UIInteractableComponent component)
        {
            // Gamepad focus can remain after the control or canvas becomes hidden or inactive.
            Toggle();
        }

        public override void CancelForRecycle()
        {
            Binding = null;
            base.CancelForRecycle();
        }

        public override void RegisterInput(IInputRegistration input)
        {
            base.RegisterInput(input);
            input.RegisterKeyStateChange(EKey.Space, ActivateKey);
        }

        private void ActivateKey(bool pressed)
        {
            if (pressed)
                OnInteract();
        }
    }
}
