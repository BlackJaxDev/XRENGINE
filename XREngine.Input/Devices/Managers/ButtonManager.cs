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
            _actions = new Dictionary<EButtonInputType, Action[]?>(4)
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

        private DelButtonState[] _onStateChanged = [];
        protected Dictionary<EButtonInputType, Action[]?> _actions;
        protected HashSet<EButtonInputType> _usedTypes;
        private Lock _actionsLock = new();

        protected float _holdDelaySeconds = 0.2f;
        protected float _maxSecondsBetweenPresses = 0.2f;
        protected float _timer;

        #region Registration
        public virtual bool IsEmpty()
        {
            using var scope = _actionsLock.EnterScope();
            return _usedTypes.Count == 0 && _onStateChanged.Length == 0;
        }
        public void Register(Action func, EButtonInputType type, bool unregister)
        {
            using var scope = _actionsLock.EnterScope();

            Action[] current = _actions[type] ?? [];

            if (unregister)
            {
                int index = Array.IndexOf(current, func);
                if (index < 0)
                    return;
                if (current.Length == 1)
                {
                    _actions[type] = null;
                    _usedTypes.Remove(type);
                }
                else
                {
                    Action[] next = new Action[current.Length - 1];
                    Array.Copy(current, 0, next, 0, index);
                    Array.Copy(current, index + 1, next, index, current.Length - index - 1);
                    _actions[type] = next;
                }
            }
            else
            {
                Action[] next = new Action[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = func;
                _actions[type] = next;
                _usedTypes.Add(type);
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
            using var scope = _actionsLock.EnterScope();
            DelButtonState[] current = _onStateChanged;
            if (unregister)
            {
                int index = Array.IndexOf(current, func);
                if (index < 0)
                    return;
                DelButtonState[] next = new DelButtonState[current.Length - 1];
                Array.Copy(current, 0, next, 0, index);
                Array.Copy(current, index + 1, next, index, current.Length - index - 1);
                _onStateChanged = next;
            }
            else
            {
                DelButtonState[] next = new DelButtonState[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = func;
                _onStateChanged = next;
            }
        }
        public virtual void UnregisterAll()
        {
            using var scope = _actionsLock.EnterScope();

            foreach (var type in _usedTypes)
                _actions[type] = null;
            _usedTypes.Clear();
            _onStateChanged = [];
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
            Action[]? list;
            using (var scope = _actionsLock.EnterScope())
                list = _actions[type];
            if (list is null) return;

            //Inform the server of the input
            ActionExecuted?.Invoke(Index, type);

            //Run the input locally
            try
            {
                for (int i = 0; i < list.Length; i++)
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
            DelButtonState[] callbacks;
            using (var scope = _actionsLock.EnterScope())
                callbacks = _onStateChanged;
            //Inform the server of the input
            StatePressed?.Invoke(Index, EButtonInputType.Pressed, pressed);

            //Run the input locally
            foreach (DelButtonState action in callbacks)
            {
                try
                {
                    action.Invoke(pressed);
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
