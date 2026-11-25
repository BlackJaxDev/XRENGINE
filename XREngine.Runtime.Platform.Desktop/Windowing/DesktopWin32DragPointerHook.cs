using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>Observes native left-button messages without changing mouse capture.</summary>
internal sealed class DesktopWin32DragPointerHook : IDisposable
{
    private const uint LeftButtonDown = 0x0201;
    private const uint LeftButtonUp = 0x0202;
    private const uint LeftButtonDoubleClick = 0x0203;
    private const uint CaptureChanged = 0x0215;
    private const uint CancelMode = 0x001F;
    private const uint NonClientDestroy = 0x0082;
    private const int NextWindow = 2;
    private const int SwappedButtonsMetric = 23;
    private static long s_nextSubclassId;
    private static readonly ConcurrentDictionary<nuint, DesktopWin32DragPointerHook> RetainedHooks = new();

    private readonly DesktopSilkWindowBackend _window;
    private readonly nuint _subclassId;
    private readonly SubclassProcedure _callback;
    private readonly nint _callbackPointer;
    private nint _hwnd;
    private bool _abandoned;

    internal bool IsObserving => !_abandoned && _hwnd != 0;

    internal DesktopWin32DragPointerHook(DesktopSilkWindowBackend window)
    {
        _window = window;
        _hwnd = window.OperatingSystemWindowHandle;
        if (_hwnd == 0)
            throw new InvalidOperationException("Native drag observation requires an HWND.");
        _subclassId = (nuint)Interlocked.Increment(ref s_nextSubclassId);
        _callback = OnWindowMessage;
        _callbackPointer = Marshal.GetFunctionPointerForDelegate(_callback);
        if (!SetWindowSubclass(_hwnd, _callbackPointer, _subclassId, 0))
            throw new InvalidOperationException("Unable to attach the native drag observer.");
    }

    internal void PollHover()
    {
        if (_abandoned || _hwnd == 0 || !_window.HasActiveDesktopDragPress)
            return;
        DesktopDragPointerObservation expected = _window.CaptureDesktopDragObservation();
        try
        {
            nint capture = GetCapture();
            int leftKey = DesktopDragPointerState.LogicalLeftVirtualKey(
                GetSystemMetrics(SwappedButtonsMetric) != 0);
            bool leftDown = (GetAsyncKeyState(leftKey) & 0x8000) != 0;
            if (!GetCursorPos(out NativePoint point))
            {
                _window.RecordDesktopDragCancellation(capture, Stopwatch.GetTimestamp(), expected);
                return;
            }
            nint target = WindowFromPoint(point);
            if (!IsUnambiguousTopLevelTarget(point, target))
                target = 0;
            _window.RecordDesktopDragHover(new(point.X, point.Y), target,
                capture, leftDown, Stopwatch.GetTimestamp(), expected);
        }
        catch (Exception error) { Disable(error); }
    }

