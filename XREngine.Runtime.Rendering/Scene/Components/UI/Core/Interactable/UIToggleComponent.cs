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
        private IUIValueBinding<ECurrentState>? _subscribedBinding;
        private Action<ECurrentState>? _subscribedHandler;
        private IUIValueBinding<ECurrentState>? _requestedBinding;
        private long _bindingRevision;
        private bool _ownerActive;
        private bool _tickRegistered;
        private bool _terminal;
        /// <summary>Typed state source. A binding stays subscribed only while this control is active.</summary>
        public IUIValueBinding<ECurrentState>? Binding
        {
            get => _binding;
            set
            {
                if (_terminal || IsDestroyed || IsDestroyQueued)
                {
                    if (value is not null)
                        throw new ObjectDisposedException(nameof(UIToggleComponent));
                    return;
                }
                if (ReferenceEquals(_binding, value))
                    return;
                long revision = ++_bindingRevision;
                _requestedBinding = value;
                DetachBinding();
                if (!OwnsBindingRevision(revision))
                {
                    if (!_terminal && !IsDestroyed && !IsDestroyQueued)
                        SetField(ref _binding, _requestedBinding, publishNotifications: false);
                    return;
                }
                try { SetField(ref _binding, value); }
                finally
                {
                    if (!OwnsBindingRevision(revision))
                        SetField(ref _binding, _requestedBinding, publishNotifications: false);
                    else if (_ownerActive)
                        AttachBinding(_binding, revision);
                }
                if (!OwnsBindingRevision(revision))
                    return;
                _requestedBinding = _binding;
                SynchronizeBinding();
            }
        }

        private bool OwnsBindingRevision(long revision)
            => !_terminal && !IsDestroyed && !IsDestroyQueued && _bindingRevision == revision;

        private void AttachBinding(IUIValueBinding<ECurrentState>? binding, long revision)
        {
            if (binding is null || !_ownerActive || !OwnsBindingRevision(revision) ||
                ReferenceEquals(_subscribedBinding, binding))
                return;
            Action<ECurrentState> handler = value => OnBindingChanged(binding, revision, value);
            _subscribedBinding = binding;
            _subscribedHandler = handler;
            try { binding.ValueChanged += handler; }
            catch
            {
                if (ReferenceEquals(_subscribedHandler, handler))
                {
                    _subscribedBinding = null;
                    _subscribedHandler = null;
                }
                binding.ValueChanged -= handler;
                throw;
            }
            if (!_ownerActive || !OwnsBindingRevision(revision) ||
                !ReferenceEquals(_binding, binding) || !ReferenceEquals(_subscribedHandler, handler))
            {
                binding.ValueChanged -= handler;
                return;
            }
        }

        private void DetachBinding()
        {
            IUIValueBinding<ECurrentState>? subscribed = _subscribedBinding;
            Action<ECurrentState>? handler = _subscribedHandler;
            _subscribedBinding = null;
            _subscribedHandler = null;
            if (subscribed is not null && handler is not null)
                subscribed.ValueChanged -= handler;
        }

        private ECurrentState _retainedState;
        private bool _writingBinding;

        private void OnBindingChanged(IUIValueBinding<ECurrentState> source, long revision, ECurrentState value)
        {
            if (_ownerActive && OwnsBindingRevision(revision) &&
                ReferenceEquals(_subscribedBinding, source) && !_writingBinding)
                ChangeState(value, EUIValueChangeOrigin.Binding);
        }

        private void SynchronizeBinding()
        {
            IUIValueBinding<ECurrentState>? binding = _binding;
            long revision = _bindingRevision;
            if (binding is not null)
            {
                ECurrentState value = binding.Value;
                if (OwnsBindingRevision(revision) && ReferenceEquals(_binding, binding))
                    ChangeState(value, EUIValueChangeOrigin.Binding);
            }
        }

        private void ChangeState(ECurrentState value, EUIValueChangeOrigin origin)
        {
            ECurrentState old = _retainedState;
            if (_terminal || IsDestroyed || IsDestroyQueued || old == value)
                return;
            long revision = _bindingRevision;
            if (!SetField(ref _retainedState, value, nameof(CurrentState)))
                return;
            if (!OwnsBindingRevision(revision))
            {
                if (_ownerActive)
                    SynchronizeBinding();
                return;
            }
            LastState = value;
            if (!OwnsBindingRevision(revision))
                return;
            OnStateChanged?.Invoke(value);
            if (origin == EUIValueChangeOrigin.User && OwnsBindingRevision(revision) && _retainedState == value)
                ValueCommitted?.Invoke(new(old, value, origin));
        }

        protected override void OnComponentActivated()
        {
            if (_terminal)
                return;
            long entryRevision = _bindingRevision;
            base.OnComponentActivated();
            if (_terminal || IsDestroyed || IsDestroyQueued || entryRevision != _bindingRevision)
                return;
            _ownerActive = true;
            AttachBinding(_binding, _bindingRevision);
            if (!_ownerActive || !OwnsBindingRevision(entryRevision))
                return;
            SynchronizeBinding();
            if (!_ownerActive || !OwnsBindingRevision(entryRevision) || _tickRegistered)
                return;
            _tickRegistered = true;
            RegisterTick(Components.ETickGroup.Late, Components.ETickOrder.Scene, UpdateBox);
        }
        protected override void OnComponentDeactivated()
        {
            _ownerActive = false;
            ++_bindingRevision;
            try { ReleaseActiveOwners(); }
            finally { base.OnComponentDeactivated(); }
        }

        protected override void OnDestroying()
        {
            _terminal = true;
            _ownerActive = false;
            ++_bindingRevision;
            _requestedBinding = null;
            SetField(ref _binding, null, publishNotifications: false);
            try { ReleaseActiveOwners(); }
            finally { base.OnDestroying(); }
        }

        private void ReleaseActiveOwners()
        {
            Exception? failure = null;
            try { DetachBinding(); }
            catch (Exception error) { failure = error; }
            if (_tickRegistered)
            {
                _tickRegistered = false;
                try { UnregisterTick(Components.ETickGroup.Late, Components.ETickOrder.Scene, UpdateBox); }
                catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
            }
            if (failure is not null)
                throw failure;
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
            if (_terminal)
                return;
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
                if (_terminal || IsDestroyed || IsDestroyQueued)
                    return;
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
            if (_terminal || IsDestroyed || IsDestroyQueued)
                return;
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
            if (_terminal || !CanInteract || IsReadOnly)
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
            if (_terminal || IsDestroyed || IsDestroyQueued)
                return;
            IUIValueBinding<ECurrentState>? binding = _binding;
            if (binding is null || origin == EUIValueChangeOrigin.Binding)
            {
                ChangeState(value, origin);
                return;
            }
            bool wasActive = IsActiveInHierarchy;
            _writingBinding = true;
            try
            {
                binding.SetValue(value);
            }
            finally
            {
                _writingBinding = false;
            }
            if (_terminal || IsDestroyed || !ReferenceEquals(_binding, binding) || SceneNode is { IsDestroyed: true } ||
                wasActive && !IsActiveInHierarchy)
                return;
            ChangeState(binding.Value, origin);
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
