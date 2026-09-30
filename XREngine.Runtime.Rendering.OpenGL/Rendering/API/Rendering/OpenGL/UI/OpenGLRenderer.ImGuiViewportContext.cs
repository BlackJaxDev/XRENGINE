using ImGuiNET;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Vectors;
using XREngine.Input.Devices;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private sealed unsafe partial class OpenGLImGuiMultiViewportController
    {
        private void EnsureMainViewportPlatformData()
        {
            ImGuiViewportPtr viewport = ImGui.GetMainViewport();
            viewport.PlatformHandle = _mainWindow.PlatformWindowHandle;
            viewport.PlatformHandleRaw = _mainWindow.OperatingSystemWindowHandle;
            IVector2 position = _mainWindow.ClientScreenPosition;
            viewport.Pos = new Vector2(position.X, position.Y);
            viewport.DpiScale = GetWindowDpiScale(_mainWindow);
        }

        private bool TryGetMousePosition(out Vector2 position, out uint viewportId)
        {
            WindowInputSnapshot input = _renderer.XRWindow.LatestWindowInputSnapshot;
            IVector2 origin = _mainWindow.ClientScreenPosition;
            position = new Vector2(origin.X + input.PointerX, origin.Y + input.PointerY);
            viewportId = ResolveHoveredViewportId(position);
            return input.HasMouse;
        }

        private uint ResolveHoveredViewportId(Vector2 screenPosition)
        {
            foreach (PlatformWindow window in _platformWindows.Values)
            {
                if (window.IsDisposed || !window.AcceptsInputs)
                    continue;
                if (TryGetWindowScreenRect(window.Window, out NativeRect rect) && rect.Contains(screenPosition))
                    return window.ViewportId;
            }

            return TryGetWindowScreenRect(_mainWindow, out NativeRect main) && main.Contains(screenPosition)
                ? ImGui.GetMainViewport().ID
                : 0;
        }

        private bool TryReadMouseButtonState(out bool left, out bool right, out bool middle)
        {
            left = right = middle = false;
            ReadOnlySpan<EMouseButton> main = _renderer.XRWindow.LatestWindowInputSnapshot.PressedMouseButtonSpan;
            AccumulateMouseButtons(main, ref left, ref right, ref middle);
            foreach (PlatformWindow window in _platformWindows.Values)
                AccumulateMouseButtons(window.Window.Input.PressedMouseButtonSpan, ref left, ref right, ref middle);
            return true;
        }

        private static void AccumulateMouseButtons(ReadOnlySpan<EMouseButton> buttons, ref bool left, ref bool right, ref bool middle)
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                switch (buttons[i])
                {
                    case EMouseButton.LeftClick: left = true; break;
                    case EMouseButton.RightClick: right = true; break;
                    case EMouseButton.MiddleClick: middle = true; break;
                }
            }
        }

        private void RequestClose(uint viewportId)
        {
            ImGuiViewport* viewport = ImGuiNative.igFindViewportByID(viewportId);
            if (viewport is not null)
                new ImGuiViewportPtr(viewport).PlatformRequestClose = true;
        }

        private static IVector2 ToWindowSize(Vector2 size)
            => new(Math.Max(1, (int)MathF.Round(size.X)), Math.Max(1, (int)MathF.Round(size.Y)));

        private static IVector2 ToWindowPosition(Vector2 position)
            => new((int)MathF.Round(position.X), (int)MathF.Round(position.Y));

        private static IVector2 GetClientScreenPosition(IRuntimeWindowBackend window)
            => window.ClientScreenPosition;

        private static bool TryGetWindowScreenRect(IRuntimeWindowBackend window, out NativeRect rect)
        {
            IVector2 position = window.ClientScreenPosition;
            WindowSurfaceSnapshot surface = window.Surface;
            rect = new NativeRect
            {
                Left = position.X,
                Top = position.Y,
                Right = position.X + Math.Max(1, surface.ClientWidth),
                Bottom = position.Y + Math.Max(1, surface.ClientHeight),
            };
            return true;
        }

        private static void SetClientScreenPosition(IRuntimeWindowBackend window, IVector2 position)
            => window.RequestClientScreenPosition(position);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
            public readonly bool Contains(Vector2 point)
                => point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MutableImVector
        {
            public int Size;
            public int Capacity;
            public nint Data;
        }

    }
}
