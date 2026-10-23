using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Silk.NET.Input;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>Lets Win32 paint and timer messages drive resize snapshots while its modal drag loop runs.</summary>
internal sealed class DesktopWin32ModalResizeHook : IDesktopInteractiveResizeHook
{
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
    private static long _nextSubclassId;
    private static readonly ConcurrentDictionary<nuint, DesktopWin32ModalResizeHook> RetainedHooks = new();

    private DesktopSilkWindowBackend? _window;
    private nint _hwnd;
    private nuint _subclassId;
    private nint _callbackPointer;
    private WindowProcedure? _callback;
    private bool _inSizeMove;
    private bool _timerRunning;
    private bool _closing;
    private bool _abandoned;

    public void Install(DesktopSilkWindowBackend window)
    {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("The Win32 modal resize hook requires Windows.");
        _hwnd = window.OperatingSystemWindowHandle;
        if (_hwnd == 0)
            throw new InvalidOperationException("The Win32 modal resize hook requires an HWND.");

        _window = window;
        _subclassId = (nuint)Interlocked.Increment(ref _nextSubclassId);
        _callback = OnWindowMessage;
        _callbackPointer = Marshal.GetFunctionPointerForDelegate(_callback);
        if (!SetWindowSubclass(_hwnd, _callbackPointer, _subclassId, 0))
        {
            _hwnd = 0;
            throw new InvalidOperationException("The Win32 resize hook could not attach its subclass.");
        }
    }

    public void OnInputCreated(IInputContext input) { }

    private nint OnWindowMessage(nint hwnd, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint referenceData)
    {
        if (message == WmNcDestroy)
            RemoveWindowSubclass(hwnd, _callbackPointer, _subclassId);
        if (!_abandoned && message is WmClose or WmDestroy or WmNcDestroy)
        {
            _closing = true;
            StopTimer();
        }

        nint result = DefSubclassProc(hwnd, message, wParam, lParam);

        try
        {
            if (_abandoned)
                return result;
            if (message == WmClose && _window?.IsClosing == false)
                _closing = false;

            if (_closing)
                return result;

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
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Win32 modal resize handling failed: {error}");
        }
        finally
        {
            if (message == WmNcDestroy)
                ReleaseAfterDestroy();
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
        _abandoned = true;
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

    public void Dispose()
    {
        StopTimer();
        Restore();
        _window = null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll", EntryPoint = "SetWindowSubclass", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, nint callback, nuint subclassId, nuint referenceData);
    [DllImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, nint callback, nuint subclassId);
    [DllImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nuint SetTimer(nint hwnd, nuint id, uint intervalMs, nint callback);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InvalidateRect(nint hwnd, nint rectangle, bool erase);
}
