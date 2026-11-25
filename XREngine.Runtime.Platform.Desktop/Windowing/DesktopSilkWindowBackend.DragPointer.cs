using System.Diagnostics;
using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

internal sealed partial class DesktopSilkWindowBackend
{
    private DesktopWin32DragPointerHook? _dragPointerHook;
    private DesktopDragPointerState? _dragPointerState;

    public RuntimeDesktopDragPointerSnapshot ReadDesktopDragPointer()
    {
        lock (_snapshotSync)
            return _dragPointerState?.Snapshot ?? default;
    }

    internal bool HasActiveDesktopDragPress
    {
        get
        {
            lock (_snapshotSync)
                return _dragPointerState?.Snapshot.LeftButtonDown == true;
        }
    }

    internal DesktopDragPointerObservation CaptureDesktopDragObservation()
    {
        lock (_snapshotSync)
            return _dragPointerState?.CurrentObservation ?? default;
    }

    private void InstallDragPointerHook()
    {
        if (!OperatingSystem.IsWindows() || _operatingSystemWindowHandle == 0)
            return;
        lock (_snapshotSync)
        {
            _dragPointerState = new(LifetimeGeneration, _operatingSystemWindowHandle);
            _dragPointerState.RefreshGeometry(_surface,
                new Vector2(_clientScreenPosition.X, _clientScreenPosition.Y));
        }
        try { _dragPointerHook = new DesktopWin32DragPointerHook(this); }
        catch (Exception error)
        {
            _dragPointerHook = null;
            lock (_snapshotSync) _dragPointerState = null;
            System.Diagnostics.Debug.WriteLine($"Native drag observation is unavailable: {error}");
        }
    }

    private void DisposeDragPointerHook()
    {
        _dragPointerHook?.Dispose();
        _dragPointerHook = null;
        InvalidateDesktopDragPointer(Stopwatch.GetTimestamp());
    }

    private void RestoreDesktopDragPointer()
    {
        if (_dragPointerHook?.IsObserving != true || _operatingSystemWindowHandle == 0)
            return;
        lock (_snapshotSync)
        {
            Vector2 origin = new(_clientScreenPosition.X, _clientScreenPosition.Y);
            if (_dragPointerState is { } state)
                state.Restore(_operatingSystemWindowHandle, _surface, origin);
            else
            {
                _dragPointerState = new(LifetimeGeneration, _operatingSystemWindowHandle);
                _dragPointerState.RefreshGeometry(_surface, origin);
            }
        }
    }

    private (WindowSurfaceSnapshot Surface, Vector2 ClientOrigin, bool Valid) CaptureDragGeometry(long ticks)
    {
        Vector2D<int> client = _window.Size;
        Vector2D<int> framebuffer = _window.FramebufferSize;
        NativePoint point = default;
        bool valid = _operatingSystemWindowHandle != 0 &&
            ClientToScreen(_operatingSystemWindowHandle, ref point);
        WindowSurfaceSnapshot surface = new(
            _surfaceSequence,
            client.X, client.Y, framebuffer.X, framebuffer.Y,
            client.X > 0 ? (float)framebuffer.X / client.X : 1.0f,
            client.Y > 0 ? (float)framebuffer.Y / client.Y : 1.0f,
            _window.WindowState == WindowState.Minimized,
            _interactiveResize, ticks);
        return (surface, new(point.X, point.Y), valid && surface.HasValidClientExtent);
    }

    internal ulong RecordDesktopDragPress(Vector2 point, nint target, long ticks)
    {
        var geometry = CaptureDragGeometry(ticks);
        lock (_snapshotSync)
            return _dragPointerState?.BeginPress(ticks, point, target,
                geometry.Surface, geometry.ClientOrigin) ?? 0;
    }

    internal void ConfirmDesktopDragCapture(ulong press, nint capture, long ticks)
    {
        var geometry = CaptureDragGeometry(ticks);
        lock (_snapshotSync)
            _dragPointerState?.ConfirmCapture(press, capture, ticks,
                geometry.Surface, geometry.ClientOrigin);
    }

    internal void RecordDesktopDragRelease(Vector2 point, nint target, nint capture,
        bool targetUnambiguous, Vector2 targetClientOrigin, Vector2 targetClientSize, long ticks,
        DesktopDragPointerObservation expected)
    {
        var geometry = CaptureDragGeometry(ticks);
        lock (_snapshotSync)
            _dragPointerState?.Release(ticks, point, target, capture,
                targetUnambiguous && geometry.Valid, targetClientOrigin, targetClientSize,
                geometry.Surface, geometry.ClientOrigin, expected);
    }

    internal void RecordDesktopDragCancellation(nint capture, long ticks,
        DesktopDragPointerObservation expected)
    {
        var geometry = CaptureDragGeometry(ticks);
        lock (_snapshotSync)
            _dragPointerState?.Cancel(ticks, capture, geometry.Surface, geometry.ClientOrigin,
                expected);
    }

    internal void RecordDesktopDragHover(Vector2 point, nint target, nint capture,
        bool leftButtonDown, long ticks, DesktopDragPointerObservation expected)
    {
        var geometry = CaptureDragGeometry(ticks);
        lock (_snapshotSync)
            _dragPointerState?.Observe(ticks, point, target, capture, leftButtonDown,
                geometry.Surface, geometry.ClientOrigin, expected);
    }

    internal void InvalidateDesktopDragPointer(long ticks)
    {
        lock (_snapshotSync)
            _dragPointerState?.Invalidate(ticks);
    }
}
