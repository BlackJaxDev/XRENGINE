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
            foreach (var keyChara in _keyCharacterRegistrations)
                keyChara(character);
        }
        public bool GetKeyState(EKey key, EButtonInputType type)
            => FindOrCacheKey(key)?.GetState(type) ?? false;
        public bool Pressed(EKey key)
            => GetKeyState(key, EButtonInputType.Pressed);
        public bool Released(EKey key)
            => GetKeyState(key, EButtonInputType.Released);
    }
    public enum EKey
    {
        Unknown         = 000,
        ShiftLeft       = 001,
        LShift          = 001,
        ShiftRight      = 002,
        RShift          = 002,
        ControlLeft     = 003,
        LControl        = 003,
        ControlRight    = 004,
        RControl        = 004,
        AltLeft         = 005,
        LAlt            = 005,
        AltRight        = 006,
        RAlt            = 006,
        WinLeft         = 007,
        LWin            = 007,
        WinRight        = 008,
        RWin            = 008,
        Menu            = 009,
        F1              = 010,
        F2              = 011,
        F3              = 012,
        F4              = 013,
        F5              = 014,
        F6              = 015,
        F7              = 016,
        F8              = 017,
        F9              = 018,
        F10             = 019,
        F11             = 020,
        F12             = 021,
        F13             = 022,
        F14             = 023,
        F15             = 024,
        F16             = 025,
        F17             = 026,
        F18             = 027,
        F19             = 028,
        F20             = 029,
        F21             = 030,
        F22             = 031,
        F23             = 032,
        F24             = 033,
        F25             = 034,
        F26             = 035,
        F27             = 036,
        F28             = 037,
        F29             = 038,
        F30             = 039,
        F31             = 040,
        F32             = 041,
        F33             = 042,
        F34             = 043,
        F35             = 044,
        Up              = 045,
        Down            = 046,
        Left            = 047,
        Right           = 048,
        Enter           = 049,
        Escape          = 050,
        Space           = 051,
        Tab             = 052,
        Backspace       = 053,
        Back            = 053,
        Insert          = 054,
        Delete          = 055,
        PageUp          = 056,
        PageDown        = 057,
        Home            = 058,
        End             = 059,
        CapsLock        = 060,
        ScrollLock      = 061,
        PrintScreen     = 062,
        Pause           = 063,
        NumLock         = 064,
        Clear           = 065,
        Sleep           = 066,
        Keypad0         = 067,
        Keypad1         = 068,
        Keypad2         = 069,
        Keypad3         = 070,
        Keypad4         = 071,
        Keypad5         = 072,
        Keypad6         = 073,
        Keypad7         = 074,
        Keypad8         = 075,
        Keypad9         = 076,
        KeypadDivide    = 077,
        KeypadMultiply  = 078,
        KeypadSubtract  = 079,
        KeypadMinus     = 079,
        KeypadAdd       = 080,
        KeypadPlus      = 080,
        KeypadDecimal   = 081,
        KeypadPeriod    = 081,
        KeypadEnter     = 082,
        A               = 083,
        B               = 084,
        C               = 085,
        D               = 086,
        E               = 087,
        F               = 088,
        G               = 089,
        H               = 090,
        I               = 091,
        J               = 092,
        K               = 093,
        L               = 094,
        M               = 095,
        N               = 096,
        O               = 097,
        P               = 098,
        Q               = 099,
        R               = 100,
        S               = 101,
        T               = 102,
        U               = 103,
        V               = 104,
        W               = 105,
        X               = 106,
        Y               = 107,
        Z               = 108,
        Number0         = 109,
        Number1         = 110,
        Number2         = 111,
        Number3         = 112,
        Number4         = 113,
        Number5         = 114,
        Number6         = 115,
        Number7         = 116,
        Number8         = 117,
        Number9         = 118,
        Tilde           = 119,
        Grave           = 119,
        Minus           = 120,
        Equal           = 121,
        BracketLeft     = 122,
        LBracket        = 122,
        BracketRight    = 123,
        RBracket        = 123,
        Semicolon       = 124,
        Apostrophe      = 125,
        Comma           = 126,
        Period          = 127,
        Slash           = 128,
        BackSlash       = 129,
        NonUSBackSlash  = 130,
        LastKey         = 131,
    }
}
