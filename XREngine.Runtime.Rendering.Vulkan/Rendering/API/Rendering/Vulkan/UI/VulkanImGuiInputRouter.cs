using ImGuiNET;
using System.Runtime.InteropServices;
using XREngine.Input.Devices;

namespace XREngine.Rendering.Vulkan;

/// <summary>Replays the independent desktop UI cursor in native event order.</summary>
internal sealed class VulkanImGuiInputRouter(XRWindow windowHost) : IDisposable
{
    private readonly List<WindowInputEvent> _events = new(64);
    private bool _leftCtrl, _rightCtrl, _leftShift, _rightShift;
    private bool _leftAlt, _rightAlt, _leftSuper, _rightSuper;

    public void Dispose() => _events.Clear();

    internal void FlushPendingInputEvents(ImGuiIOPtr io)
    {
        WindowInputSnapshot input = windowHost.ConsumeUiInputSnapshot(_events);
        bool viewports = (io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0;
        var origin = windowHost.DesktopWindowBackend?.ClientScreenPosition ?? default;
        ReadOnlySpan<WindowInputEvent> events = CollectionsMarshal.AsSpan(_events);
        for (int i = 0; i < events.Length; i++)
        {
            WindowInputEvent item = events[i];
            switch (item.Kind)
            {
                case WindowInputEventKind.Key:
                    if (TryConvertKey(item.Key, out ImGuiKey key))
                        io.AddKeyEvent(key, item.IsDown);
                    ReplayModifier(io, item.Key, item.IsDown);
                    break;
                case WindowInputEventKind.MouseButton:
                    io.AddMouseButtonEvent((int)item.MouseButton, item.IsDown);
                    break;
                case WindowInputEventKind.Text:
                    io.AddInputCharacter(item.Character);
                    break;
                case WindowInputEventKind.Pointer:
                    io.AddMousePosEvent(item.X + (viewports ? origin.X : 0), item.Y + (viewports ? origin.Y : 0));
                    break;
                case WindowInputEventKind.Scroll:
                    io.AddMouseWheelEvent(item.X, item.Y);
                    break;
            }
        }
        io.AddFocusEvent(input.IsFocused);
    }

    private void ReplayModifier(ImGuiIOPtr io, EKey key, bool down)
    {
        switch (key)
        {
            case EKey.ControlLeft: _leftCtrl = down; io.AddKeyEvent(ImGuiKey.ModCtrl, _leftCtrl || _rightCtrl); break;
            case EKey.ControlRight: _rightCtrl = down; io.AddKeyEvent(ImGuiKey.ModCtrl, _leftCtrl || _rightCtrl); break;
            case EKey.ShiftLeft: _leftShift = down; io.AddKeyEvent(ImGuiKey.ModShift, _leftShift || _rightShift); break;
            case EKey.ShiftRight: _rightShift = down; io.AddKeyEvent(ImGuiKey.ModShift, _leftShift || _rightShift); break;
            case EKey.AltLeft: _leftAlt = down; io.AddKeyEvent(ImGuiKey.ModAlt, _leftAlt || _rightAlt); break;
            case EKey.AltRight: _rightAlt = down; io.AddKeyEvent(ImGuiKey.ModAlt, _leftAlt || _rightAlt); break;
            case EKey.WinLeft: _leftSuper = down; io.AddKeyEvent(ImGuiKey.ModSuper, _leftSuper || _rightSuper); break;
            case EKey.WinRight: _rightSuper = down; io.AddKeyEvent(ImGuiKey.ModSuper, _leftSuper || _rightSuper); break;
        }
    }

    internal static bool TryConvertKey(EKey key, out ImGuiKey imguiKey)
    {
        imguiKey = key switch
        {
            EKey.Tab => ImGuiKey.Tab,
            EKey.Left => ImGuiKey.LeftArrow,
            EKey.Right => ImGuiKey.RightArrow,
            EKey.Up => ImGuiKey.UpArrow,
            EKey.Down => ImGuiKey.DownArrow,
            EKey.PageUp => ImGuiKey.PageUp,
            EKey.PageDown => ImGuiKey.PageDown,
            EKey.Home => ImGuiKey.Home,
            EKey.End => ImGuiKey.End,
            EKey.Insert => ImGuiKey.Insert,
            EKey.Delete => ImGuiKey.Delete,
            EKey.Backspace => ImGuiKey.Backspace,
            EKey.Space => ImGuiKey.Space,
            EKey.Enter => ImGuiKey.Enter,
            EKey.Escape => ImGuiKey.Escape,
            EKey.Apostrophe => ImGuiKey.Apostrophe,
            EKey.Comma => ImGuiKey.Comma,
            EKey.Minus => ImGuiKey.Minus,
            EKey.Period => ImGuiKey.Period,
            EKey.Slash => ImGuiKey.Slash,
            EKey.Semicolon => ImGuiKey.Semicolon,
            EKey.Equal => ImGuiKey.Equal,
            EKey.BracketLeft => ImGuiKey.LeftBracket,
            EKey.BackSlash => ImGuiKey.Backslash,
            EKey.BracketRight => ImGuiKey.RightBracket,
            EKey.Tilde => ImGuiKey.GraveAccent,
            EKey.CapsLock => ImGuiKey.CapsLock,
            EKey.ScrollLock => ImGuiKey.ScrollLock,
            EKey.NumLock => ImGuiKey.NumLock,
            EKey.PrintScreen => ImGuiKey.PrintScreen,
            EKey.Pause => ImGuiKey.Pause,
            EKey.Keypad0 => ImGuiKey.Keypad0,
            EKey.Keypad1 => ImGuiKey.Keypad1,
            EKey.Keypad2 => ImGuiKey.Keypad2,
            EKey.Keypad3 => ImGuiKey.Keypad3,
            EKey.Keypad4 => ImGuiKey.Keypad4,
            EKey.Keypad5 => ImGuiKey.Keypad5,
            EKey.Keypad6 => ImGuiKey.Keypad6,
            EKey.Keypad7 => ImGuiKey.Keypad7,
            EKey.Keypad8 => ImGuiKey.Keypad8,
            EKey.Keypad9 => ImGuiKey.Keypad9,
            EKey.KeypadDecimal => ImGuiKey.KeypadDecimal,
            EKey.KeypadDivide => ImGuiKey.KeypadDivide,
            EKey.KeypadMultiply => ImGuiKey.KeypadMultiply,
            EKey.KeypadMinus => ImGuiKey.KeypadSubtract,
            EKey.KeypadAdd => ImGuiKey.KeypadAdd,
            EKey.KeypadEnter => ImGuiKey.KeypadEnter,
            EKey.ShiftLeft => ImGuiKey.LeftShift,
            EKey.ControlLeft => ImGuiKey.LeftCtrl,
            EKey.AltLeft => ImGuiKey.LeftAlt,
            EKey.WinLeft => ImGuiKey.LeftSuper,
            EKey.ShiftRight => ImGuiKey.RightShift,
            EKey.ControlRight => ImGuiKey.RightCtrl,
            EKey.AltRight => ImGuiKey.RightAlt,
            EKey.WinRight => ImGuiKey.RightSuper,
            EKey.Menu => ImGuiKey.Menu,
            >= EKey.Number0 and <= EKey.Number9 => ImGuiKey._0 + (key - EKey.Number0),
            >= EKey.A and <= EKey.Z => ImGuiKey.A + (key - EKey.A),
            >= EKey.F1 and <= EKey.F24 => ImGuiKey.F1 + (key - EKey.F1),
            _ => ImGuiKey.None,
        };
        return imguiKey != ImGuiKey.None;
    }
}
