using System.Runtime.InteropServices;
using Silk.NET.Input;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>Lets Win32 paint and timer messages drive resize snapshots while its modal drag loop runs.</summary>
internal sealed class DesktopWin32ModalResizeHook : IDesktopInteractiveResizeHook
{
    private const int WndProcIndex = -4;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmSize = 0x0005;
    private const uint WmPaint = 0x000F;
    private const uint WmTimer = 0x0113;
    private const uint WmSizing = 0x0214;
    private const uint WmEnterSizeMove = 0x0231;
    private const uint WmExitSizeMove = 0x0232;
    private const nuint TimerId = 0x5852454E;
    private const uint MaximumTimerIntervalMs = 250;
    private static readonly object HookSync = new();
    private static readonly Dictionary<nint, nint> OriginalProcedures = [];

    private DesktopSilkWindowBackend? _window;
    private nint _hwnd;
    private nint _previous;
    private nint _callbackPointer;
    private WindowProcedure? _callback;
    private bool _inSizeMove;
    private bool _timerRunning;
    private bool _closing;

    public void Install(DesktopSilkWindowBackend window)
    {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("The Win32 modal resize hook requires Windows.");
        _hwnd = window.OperatingSystemWindowHandle;
        if (_hwnd == 0)
            throw new InvalidOperationException("The Win32 modal resize hook requires an HWND.");

        _window = window;
        _callback = OnWindowMessage;
        _callbackPointer = Marshal.GetFunctionPointerForDelegate(_callback);
        lock (HookSync)
        {
            nint current = GetWindowLongPtr(_hwnd, WndProcIndex);
            if (OriginalProcedures.TryGetValue(_hwnd, out nint original))
            {
                if (current != original && current != _callbackPointer)
                    SetWindowLongPtr(_hwnd, WndProcIndex, original);
                current = original;
            }
            _previous = current;
            if (_previous == _callbackPointer)
                throw new InvalidOperationException("The Win32 resize hook cannot chain to itself.");
            nint replaced = SetWindowLongPtr(_hwnd, WndProcIndex, _callbackPointer);
            if (replaced == 0)
                throw new InvalidOperationException("The Win32 resize hook could not replace the window procedure.");
            OriginalProcedures[_hwnd] = _previous;
        }
    }

    public void OnInputCreated(IInputContext input) { }

    private nint OnWindowMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message is WmClose or WmDestroy or WmNcDestroy)
        {
            _closing = true;
            StopTimer();
        }

        nint result = _previous != 0
            ? CallWindowProc(_previous, hwnd, message, wParam, lParam)
            : DefWindowProc(hwnd, message, wParam, lParam);

        if (message == WmClose && _window?.IsClosing == false)
            _closing = false;

        if (_closing)
        {
            if (message == WmNcDestroy)
                Restore();
            return result;
        }

        switch (message)
        {
            case WmEnterSizeMove:
                _inSizeMove = true;
                _window?.BeginNativeResize();
                StartTimer();
                RequestPaint();
                break;
            case WmSize:
            case WmSizing:
                _window?.UpdateNativeResize();
                break;
            case WmTimer when wParam == TimerId:
                _window?.UpdateNativeResize();
                RequestPaint();
                break;
            case WmPaint when _inSizeMove:
                _window?.UpdateNativeResize();
                break;
            case WmExitSizeMove:
                _inSizeMove = false;
                StopTimer();
                _window?.EndNativeResize();
                break;
        }
        return result;
    }

    private void StartTimer()
    {
        if (_timerRunning || _hwnd == 0)
            return;
        _timerRunning = SetTimer(_hwnd, TimerId, ResolveTimerIntervalMs(), 0) != 0;
    }

    private void StopTimer()
    {
        if (!_timerRunning || _hwnd == 0)
            return;
        KillTimer(_hwnd, TimerId);
        _timerRunning = false;
    }

    private void RequestPaint()
    {
        if (_inSizeMove && !_closing && _hwnd != 0)
            InvalidateRect(_hwnd, 0, false);
    }

    private static uint ResolveTimerIntervalMs()
    {
        string? raw = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.Win32InteractiveResizeTimerMs);
        if (uint.TryParse(raw, out uint interval) && interval is >= 1 and <= MaximumTimerIntervalMs)
            return interval;
        return 1;
    }

    private void Restore()
    {
        if (_hwnd == 0)
            return;
        lock (HookSync)
        {
            nint current = GetWindowLongPtr(_hwnd, WndProcIndex);
            if (current == _callbackPointer && _previous != 0)
                SetWindowLongPtr(_hwnd, WndProcIndex, _previous);
            OriginalProcedures.Remove(_hwnd);
        }
        _hwnd = 0;
        _previous = 0;
        _callbackPointer = 0;
        _callback = null;
    }

    public void Dispose()
    {
        StopTimer();
        Restore();
        _window = null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam);

    private static nint GetWindowLongPtr(nint hwnd, int index)
        => Environment.Is64BitProcess ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);

    private static nint SetWindowLongPtr(nint hwnd, int index, nint procedure)
        => Environment.Is64BitProcess
            ? SetWindowLongPtr64(hwnd, index, procedure)
            : SetWindowLong32(hwnd, index, procedure);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern nint GetWindowLong32(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern nint SetWindowLong32(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CallWindowProc(nint previous, nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nuint SetTimer(nint hwnd, nuint id, uint intervalMs, nint callback);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InvalidateRect(nint hwnd, nint rectangle, bool erase);
}
