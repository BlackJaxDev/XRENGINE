using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.OpenGL
{
    public partial class OpenGLRenderer
    {
        private sealed unsafe partial class OpenGLImGuiMultiViewportController
        {
            private void EnsureMainViewportPlatformData()
            {
                ImGuiViewportPtr mainViewport = ImGui.GetMainViewport();
                mainViewport.PlatformHandle = _mainWindow.Handle;
                mainViewport.PlatformHandleRaw = _mainWindow.Handle;
                Vector2D<int> clientPosition = GetClientScreenPosition(_mainWindow);
                mainViewport.Pos = new Vector2(clientPosition.X, clientPosition.Y);
            }

            private bool TryGetMousePosition(out Vector2 position, out uint viewportId)
            {
                position = default;
                viewportId = 0;

                if (OperatingSystem.IsWindows() && GetCursorPos(out NativePoint cursorPosition))
                {
                    position = new Vector2(cursorPosition.X, cursorPosition.Y);
                    viewportId = ResolveHoveredViewportId(cursorPosition);
                    return true;
                }

                IInputContext? input = _renderer.XRWindow.Input;
                if (input is null || input.Mice.Count == 0)
                    return false;

                Vector2 localPosition = input.Mice[0].Position;
                Vector2D<int> clientPosition = GetClientScreenPosition(_mainWindow);
                position = new Vector2(clientPosition.X + localPosition.X, clientPosition.Y + localPosition.Y);
                viewportId = ImGui.GetMainViewport().ID;
                return true;
            }

            private uint ResolveHoveredViewportId(NativePoint screenPosition)
            {
                foreach (PlatformWindow window in _platformWindows.Values)
                {
                    if (window.IsDisposed)
                        continue;

                    if (TryGetWindowScreenRect(window.Window, out NativeRect rect) && rect.Contains(screenPosition))
                        return window.ViewportId;
                }

                if (TryGetWindowScreenRect(_mainWindow, out NativeRect mainRect) && mainRect.Contains(screenPosition))
                    return ImGui.GetMainViewport().ID;

                return 0;
            }

            private void QueueMainMouseButtonEvents(ImGuiIOPtr io)
            {
                if (!TryReadMouseButtonState(out bool leftDown, out bool rightDown, out bool middleDown))
                    return;

                QueueMouseButtonTransition(io, 0, leftDown, ref _lastLeftMouseDown);
                QueueMouseButtonTransition(io, 1, rightDown, ref _lastRightMouseDown);
                QueueMouseButtonTransition(io, 2, middleDown, ref _lastMiddleMouseDown);
            }

            private void QueueMainMouseWheelEvents(ImGuiIOPtr io, uint viewportId)
            {
                lock (_mainMouseWheelLock)
                {
                    _mainMouseWheelDispatchBuffer.Clear();
                    while (_pendingMainMouseWheelDeltas.Count > 0)
                        _mainMouseWheelDispatchBuffer.Add(_pendingMainMouseWheelDeltas.Dequeue());
                }

                for (int i = 0; i < _mainMouseWheelDispatchBuffer.Count; i++)
                {
                    Vector2 delta = _mainMouseWheelDispatchBuffer[i];
                    io.AddMouseWheelEvent(delta.X, delta.Y);
                    io.AddMouseViewportEvent(viewportId);
                }
            }

            private static void QueueMouseButtonTransition(ImGuiIOPtr io, int button, bool down, ref bool previousDown)
            {
                if (down == previousDown)
                    return;

                previousDown = down;
                io.AddMouseButtonEvent(button, down);
            }

            private bool TryReadMouseButtonState(out bool leftDown, out bool rightDown, out bool middleDown)
            {
                leftDown = false;
                rightDown = false;
                middleDown = false;

                if (OperatingSystem.IsWindows())
                {
                    leftDown = IsNativeMouseButtonDown(NativeVirtualKeyLeftButton);
                    rightDown = IsNativeMouseButtonDown(NativeVirtualKeyRightButton);
                    middleDown = IsNativeMouseButtonDown(NativeVirtualKeyMiddleButton);
                    return true;
                }

                IInputContext? input = _renderer.XRWindow.Input;
                if (input is null || input.Mice.Count == 0)
                    return false;

                IMouse mouse = input.Mice[0];
                leftDown = mouse.IsButtonPressed(MouseButton.Left);
                rightDown = mouse.IsButtonPressed(MouseButton.Right);
                middleDown = mouse.IsButtonPressed(MouseButton.Middle);
                return true;
            }

            private static bool IsNativeMouseButtonDown(int virtualKey)
                => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

            private void AttachMainInput()
            {
                if (_mainMouse is not null)
                    return;

                IInputContext? input = _renderer.XRWindow.Input;
                if (input is null || input.Mice.Count == 0)
                    return;

                _mainMouse = input.Mice[0];
                _mainMouse.Scroll += OnMainMouseScroll;
            }

            private void DetachMainInput()
            {
                if (_mainMouse is not null)
                {
                    _mainMouse.Scroll -= OnMainMouseScroll;
                    _mainMouse = null;
                }

                ClearQueuedMainMouseWheelEvents();
                _mainMouseWheelDispatchBuffer.Clear();
            }

            private void OnMainMouseScroll(IMouse mouse, ScrollWheel wheel)
            {
                if (_disposed)
                    return;

                if (MathF.Abs(wheel.X) <= float.Epsilon && MathF.Abs(wheel.Y) <= float.Epsilon)
                    return;

                lock (_mainMouseWheelLock)
                    _pendingMainMouseWheelDeltas.Enqueue(new Vector2(wheel.X, wheel.Y));
            }

            private void RequestClose(uint viewportId)
            {
                ImGuiViewport* viewport = ImGuiNative.igFindViewportByID(viewportId);
                if (viewport is null)
                    return;

                new ImGuiViewportPtr(viewport).PlatformRequestClose = true;
            }

            private void PushMousePosition(uint viewportId, IWindow window, Vector2 localPosition)
            {
                _controller.MakeCurrent();
                var io = ImGui.GetIO();
                Vector2D<int> clientPosition = GetClientScreenPosition(window);
                io.AddMousePosEvent(clientPosition.X + localPosition.X, clientPosition.Y + localPosition.Y);
                io.AddMouseViewportEvent(viewportId);
            }

            private void PushMouseButton(uint viewportId, MouseButton button, bool down)
            {
                if (!TryTranslateMouseButton(button, out int imGuiButton))
                    return;

                _controller.MakeCurrent();
                var io = ImGui.GetIO();
                io.AddMouseButtonEvent(imGuiButton, down);
                io.AddMouseViewportEvent(viewportId);
            }

            private void PushMouseWheel(uint viewportId, ScrollWheel wheel)
            {
                _controller.MakeCurrent();
                var io = ImGui.GetIO();
                io.AddMouseWheelEvent(wheel.X, wheel.Y);
                io.AddMouseViewportEvent(viewportId);
            }

            private void PushKey(IKeyboard keyboard, Key key, int scancode, bool down)
            {
                _controller.MakeCurrent();
                var io = ImGui.GetIO();
                ImGuiKey imGuiKey = TranslateKey(key);
                io.AddKeyEvent(imGuiKey, down);
                io.SetKeyEventNativeData(imGuiKey, (int)key, scancode);
                io.KeyCtrl = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
                io.KeyAlt = keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight);
                io.KeyShift = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);
                io.KeySuper = keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight);
            }

            private void PushChar(char value)
            {
                _controller.MakeCurrent();
                ImGui.GetIO().AddInputCharacter(value);
            }


            private static bool TryTranslateMouseButton(MouseButton button, out int imGuiButton)
            {
                switch (button)
                {
                    case MouseButton.Left:
                        imGuiButton = 0;
                        return true;
                    case MouseButton.Right:
                        imGuiButton = 1;
                        return true;
                    case MouseButton.Middle:
                        imGuiButton = 2;
                        return true;
                    default:
                        imGuiButton = -1;
                        return false;
                }
            }

            private static ImGuiKey TranslateKey(Key key)
            {
                if (TranslateInputKey is not null)
                    return TranslateInputKey(key);

                return key switch
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
                    Key.A => ImGuiKey.A,
                    Key.C => ImGuiKey.C,
                    Key.V => ImGuiKey.V,
                    Key.X => ImGuiKey.X,
                    Key.Y => ImGuiKey.Y,
                    Key.Z => ImGuiKey.Z,
                    _ => ImGuiKey.None,
                };
            }

            private static Vector2D<int> ToWindowSize(Vector2 size)
                => new(Math.Max(1, (int)MathF.Round(size.X)), Math.Max(1, (int)MathF.Round(size.Y)));

            private static Vector2D<int> ToWindowPosition(Vector2 position)
                => new((int)MathF.Round(position.X), (int)MathF.Round(position.Y));

            private static Vector2D<int> GetClientScreenPosition(IWindow window)
            {
                if (OperatingSystem.IsWindows() && window.Handle != nint.Zero)
                {
                    var point = new NativePoint();
                    if (ClientToScreen(window.Handle, ref point))
                        return new Vector2D<int>(point.X, point.Y);
                }

                return window.Position;
            }

            private static bool TryGetWindowScreenRect(IWindow window, out NativeRect rect)
            {
                if (OperatingSystem.IsWindows() && window.Handle != nint.Zero && GetWindowRect(window.Handle, out rect))
                    return true;

                Vector2D<int> position = GetClientScreenPosition(window);
                Vector2D<int> size = window.Size;
                rect = new NativeRect
                {
                    Left = position.X,
                    Top = position.Y,
                    Right = position.X + Math.Max(1, size.X),
                    Bottom = position.Y + Math.Max(1, size.Y)
                };
                return true;
            }

            private static void SetClientScreenPosition(IWindow window, Vector2D<int> targetClientPosition)
            {
                Vector2D<int> clientPosition = GetClientScreenPosition(window);
                Vector2D<int> windowPosition = window.Position;
                Vector2D<int> clientOffset = clientPosition - windowPosition;
                window.Position = targetClientPosition - clientOffset;
            }


            [StructLayout(LayoutKind.Sequential)]
            private struct NativePoint
            {
                public int X;
                public int Y;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct NativeRect
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;

                public int Width => Right - Left;
                public int Height => Bottom - Top;

                public readonly bool Contains(NativePoint point)
                    => point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
            private struct NativeMonitorInfo
            {
                public int Size;
                public NativeRect Monitor;
                public NativeRect Work;
                public uint Flags;
            }

            private enum MonitorDpiType
            {
                Effective = 0
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct MutableImVector
            {
                public int Size;
                public int Capacity;
                public nint Data;
            }

            private delegate bool MonitorEnumProc(nint monitor, nint hdc, ref NativeRect rect, nint data);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool ClientToScreen(nint hWnd, ref NativePoint point);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool GetCursorPos(out NativePoint point);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);

            [DllImport("user32.dll")]
            private static extern short GetAsyncKeyState(int virtualKey);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool EnumDisplayMonitors(nint hdc, nint clipRect, MonitorEnumProc callback, nint data);

            [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            private static extern bool GetMonitorInfo(nint monitor, ref NativeMonitorInfo monitorInfo);

            [DllImport("shcore.dll")]
            private static extern int GetDpiForMonitor(nint monitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

            private const int NativeVirtualKeyLeftButton = 0x01;
            private const int NativeVirtualKeyRightButton = 0x02;
            private const int NativeVirtualKeyMiddleButton = 0x04;

            private static float GetMonitorDpiScale(nint monitor)
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(6, 3))
                    return 1.0f;

                try
                {
                    return GetDpiForMonitor(monitor, MonitorDpiType.Effective, out uint dpiX, out uint dpiY) == 0
                        ? MathF.Max(MathF.Max(dpiX, dpiY) / 96.0f, 1.0f)
                        : 1.0f;
                }
                catch
                {
                    return 1.0f;
                }
            }

        }
    }
}
