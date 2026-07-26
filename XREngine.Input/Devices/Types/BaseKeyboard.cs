namespace XREngine.Input.Devices
{
    [Serializable]
    public abstract class BaseKeyboard(int index) : InputDevice(index)
    {
        protected List<EKey> _registeredKeys = new(132);

        protected override int GetAxisCount() => 0;
        protected override int GetButtonCount() => 132;
        public override EInputDeviceType DeviceType => EInputDeviceType.Keyboard;

        private ButtonManager? FindOrCacheKey(EKey key)
        {
            int index = (int)key;
            if (_buttonStates[index] is null)
            {
                _buttonStates[index] = MakeKeyManager(key, index);
                _registeredKeys.Add(key);
            }
            return _buttonStates[index];
        }

        public void RegisterKeyPressed(EKey key, DelButtonState func, bool unregister)
        {
            if (unregister)
            {
                int keyIndex = (int)key;
                var state = _buttonStates[keyIndex];
                if (state != null)
                {
                    state.RegisterPressedState(func, true);
                    if (state.IsEmpty())
                    {
                        _buttonStates[keyIndex] = null;
                        _registeredKeys.Remove(key);
                    }
                }
            }
            else
                FindOrCacheKey(key)?.RegisterPressedState(func, false);
        }

        public void RegisterKeyEvent(EKey key, EButtonInputType type, Action func, bool unregister)
            => RegisterButtonEvent(unregister ? _buttonStates[(int)key] : FindOrCacheKey(key), type, func, unregister);

        private readonly List<DelKeystroke> _keystrokeRegistrations = [];
        private readonly List<Action<char>> _keyCharacterRegistrations = [];

        public delegate void DelKeystroke(EKey key, bool pressed);

        public void RegisterKeystroke(DelKeystroke func, bool unregister)
        {
            if (unregister)
                _keystrokeRegistrations.Remove(func);
            else
                _keystrokeRegistrations.Add(func);
        }

        public void RegisterKeyCharacter(Action<char> action, bool unregister)
        {
            if (unregister)
                _keyCharacterRegistrations.Remove(action);
            else
                _keyCharacterRegistrations.Add(action);
        }

        protected void Keystroke(EKey key, bool pressed)
        {
            foreach (var keystroke in _keystrokeRegistrations)
                keystroke(key, pressed);
        }

        protected void KeyCharacter(char character)
        {
            foreach (var keyCharacter in _keyCharacterRegistrations)
                keyCharacter(character);
        }

        public bool GetKeyState(EKey key, EButtonInputType type)
            => FindOrCacheKey(key)?.GetState(type) ?? false;
        public bool Pressed(EKey key)
            => GetKeyState(key, EButtonInputType.Pressed);
        public bool Released(EKey key)
            => GetKeyState(key, EButtonInputType.Released);
        protected void TickKeyState(EKey key, bool isPressed, float delta)
            => _buttonStates[(int)key]?.Tick(isPressed, delta);
    }
}
