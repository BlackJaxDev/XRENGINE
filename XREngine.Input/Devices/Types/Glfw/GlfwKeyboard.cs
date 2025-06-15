using Silk.NET.Input;

namespace XREngine.Input.Devices.Glfw
{
    [Serializable]
    public class GlfwKeyboard : BaseKeyboard
    {
        private readonly IKeyboard _keyboard;

        public GlfwKeyboard(IKeyboard keyboard) : base(keyboard.Index)
        {
            _keyboard = keyboard;
            _keyboard.KeyDown += KeyDown;
            _keyboard.KeyUp += KeyUp;
            _keyboard.KeyChar += KeyChar;
        }
        ~GlfwKeyboard()
        {
            _keyboard.KeyDown -= KeyDown;
            _keyboard.KeyUp -= KeyUp;
            _keyboard.KeyChar -= KeyChar;
        }

        private void KeyChar(IKeyboard keyboard, char character)
        {
            KeyCharacter(character);
        }
        private void KeyUp(IKeyboard keyboard, Key key, int i)
        {
            EKey key2 = Conv(key);
            Keystroke(key2, false);
        }

        private void KeyDown(IKeyboard keyboard, Key key, int i)
        {
            Keystroke(Conv(key), true);
        }

        public override void TickStates(float delta)
        {
            foreach (ButtonManager? m in _buttonStates)
            {
                if (m is null)
                    continue;

                var key = Conv((EKey)m.Index);
                if (key == Key.Unknown)
                    continue;

                if (_keyboard.SupportedKeys.Contains(key))
                    m?.Tick(_keyboard.IsKeyPressed(key), delta);
            }
        }