    private nint OnWindowMessage(nint hwnd, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint referenceData)
    {
        if (_abandoned)
        {
            if (message == NonClientDestroy)
                RemoveWindowSubclass(hwnd, _callbackPointer, _subclassId);
            return Forward(hwnd, message, wParam, lParam);
        }
        if (message is LeftButtonDown or LeftButtonDoubleClick)
        {
            ulong press = 0;
            try
            {
                NativePoint point = DecodeClientPoint(lParam);
                bool located = ClientToScreen(hwnd, ref point);
                press = _window.RecordDesktopDragPress(
                    located ? new(point.X, point.Y) : default,
                    0,
                    Stopwatch.GetTimestamp());
            }
            catch (Exception error) { Disable(error); }
            nint result = DefSubclassProc(hwnd, message, wParam, lParam);
            if (!_abandoned)
                try { _window.ConfirmDesktopDragCapture(press, GetCapture(), Stopwatch.GetTimestamp()); }
                catch (Exception error) { Disable(error); }
            return result;
        }
        try
        {
            if (message == LeftButtonUp)
            {
                DesktopDragPointerObservation expected = _window.CaptureDesktopDragObservation();
                nint capture = GetCapture();
                NativePoint point = DecodeClientPoint(lParam);
                bool located = ClientToScreen(hwnd, ref point);
                nint target = located ? WindowFromPoint(point) : 0;
                bool geometry = TryCaptureTargetGeometry(target, out Vector2 targetOrigin,
                    out Vector2 targetSize);
                bool unambiguous = located && geometry && IsUnambiguousTopLevelTarget(point, target);
                _window.RecordDesktopDragRelease(new(point.X, point.Y), target, capture,
                    unambiguous, targetOrigin, targetSize, Stopwatch.GetTimestamp(), expected);
            }
            else if (message is CaptureChanged or CancelMode)
            {
                DesktopDragPointerObservation expected = _window.CaptureDesktopDragObservation();
                _window.RecordDesktopDragCancellation(GetCapture(), Stopwatch.GetTimestamp(), expected);
            }
            else if (message == NonClientDestroy)
            {
                _window.InvalidateDesktopDragPointer(Stopwatch.GetTimestamp());
                RemoveWindowSubclass(hwnd, _callbackPointer, _subclassId);
            }
        }
        catch (Exception error) { Disable(error); }
        return Forward(hwnd, message, wParam, lParam);
    }

    private nint Forward(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        nint result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == NonClientDestroy)
            ReleaseAfterDestroy();
        return result;
    }

    private static NativePoint DecodeClientPoint(nint lParam)
    {
        Vector2 point = DesktopDragPointerState.DecodeSignedClientPoint(lParam);
        return new((int)point.X, (int)point.Y);
    }

    private static bool IsUnambiguousTopLevelTarget(NativePoint point, nint target)
    {
        if (target == 0)
            return false;
        nint current = GetTopWindow(0);
        for (int count = 0; current != 0 && count < 4096; count++)
        {
            if (IsWindowVisible(current))
            {
                if (!GetWindowRect(current, out NativeRect bounds))
                    return false;
                if (point.X >= bounds.Left && point.X < bounds.Right &&
                    point.Y >= bounds.Top && point.Y < bounds.Bottom)
                    return current == target;
            }
            current = GetWindow(current, NextWindow);
        }
        return false;
    }

    private static bool TryCaptureTargetGeometry(nint target, out Vector2 origin, out Vector2 size)
    {
        origin = default;
        size = default;
        if (target == 0 || !GetClientRect(target, out NativeRect client) ||
            client.Right <= client.Left || client.Bottom <= client.Top)
            return false;
        NativePoint point = default;
        if (!ClientToScreen(target, ref point))
            return false;
        origin = new(point.X, point.Y);
        size = new(client.Right - client.Left, client.Bottom - client.Top);
        return true;
    }

    private void Disable(Exception error)
    {
        _abandoned = true;
        _window.InvalidateDesktopDragPointer(Stopwatch.GetTimestamp());
        System.Diagnostics.Debug.WriteLine($"Native drag observation stopped: {error}");
    }

    public void Dispose()
    {
        if (_hwnd == 0)
            return;
        _abandoned = true;
        _window.InvalidateDesktopDragPointer(Stopwatch.GetTimestamp());
        if (!RemoveWindowSubclass(_hwnd, _callbackPointer, _subclassId))
        {
            RetainedHooks[_subclassId] = this;
            return;
        }
        _hwnd = 0;
        RetainedHooks.TryRemove(_subclassId, out _);
    }

    private void ReleaseAfterDestroy()
    {
        _abandoned = true;
        _hwnd = 0;
        RetainedHooks.TryRemove(_subclassId, out _);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(nint hwnd, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll", EntryPoint = "SetWindowSubclass", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, nint callback, nuint subclassId, nuint referenceData);
    [DllImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, nint callback, nuint subclassId);
    [DllImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern nint GetCapture();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    private static extern nint GetTopWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
