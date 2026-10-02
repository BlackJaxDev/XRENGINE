using System.Diagnostics;

namespace XREngine.Input.Devices
{
    public delegate void DelSendButtonPressedState(int buttonIndex, EButtonInputType inputType, bool pressed);
    public delegate void DelSendButtonAction(int buttonIndex, EButtonInputType inputType);
    [Serializable]
    public class ButtonManager : InputManagerBase
    {
        private const float TimerMax = 0.5f;

        public event DelSendButtonPressedState? StatePressed;
        public event DelSendButtonAction? ActionExecuted;

        public ButtonManager(int index, string name)
        {
            _actions = new Dictionary<EButtonInputType, List<Action?>?>(4)
            {
                [EButtonInputType.Pressed] = null,
                [EButtonInputType.Released] = null,
                [EButtonInputType.Held] = null,
                [EButtonInputType.DoublePressed] = null
            };
            _usedTypes = [];

            Name = name;
            Index = index;
        }

        public int Index { get; }
        public string Name { get; }
        
        public bool IsPressed { get; protected set; }
        public bool IsHeld { get; protected set; }
        public bool IsDoublePressed { get; protected set; }

        protected List<DelButtonState?> _onStateChanged = [];
        protected Dictionary<EButtonInputType, List<Action?>?> _actions;
        protected HashSet<EButtonInputType> _usedTypes;
        private Lock _actionsLock = new();
        private ulong _registrationRevision;

        protected float _holdDelaySeconds = 0.2f;
        protected float _maxSecondsBetweenPresses = 0.2f;
        protected float _timer;

        #region Registration
        public virtual bool IsEmpty() => _usedTypes.Count == 0 && _onStateChanged.All(x => x is null);
        public void Register(Action func, EButtonInputType type, bool unregister)
        {
            using var scope = _actionsLock.EnterScope();
            ++_registrationRevision;

            List<Action?>? list = _actions[type];

            if (unregister)
            {
                if (list is null)
                    return;

                list.Remove(func);
                if (list.Count == 0)
                {
                    _actions[type] = null;
                    _usedTypes.Remove(type);
                }
            }
            else
            {
                if (list is null)
                {
                    _actions[type] = [func];
                    _usedTypes.Add(type);
                }
                else
                    _actions[type]?.Add(func);
            }
        }

        public bool GetState(EButtonInputType type)
            => type switch
            {
                EButtonInputType.Pressed => IsPressed,
                EButtonInputType.Released => !IsPressed,
                EButtonInputType.Held => IsHeld,
                EButtonInputType.DoublePressed => IsDoublePressed,
                _ => false,//Output.LogWarning($"Invalid {nameof(EButtonInputType)} {nameof(type)}.");
            };

        public void RegisterPressedState(DelButtonState func, bool unregister)
        {
            ++_registrationRevision;
            if (unregister)
                _onStateChanged.Remove(func);
            else
                _onStateChanged.Add(func);
        }
        public virtual void UnregisterAll()
        {
            using var scope = _actionsLock.EnterScope();
            ++_registrationRevision;

            foreach (var type in _usedTypes)
                _actions[type] = null;
            _usedTypes.Clear();
            _onStateChanged.Clear();
        }
        #endregion

        #region Actions
        internal void Tick(bool isPressed, float delta)
            => Tick(isPressed, delta, null, 0);

        internal void Tick(bool isPressed, float delta, InputDevice? device, ulong dispatchRevision)
        {
            ulong revision = _registrationRevision;
            if (IsPressed != isPressed)
            {
                if (isPressed)
                {
                    if (_timer <= _maxSecondsBetweenPresses)
                        OnDoublePressed(device, dispatchRevision);

                    if (revision != _registrationRevision || !IsDispatchCurrent(device, dispatchRevision))
                        return;

                    _timer = 0.0f;
                    OnPressed(device, dispatchRevision);
                }
                else
                    OnReleased(device, dispatchRevision);
            }
            else if (_timer < TimerMax)
            {
                _timer += delta;
                if (IsPressed && _timer >= _holdDelaySeconds)
                {
                    _timer = TimerMax;
                    OnHeld(device, dispatchRevision);
                }
            }
        }
        public void OnPressed()
            => OnPressed(null, 0);

        private void OnPressed(InputDevice? device, ulong dispatchRevision)
        {
            ulong revision = _registrationRevision;
            IsPressed = true;
            ExecuteActionList(EButtonInputType.Pressed, device, dispatchRevision);
            if (revision == _registrationRevision && IsDispatchCurrent(device, dispatchRevision))
                ExecutePressedStateList(true, device, dispatchRevision);
        }
        public void OnReleased()
            => OnReleased(null, 0);

        private void OnReleased(InputDevice? device, ulong dispatchRevision)
        {
            ulong revision = _registrationRevision;
            IsPressed = false;
            IsHeld = false;
            IsDoublePressed = false;
            ExecuteActionList(EButtonInputType.Released, device, dispatchRevision);
            if (revision == _registrationRevision && IsDispatchCurrent(device, dispatchRevision))
                ExecutePressedStateList(false, device, dispatchRevision);
        }
        public void OnHeld()
            => OnHeld(null, 0);

        private void OnHeld(InputDevice? device, ulong dispatchRevision)
        {
            IsHeld = true;
            ExecuteActionList(EButtonInputType.Held, device, dispatchRevision);
        }
        public void OnDoublePressed()
            => OnDoublePressed(null, 0);

        private void OnDoublePressed(InputDevice? device, ulong dispatchRevision)
        {
            IsDoublePressed = true;
            ExecuteActionList(EButtonInputType.DoublePressed, device, dispatchRevision);
        }
        private static bool IsDispatchCurrent(InputDevice? device, ulong revision)
            => device is null || device.InputDispatchRevision == revision;

        private void ExecuteActionList(EButtonInputType type, InputDevice? device, ulong dispatchRevision)
        {
            using var scope = _actionsLock.EnterScope();
            ulong revision = _registrationRevision;

            List<Action?>? list = _actions[type];
            if (list is null)
                return;

            //Inform the server of the input
            ActionExecuted?.Invoke(Index, type);

            //Run the input locally
            try
            {
                for (int i = 0; i < list.Count && revision == _registrationRevision && IsDispatchCurrent(device, dispatchRevision); i++)
                {
                    try
                    {
                        list[i]?.Invoke();
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"Error executing action for {Name} button: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Error executing action list for {Name} button: {e.Message}");
            }
        }
        private void ExecutePressedStateList(bool pressed, InputDevice? device, ulong dispatchRevision)
        {
            ulong revision = _registrationRevision;
            //Inform the server of the input
            StatePressed?.Invoke(Index, EButtonInputType.Pressed, pressed);

            //Run the input locally
            for (int i = 0; i < _onStateChanged.Count && revision == _registrationRevision && IsDispatchCurrent(device, dispatchRevision); i++)
            {
                try
                {
                    _onStateChanged[i]?.Invoke(pressed);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"Error executing action for {Name} button: {e.Message}");
                }
            }
        }
        #endregion

        /// <summary>Neutralizes an abandoned input owner without notifying replacement mappings.</summary>
        public void ResetStateWithoutEvents()
        {
            ++_registrationRevision;
            IsPressed = false;
            IsHeld = false;
            IsDoublePressed = false;
            _timer = TimerMax;
        }

        public override string ToString() => Name;
    }
}