        public static EKey Conv(Key key) => key switch
        {
            Key.A => EKey.A,
            Key.B => EKey.B,
            Key.C => EKey.C,
            Key.D => EKey.D,
            Key.E => EKey.E,
            Key.F => EKey.F,
            Key.G => EKey.G,
            Key.H => EKey.H,
            Key.I => EKey.I,
            Key.J => EKey.J,
            Key.K => EKey.K,
            Key.L => EKey.L,
            Key.M => EKey.M,
            Key.N => EKey.N,
            Key.O => EKey.O,
            Key.P => EKey.P,
            Key.Q => EKey.Q,
            Key.R => EKey.R,
            Key.S => EKey.S,
            Key.T => EKey.T,
            Key.U => EKey.U,
            Key.V => EKey.V,
            Key.W => EKey.W,
            Key.X => EKey.X,
            Key.Y => EKey.Y,
            Key.Z => EKey.Z,
            Key.Number0 => EKey.Number0,
            Key.Number1 => EKey.Number1,
            Key.Number2 => EKey.Number2,
            Key.Number3 => EKey.Number3,
            Key.Number4 => EKey.Number4,
            Key.Number5 => EKey.Number5,
            Key.Number6 => EKey.Number6,
            Key.Number7 => EKey.Number7,
            Key.Number8 => EKey.Number8,
            Key.Number9 => EKey.Number9,
            Key.Keypad0 => EKey.Keypad0,
            Key.Keypad1 => EKey.Keypad1,
            Key.Keypad2 => EKey.Keypad2,
            Key.Keypad3 => EKey.Keypad3,
            Key.Keypad4 => EKey.Keypad4,
            Key.Keypad5 => EKey.Keypad5,
            Key.Keypad6 => EKey.Keypad6,
            Key.Keypad7 => EKey.Keypad7,
            Key.Keypad8 => EKey.Keypad8,
            Key.Keypad9 => EKey.Keypad9,
            Key.F1 => EKey.F1,
            Key.F2 => EKey.F2,
            Key.F3 => EKey.F3,
            Key.F4 => EKey.F4,
            Key.F5 => EKey.F5,
            Key.F6 => EKey.F6,
            Key.F7 => EKey.F7,
            Key.F8 => EKey.F8,
            Key.F9 => EKey.F9,
            Key.F10 => EKey.F10,
            Key.F11 => EKey.F11,
            Key.F12 => EKey.F12,
            Key.F13 => EKey.F13,
            Key.F14 => EKey.F14,
            Key.F15 => EKey.F15,
            Key.F16 => EKey.F16,
            Key.F17 => EKey.F17,
            Key.F18 => EKey.F18,
            Key.F19 => EKey.F19,
            Key.F20 => EKey.F20,
            Key.F21 => EKey.F21,
            Key.F22 => EKey.F22,
            Key.F23 => EKey.F23,
            Key.F24 => EKey.F24,
            Key.F25 => EKey.F25,
            Key.Up => EKey.Up,
            Key.Down => EKey.Down,
            Key.Left => EKey.Left,
            Key.Right => EKey.Right,
            Key.ShiftLeft => EKey.ShiftLeft,
            Key.ShiftRight => EKey.ShiftRight,
            Key.ControlLeft => EKey.ControlLeft,
            Key.ControlRight => EKey.ControlRight,
            Key.AltLeft => EKey.AltLeft,
            Key.AltRight => EKey.AltRight,
            Key.SuperLeft => EKey.WinLeft,
            Key.SuperRight => EKey.WinRight,
            Key.Menu => EKey.Menu,
            Key.CapsLock => EKey.CapsLock,
            Key.Space => EKey.Space,
            Key.Enter => EKey.Enter,
            Key.Escape => EKey.Escape,
            Key.Tab => EKey.Tab,
            Key.KeypadSubtract => EKey.KeypadSubtract,
            Key.KeypadAdd => EKey.KeypadAdd,
            Key.KeypadDecimal => EKey.KeypadDecimal,
            Key.KeypadEnter => EKey.KeypadEnter,
            Key.GraveAccent => EKey.Grave,
            Key.Minus => EKey.Minus,
            Key.Equal => EKey.Equal,
            Key.LeftBracket => EKey.BracketLeft,
            Key.RightBracket => EKey.BracketRight,
            Key.Semicolon => EKey.Semicolon,
            Key.Apostrophe => EKey.Apostrophe,
            Key.Comma => EKey.Comma,
            Key.Period => EKey.Period,
            Key.Slash => EKey.Slash,
            Key.BackSlash => EKey.BackSlash,
            Key.Backspace => EKey.Backspace,
            Key.Insert => EKey.Insert,
            Key.Delete => EKey.Delete,
            Key.PageUp => EKey.PageUp,
            Key.PageDown => EKey.PageDown,
            Key.Home => EKey.Home,
            Key.End => EKey.End,
            Key.ScrollLock => EKey.ScrollLock,
            Key.NumLock => EKey.NumLock,
            Key.PrintScreen => EKey.PrintScreen,
            Key.Pause => EKey.Pause,
            Key.KeypadDivide => EKey.KeypadDivide,
            Key.KeypadMultiply => EKey.KeypadMultiply,
            Key.KeypadEqual => EKey.Unknown,
            Key.Unknown => EKey.Unknown,
            Key.World1 => EKey.Unknown,
            Key.World2 => EKey.Unknown,
            _ => EKey.Unknown,
        };
        public static Key Conv(EKey key) => key switch
        {
            EKey.A => Key.A,
            EKey.B => Key.B,
            EKey.C => Key.C,
            EKey.D => Key.D,
            EKey.E => Key.E,
            EKey.F => Key.F,
            EKey.G => Key.G,
            EKey.H => Key.H,
            EKey.I => Key.I,
            EKey.J => Key.J,
            EKey.K => Key.K,
            EKey.L => Key.L,
            EKey.M => Key.M,
            EKey.N => Key.N,
            EKey.O => Key.O,
            EKey.P => Key.P,
            EKey.Q => Key.Q,
            EKey.R => Key.R,
            EKey.S => Key.S,
            EKey.T => Key.T,
            EKey.U => Key.U,
            EKey.V => Key.V,
            EKey.W => Key.W,
            EKey.X => Key.X,
            EKey.Y => Key.Y,
            EKey.Z => Key.Z,
            EKey.Number0 => Key.Number0,
            EKey.Number1 => Key.Number1,
            EKey.Number2 => Key.Number2,
            EKey.Number3 => Key.Number3,
            EKey.Number4 => Key.Number4,
            EKey.Number5 => Key.Number5,
            EKey.Number6 => Key.Number6,
            EKey.Number7 => Key.Number7,
            EKey.Number8 => Key.Number8,
            EKey.Number9 => Key.Number9,
            EKey.Keypad0 => Key.Keypad0,
            EKey.Keypad1 => Key.Keypad1,
            EKey.Keypad2 => Key.Keypad2,
            EKey.Keypad3 => Key.Keypad3,
            EKey.Keypad4 => Key.Keypad4,
            EKey.Keypad5 => Key.Keypad5,
            EKey.Keypad6 => Key.Keypad6,
            EKey.Keypad7 => Key.Keypad7,
            EKey.Keypad8 => Key.Keypad8,
            EKey.Keypad9 => Key.Keypad9,
            EKey.F1 => Key.F1,
            EKey.F2 => Key.F2,
            EKey.F3 => Key.F3,
            EKey.F4 => Key.F4,
            EKey.F5 => Key.F5,
            EKey.F6 => Key.F6,
            EKey.F7 => Key.F7,
            EKey.F8 => Key.F8,
            EKey.F9 => Key.F9,
            EKey.F10 => Key.F10,
            EKey.F11 => Key.F11,
            EKey.F12 => Key.F12,
            EKey.F13 => Key.F13,
            EKey.F14 => Key.F14,
            EKey.F15 => Key.F15,
            EKey.F16 => Key.F16,
            EKey.F17 => Key.F17,
            EKey.F18 => Key.F18,
            EKey.F19 => Key.F19,
            EKey.F20 => Key.F20,
            EKey.F21 => Key.F21,
            EKey.F22 => Key.F22,
            EKey.F23 => Key.F23,
            EKey.F24 => Key.F24,
            EKey.F25 => Key.F25,
            EKey.Up => Key.Up,
            EKey.Down => Key.Down,
            EKey.Left => Key.Left,
            EKey.Right => Key.Right,
            EKey.ShiftLeft => Key.ShiftLeft,
            EKey.ShiftRight => Key.ShiftRight,
            EKey.ControlLeft => Key.ControlLeft,
            EKey.ControlRight => Key.ControlRight,
            EKey.AltLeft => Key.AltLeft,
            EKey.AltRight => Key.AltRight,
            EKey.WinLeft => Key.SuperLeft,
            EKey.WinRight => Key.SuperRight,
            EKey.Menu => Key.Menu,
            EKey.CapsLock => Key.CapsLock,
            EKey.Space => Key.Space,
            EKey.Enter => Key.Enter,
            EKey.Escape => Key.Escape,
            EKey.Tab => Key.Tab,
            EKey.KeypadSubtract => Key.KeypadSubtract,
            EKey.KeypadAdd => Key.KeypadAdd,
            EKey.KeypadDecimal => Key.KeypadDecimal,
            EKey.KeypadEnter => Key.KeypadEnter,
            EKey.Grave => Key.GraveAccent,
            EKey.Minus => Key.Minus,
            EKey.Equal => Key.Equal,
            EKey.BracketLeft => Key.LeftBracket,
            EKey.BracketRight => Key.RightBracket,
            EKey.Semicolon => Key.Semicolon,
            EKey.Apostrophe => Key.Apostrophe,
            EKey.Comma => Key.Comma,
            EKey.Period => Key.Period,
            EKey.Slash => Key.Slash,
            EKey.BackSlash => Key.BackSlash,
            EKey.Backspace => Key.Backspace,
            EKey.Insert => Key.Insert,
            EKey.Delete => Key.Delete,
            EKey.PageUp => Key.PageUp,
            EKey.PageDown => Key.PageDown,
            EKey.Home => Key.Home,
            EKey.End => Key.End,
            EKey.ScrollLock => Key.ScrollLock,
            EKey.NumLock => Key.NumLock,
            EKey.PrintScreen => Key.PrintScreen,
            EKey.Pause => Key.Pause,
            EKey.KeypadDivide => Key.KeypadDivide,
            EKey.KeypadMultiply => Key.KeypadMultiply,
            EKey.NonUSBackSlash => Key.Unknown,
            EKey.Unknown => Key.Unknown,
            _ => Key.Unknown,
        };
    }
}
