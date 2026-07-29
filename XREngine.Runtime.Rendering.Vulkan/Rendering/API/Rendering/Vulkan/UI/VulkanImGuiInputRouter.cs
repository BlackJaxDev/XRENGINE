using ImGuiNET;
using Silk.NET.Input;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanImGuiInputRouter : IDisposable
{
    private readonly VulkanRenderer _renderer;
    private readonly object _pendingInputLock = new();
    private readonly Queue<Action<ImGuiIOPtr>> _pendingInputEvents = new();
    private IMouse? _mouse;
    private readonly HashSet<IKeyboard> _keyboards = [];
    private bool _mouseSubscribed;
    private bool _disposed;

    public VulkanImGuiInputRouter(VulkanRenderer renderer)
        => _renderer = renderer;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DetachInputHandlers();
    }

    internal void TryAttachInputHandlers()
    {
        if (_disposed)
            return;

        var input = _renderer.ImGuiInputContext;
        if (input is null)
            return;

        if (!_mouseSubscribed)
        {
            IMouse? mouse = null;
            if (input.Mice is { Count: > 0 })
                mouse = input.Mice[0];

            if (mouse is not null)
            {
                _mouse = mouse;
                _mouse.MouseMove += OnMouseMove;
                _mouse.MouseDown += OnMouseDown;
                _mouse.MouseUp += OnMouseUp;
                _mouse.Scroll += OnMouseScroll;
                _mouseSubscribed = true;
            }
        }

        if (input.Keyboards is { Count: > 0 } keyboards)
        {
            for (int keyboardIndex = 0; keyboardIndex < keyboards.Count; keyboardIndex++)
            {
                IKeyboard keyboard = keyboards[keyboardIndex];
                if (!_keyboards.Add(keyboard))
                    continue;

                keyboard.KeyDown += OnKeyDown;
                keyboard.KeyUp += OnKeyUp;
                keyboard.KeyChar += OnKeyChar;
            }
        }
    }

    private void DetachInputHandlers()
    {
        if (_mouse is not null)
        {
            try
            {
                _mouse.MouseMove -= OnMouseMove;
                _mouse.MouseDown -= OnMouseDown;
                _mouse.MouseUp -= OnMouseUp;
                _mouse.Scroll -= OnMouseScroll;
            }
            catch
            {
            }

            _mouse = null;
        }

        _mouseSubscribed = false;

        foreach (IKeyboard keyboard in _keyboards)
        {
            try
            {
                keyboard.KeyDown -= OnKeyDown;
                keyboard.KeyUp -= OnKeyUp;
                keyboard.KeyChar -= OnKeyChar;
            }
            catch
            {
            }
        }

        _keyboards.Clear();

        lock (_pendingInputLock)
            _pendingInputEvents.Clear();
    }

    private void EnqueueInputEvent(Action<ImGuiIOPtr> inputEvent)
    {
        if (_disposed)
            return;

        lock (_pendingInputLock)
            _pendingInputEvents.Enqueue(inputEvent);
    }

    internal void FlushPendingInputEvents(ImGuiIOPtr io)
    {
        while (true)
        {
            Action<ImGuiIOPtr>? next;
            lock (_pendingInputLock)
            {
                if (_pendingInputEvents.Count == 0)
                    break;

                next = _pendingInputEvents.Dequeue();
            }

            next(io);
        }
    }

    internal void PushModifierKeyState(ImGuiIOPtr io)
    {
        bool ctrl = false;
        bool shift = false;
        bool alt = false;
        bool super = false;

        foreach (IKeyboard keyboard in _keyboards)
        {
            ctrl |= keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
            shift |= keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);
            alt |= keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight);
            super |= keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight);
        }

        io.AddKeyEvent(ImGuiKey.ModCtrl, ctrl);
        io.AddKeyEvent(ImGuiKey.ModShift, shift);
        io.AddKeyEvent(ImGuiKey.ModAlt, alt);
        io.AddKeyEvent(ImGuiKey.ModSuper, super);
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
        => EnqueueInputEvent(io => io.AddMousePosEvent(position.X, position.Y));

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (!TryConvertMouseButton(button, out int imguiButton))
            return;

        EnqueueInputEvent(io => io.AddMouseButtonEvent(imguiButton, true));
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (!TryConvertMouseButton(button, out int imguiButton))
            return;

        EnqueueInputEvent(io => io.AddMouseButtonEvent(imguiButton, false));
    }

    private void OnMouseScroll(IMouse mouse, ScrollWheel wheel)
    {
        float x = wheel.X;
        float y = wheel.Y;
        EnqueueInputEvent(io => io.AddMouseWheelEvent(x, y));
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int repeat)
    {
        if (!TryConvertKey(key, out ImGuiKey imguiKey))
            return;

        EnqueueInputEvent(io => io.AddKeyEvent(imguiKey, true));
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int repeat)
    {
        if (!TryConvertKey(key, out ImGuiKey imguiKey))
            return;

        EnqueueInputEvent(io => io.AddKeyEvent(imguiKey, false));
    }

    private void OnKeyChar(IKeyboard keyboard, char character)
    {
        if (character == '\0')
            return;

        EnqueueInputEvent(io => io.AddInputCharacter(character));
    }

    private static bool TryConvertMouseButton(MouseButton button, out int imguiButton)
    {
        imguiButton = button switch
        {
            MouseButton.Left => 0,
            MouseButton.Right => 1,
            MouseButton.Middle => 2,
            MouseButton.Button4 => 3,
            MouseButton.Button5 => 4,
            _ => -1
        };

        return imguiButton >= 0;
    }

    private static bool TryConvertKey(Key key, out ImGuiKey imguiKey)
    {
        imguiKey = key switch
        {
            Key.Tab => ImGuiKey.Tab,
            Key.Left => ImGuiKey.LeftArrow,
            Key.Right => ImGuiKey.RightArrow,
            Key.Up => ImGuiKey.UpArrow,
            Key.Down => ImGuiKey.DownArrow,
            Key.PageUp => ImGuiKey.PageUp,
            Key.PageDown => ImGuiKey.PageDown,
            Key.Home => ImGuiKey.Home,
            Key.End => ImGuiKey.End,
            Key.Insert => ImGuiKey.Insert,
            Key.Delete => ImGuiKey.Delete,
            Key.Backspace => ImGuiKey.Backspace,
            Key.Space => ImGuiKey.Space,
            Key.Enter => ImGuiKey.Enter,
            Key.Escape => ImGuiKey.Escape,
            Key.Apostrophe => ImGuiKey.Apostrophe,
            Key.Comma => ImGuiKey.Comma,
            Key.Minus => ImGuiKey.Minus,
            Key.Period => ImGuiKey.Period,
            Key.Slash => ImGuiKey.Slash,
            Key.Semicolon => ImGuiKey.Semicolon,
            Key.Equal => ImGuiKey.Equal,
            Key.LeftBracket => ImGuiKey.LeftBracket,
            Key.BackSlash => ImGuiKey.Backslash,
            Key.RightBracket => ImGuiKey.RightBracket,
            Key.GraveAccent => ImGuiKey.GraveAccent,
            Key.CapsLock => ImGuiKey.CapsLock,
            Key.ScrollLock => ImGuiKey.ScrollLock,
            Key.NumLock => ImGuiKey.NumLock,
            Key.PrintScreen => ImGuiKey.PrintScreen,
            Key.Pause => ImGuiKey.Pause,
            Key.Keypad0 => ImGuiKey.Keypad0,
            Key.Keypad1 => ImGuiKey.Keypad1,
            Key.Keypad2 => ImGuiKey.Keypad2,
            Key.Keypad3 => ImGuiKey.Keypad3,
            Key.Keypad4 => ImGuiKey.Keypad4,
            Key.Keypad5 => ImGuiKey.Keypad5,
            Key.Keypad6 => ImGuiKey.Keypad6,
            Key.Keypad7 => ImGuiKey.Keypad7,
            Key.Keypad8 => ImGuiKey.Keypad8,
            Key.Keypad9 => ImGuiKey.Keypad9,
            Key.KeypadDecimal => ImGuiKey.KeypadDecimal,
            Key.KeypadDivide => ImGuiKey.KeypadDivide,
            Key.KeypadMultiply => ImGuiKey.KeypadMultiply,
            Key.KeypadSubtract => ImGuiKey.KeypadSubtract,
            Key.KeypadAdd => ImGuiKey.KeypadAdd,
            Key.KeypadEnter => ImGuiKey.KeypadEnter,
            Key.KeypadEqual => ImGuiKey.KeypadEqual,
            Key.ShiftLeft => ImGuiKey.LeftShift,
            Key.ControlLeft => ImGuiKey.LeftCtrl,
            Key.AltLeft => ImGuiKey.LeftAlt,
            Key.SuperLeft => ImGuiKey.LeftSuper,
            Key.ShiftRight => ImGuiKey.RightShift,
            Key.ControlRight => ImGuiKey.RightCtrl,
            Key.AltRight => ImGuiKey.RightAlt,
            Key.SuperRight => ImGuiKey.RightSuper,
            Key.Menu => ImGuiKey.Menu,
            Key.Number0 => ImGuiKey._0,
            Key.Number1 => ImGuiKey._1,
            Key.Number2 => ImGuiKey._2,
            Key.Number3 => ImGuiKey._3,
            Key.Number4 => ImGuiKey._4,
            Key.Number5 => ImGuiKey._5,
            Key.Number6 => ImGuiKey._6,
            Key.Number7 => ImGuiKey._7,
            Key.Number8 => ImGuiKey._8,
            Key.Number9 => ImGuiKey._9,
            Key.A => ImGuiKey.A,
            Key.B => ImGuiKey.B,
            Key.C => ImGuiKey.C,
            Key.D => ImGuiKey.D,
            Key.E => ImGuiKey.E,
            Key.F => ImGuiKey.F,
            Key.G => ImGuiKey.G,
            Key.H => ImGuiKey.H,
            Key.I => ImGuiKey.I,
            Key.J => ImGuiKey.J,
            Key.K => ImGuiKey.K,
            Key.L => ImGuiKey.L,
            Key.M => ImGuiKey.M,
            Key.N => ImGuiKey.N,
            Key.O => ImGuiKey.O,
            Key.P => ImGuiKey.P,
            Key.Q => ImGuiKey.Q,
            Key.R => ImGuiKey.R,
            Key.S => ImGuiKey.S,
            Key.T => ImGuiKey.T,
            Key.U => ImGuiKey.U,
            Key.V => ImGuiKey.V,
            Key.W => ImGuiKey.W,
            Key.X => ImGuiKey.X,
            Key.Y => ImGuiKey.Y,
            Key.Z => ImGuiKey.Z,
            Key.F1 => ImGuiKey.F1,
            Key.F2 => ImGuiKey.F2,
            Key.F3 => ImGuiKey.F3,
            Key.F4 => ImGuiKey.F4,
            Key.F5 => ImGuiKey.F5,
            Key.F6 => ImGuiKey.F6,
            Key.F7 => ImGuiKey.F7,
            Key.F8 => ImGuiKey.F8,
            Key.F9 => ImGuiKey.F9,
            Key.F10 => ImGuiKey.F10,
            Key.F11 => ImGuiKey.F11,
            Key.F12 => ImGuiKey.F12,
            _ => ImGuiKey.None,
        };

        return imguiKey != ImGuiKey.None;
    }
}
