using System.Diagnostics;

namespace XREngine.Input.Devices
{
    public delegate void DelSendButtonPressedState(int buttonIndex, EButtonInputType inputType, bool pressed);
    public delegate void DelSendButtonAction(int buttonIndex, EButtonInputType inputType);
    public delegate void DelButtonState(bool pressed);
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

        protected float _holdDelaySeconds = 0.2f;
        protected float _maxSecondsBetweenPresses = 0.2f;
        protected float _timer;

        #region Registration
        public virtual bool IsEmpty() => _usedTypes.Count == 0 && _onStateChanged.All(x => x is null);
        public void Register(Action func, EButtonInputType type, bool unregister)
        {
            using var scope = _actionsLock.EnterScope();

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
            if (unregister)
                _onStateChanged.Remove(func);
            else
                _onStateChanged.Add(func);
        }
        public virtual void UnregisterAll()
        {
            using var scope = _actionsLock.EnterScope();

            foreach (var type in _usedTypes)
                _actions[type] = null;
            _usedTypes.Clear();
            for (int i = 0; i < 3; ++i)
                _onStateChanged[i] = null;
        }
        #endregion

        #region Actions
        internal void Tick(bool isPressed, float delta)
        {
            if (IsPressed != isPressed)
            {
                if (isPressed)
                {
                    if (_timer <= _maxSecondsBetweenPresses)
                        OnDoublePressed();

                    _timer = 0.0f;
                    OnPressed();
                }
                else
                    OnReleased();
            }
            else if (_timer < TimerMax)
            {
                _timer += delta;
                if (IsPressed && _timer >= _holdDelaySeconds)
                {
                    _timer = TimerMax;
                    OnHeld();
                }
            }
        }
        public void OnPressed()
        {
            IsPressed = true;
            ExecuteActionList(EButtonInputType.Pressed);
            ExecutePressedStateList(true);
        }
        public void OnReleased()
        {
            IsPressed = false;
            IsHeld = false;
            IsDoublePressed = false;
            ExecuteActionList(EButtonInputType.Released);
            ExecutePressedStateList(false);
        }
        public void OnHeld()
        {
            IsHeld = true;
            ExecuteActionList(EButtonInputType.Held);
        }
        public void OnDoublePressed()
        {
            IsDoublePressed = true;
            ExecuteActionList(EButtonInputType.DoublePressed);
        }
        private void ExecuteActionList(EButtonInputType type)
        {
            using var scope = _actionsLock.EnterScope();

            List<Action?>? list = _actions[type];
            if (list is null)
                return;

            //Inform the server of the input
            ActionExecuted?.Invoke(Index, type);

            //Run the input locally
            try
            {
                for (int i = 0; i < list.Count; i++)
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
        private void ExecutePressedStateList(bool pressed)
        {
            //Inform the server of the input
            StatePressed?.Invoke(Index, EButtonInputType.Pressed, pressed);

            //Run the input locally
            foreach (DelButtonState? action in _onStateChanged)
            {
                try
                {
                    action?.Invoke(pressed);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"Error executing action for {Name} button: {e.Message}");
                }
            }
        }
        #endregion

        public override string ToString() => Name;
    }
}
