using XREngine.Data.Core;

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
        public override bool AccessibilityActivate() => Toggle();
        public XREvent<ECurrentState>? OnStateChanged;

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RegisterTick(Components.ETickGroup.Late, Components.ETickOrder.Scene, UpdateBox);
        }
        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UnregisterTick(Components.ETickGroup.Late, Components.ETickOrder.Scene, UpdateBox);
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
            if (DateTime.Now - _lastUpdate < UpdateInterval)
                return;

            _lastUpdate = DateTime.Now;

            if (Property == null || Targets == null || Targets.Length == 0)
                return;

            // Update the toggle state based on the current property value
            ECurrentState currentState = CurrentState;
            if (currentState != LastState)
            {
                LastState = currentState;
                OnStateChanged?.Invoke(currentState);
            }
        }

        public ECurrentState CurrentState
        {
            get
            {
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
            if (Property == null || Targets == null || Targets.Length == 0)
                return;

            // Set the value of the property on each target
            foreach (var target in Targets)
                if (target != null && Property.CanWrite)
                    Property.SetValue(target, value);
        }

        public bool Toggle()
        {
            if (Property == null || Targets == null || Targets.Length == 0)
                return false;

            bool majorityValue = GetMajorityValue();
            bool newValue = !majorityValue; // Toggle the value
            SetValue(newValue);
            return true;
        }

        private void OnToggleChecked(UIInteractableComponent component)
        {
            // Gamepad focus can remain after the control or canvas becomes hidden or inactive.
            if (!IsActiveInHierarchy || !UITransform.IsVisibleInHierarchy ||
                UserInterfaceCanvas is not { IsActiveInHierarchy: true } canvas ||
                !canvas.CanvasTransform.IsVisibleInHierarchy)
                return;

            Toggle();
        }
    }
}
