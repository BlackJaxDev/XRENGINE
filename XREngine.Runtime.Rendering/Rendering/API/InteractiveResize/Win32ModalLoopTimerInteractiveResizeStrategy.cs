using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace XREngine.Rendering;

internal sealed class Win32ModalLoopTimerInteractiveResizeStrategy : InteractiveResizeStrategyBase
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_NCDESTROY = 0x0082;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_SIZING = 0x0214;
    private const uint WM_ENTERSIZEMOVE = 0x0231;
    private const uint WM_EXITSIZEMOVE = 0x0232;
    private const uint WM_WINDOWPOSCHANGED = 0x0047;
    private const nuint TimerId = 0x5852454Eu;
    private const uint DefaultTimerIntervalMs = 16;
    private const int OpenGlActiveSizingRenderHz = 60;
    private const int VulkanActiveSizingRenderHz = 60;
    private const string TimerIntervalEnvironmentVariable = "XRE_WIN32_INTERACTIVE_RESIZE_TIMER_MS";

    private static readonly object s_hookSync = new();
    private static readonly Dictionary<IntPtr, IntPtr> s_originalWndProcs = [];

    private IntPtr _hwnd;
    private IntPtr _previousWndProc;
    private IntPtr _wndProcPtr;
    private WndProc? _wndProc;
    private bool _timerActive;
    private bool _inSizeMove;
    private bool _windowClosing;
    private long _lastSizingRenderTimestamp;
    private int _pendingClientWidth;
    private int _pendingClientHeight;
    private bool _hasPendingClientSize;
    private int _lastAppliedClientWidth;
    private int _lastAppliedClientHeight;
    private uint _timerIntervalMs = DefaultTimerIntervalMs;

    public override EInteractiveWindowResizeStrategy Kind => EInteractiveWindowResizeStrategy.Win32ModalLoopTimer;

    public override void Install(XRWindow window)
    {
        if (_hwnd != IntPtr.Zero || _wndProcPtr != IntPtr.Zero)
            Uninstall();

        base.Install(window);

        if (!OperatingSystem.IsWindows())
        {
            Debug.RenderingWarning(
                "[InteractiveResize] Win32 modal loop timer requested on non-Windows platform window={0}; no native hook installed.",
                window.GetHashCode());
            return;
        }

        _timerIntervalMs = ResolveTimerIntervalMs();
        _hwnd = window.TryGetWin32WindowHandle();
        if (_hwnd == IntPtr.Zero)
        {
            Debug.RenderingWarning(
                "[InteractiveResize] Win32 modal loop timer could not resolve HWND window={0} backend={1}.",
                window.GetHashCode(),
                window.ActualWindowingBackendName);
            return;
        }

        try
        {
            _wndProc = WindowProc;
            _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
            _windowClosing = false;

            IntPtr currentWndProc = GetWindowLongPtr(_hwnd, GWLP_WNDPROC);
            lock (s_hookSync)
            {
                if (s_originalWndProcs.TryGetValue(_hwnd, out IntPtr originalWndProc))
                {
                    if (currentWndProc != originalWndProc && currentWndProc != _wndProcPtr)
                    {
                        Debug.RenderingWarning(
                            "[InteractiveResize] Replacing stale Win32 modal loop timer hook window={0} hwnd=0x{1:X} current=0x{2:X} original=0x{3:X}.",
                            window.GetHashCode(),
                            _hwnd.ToInt64(),
                            currentWndProc.ToInt64(),
                            originalWndProc.ToInt64());
                        SetWindowLongPtr(_hwnd, GWLP_WNDPROC, originalWndProc);
                        currentWndProc = originalWndProc;
                    }

                    _previousWndProc = originalWndProc;
                }
                else
                {
                    _previousWndProc = currentWndProc;
                    s_originalWndProcs[_hwnd] = currentWndProc;
                }

                IntPtr replacedWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _wndProcPtr);
                if (replacedWndProc == _wndProcPtr)
                {
                    SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _previousWndProc);
                    s_originalWndProcs.Remove(_hwnd);
                    throw new InvalidOperationException("Win32 modal loop timer hook attempted to chain to itself.");
                }
            }

            if (_previousWndProc == IntPtr.Zero)
            {
                Debug.RenderingWarning(
                    "[InteractiveResize] Win32 modal loop timer hook returned a null previous WndProc window={0} hwnd=0x{1:X}.",
                    window.GetHashCode(),
                    _hwnd.ToInt64());
            }

            Debug.Rendering(
                "[InteractiveResize] Win32 modal loop timer hook installed window={0} hwnd=0x{1:X} originalWndProc=0x{2:X} intervalMs={3}.",
                window.GetHashCode(),
                _hwnd.ToInt64(),
                _previousWndProc.ToInt64(),
                _timerIntervalMs);
        }
        catch (Exception ex)
        {
            Debug.RenderingWarning(
                "[InteractiveResize] Failed to install Win32 modal loop timer hook window={0}. {1}",
                window.GetHashCode(),
                ex);
            _previousWndProc = IntPtr.Zero;
            _wndProcPtr = IntPtr.Zero;
            _wndProc = null;
        }
    }

    public override void Uninstall()
    {
        StopTimer("uninstall");

        RestoreHook("uninstall");

        base.Uninstall();
    }

    private void RestoreHook(string reason)
    {
        XRWindow? window = Window;
        IntPtr hwnd = _hwnd;
        IntPtr previousWndProc = _previousWndProc;
        IntPtr wndProcPtr = _wndProcPtr;

        if (hwnd != IntPtr.Zero && previousWndProc != IntPtr.Zero)
        {
            try
            {
                lock (s_hookSync)
                {
                    IntPtr originalWndProc = s_originalWndProcs.TryGetValue(hwnd, out IntPtr tracked)
                        ? tracked
                        : previousWndProc;
                    IntPtr currentWndProc = GetWindowLongPtr(hwnd, GWLP_WNDPROC);

                    if (currentWndProc == wndProcPtr)
                    {
                        SetWindowLongPtr(hwnd, GWLP_WNDPROC, originalWndProc);
                        s_originalWndProcs.Remove(hwnd);
                        Debug.Rendering(
                            "[InteractiveResize] Win32 modal loop timer hook restored window={0} hwnd=0x{1:X} reason={2}.",
                            window?.GetHashCode() ?? 0,
                            hwnd.ToInt64(),
                            reason);
                    }
                    else
                    {
                        if (currentWndProc == originalWndProc)
                            s_originalWndProcs.Remove(hwnd);

                        Debug.RenderingWarning(
                            "[InteractiveResize] Win32 modal loop timer hook restore skipped window={0} hwnd=0x{1:X} reason={2} current=0x{3:X} expected=0x{4:X}.",
                            window?.GetHashCode() ?? 0,
                            hwnd.ToInt64(),
                            reason,
                            currentWndProc.ToInt64(),
                            wndProcPtr.ToInt64());
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.RenderingWarning(
                    "[InteractiveResize] Failed to restore Win32 modal loop timer hook window={0}. {1}",
                    window?.GetHashCode() ?? 0,
                    ex);
            }
        }

        _previousWndProc = IntPtr.Zero;
        _wndProcPtr = IntPtr.Zero;
        _hwnd = IntPtr.Zero;
        _wndProc = null;
        _inSizeMove = false;
        _windowClosing = true;
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        if (msg is WM_CLOSE or WM_DESTROY or WM_NCDESTROY)
            return HandleWindowClosingMessage(hwnd, msg, wParam, lParam);

        IntPtr result = CallNextWindowProc(hwnd, msg, wParam, lParam);

        switch (msg)
        {
            case WM_ENTERSIZEMOVE:
                _inSizeMove = true;
                Window?.BeginInteractiveResize("win32-enter-size-move");
                ResetCoalescedClientSize();
                CaptureCurrentClientSize();
                StartTimer();
                break;
            case WM_SIZE:
                if (_inSizeMove)
                {
                    CaptureSizeMessageClientSize(lParam);
                }
                else
                {
                    QueueSizeMessageResize(lParam, "win32-size");
                    RecordCallbackAndRenderImmediate("win32-size");
                }
                break;
            case WM_SIZING:
                CaptureCurrentClientSize();
                break;
            case WM_WINDOWPOSCHANGED:
                if (_inSizeMove)
                    CaptureCurrentClientSize();
                else
                    QueueClientResize("win32-windowposchanged");

                if (!_inSizeMove)
                    RecordCallbackAndRenderImmediate("win32-windowposchanged");
                break;
            case WM_TIMER:
                if (wParam == new UIntPtr(TimerId))
                {
                    ApplyCoalescedClientPresentationResize("win32-timer");
                    RecordCallbackAndRenderRateLimited("win32-timer");
                }
                break;
            case WM_EXITSIZEMOVE:
                _inSizeMove = false;
                EndInteractiveResize("win32-exit-size-move");
                StopTimer("exit-size-move");
                ResetCoalescedClientSize();
                RecordCallbackAndRenderImmediate("win32-exit-size-move");
                break;
        }

        return result;
    }

    private IntPtr CallNextWindowProc(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam)
        => _previousWndProc != IntPtr.Zero && _previousWndProc != _wndProcPtr
            ? CallWindowProc(_previousWndProc, hwnd, msg, wParam, lParam)
            : DefWindowProc(hwnd, msg, wParam, lParam);

    private IntPtr HandleWindowClosingMessage(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        _windowClosing = true;
        _inSizeMove = false;
        ResetCoalescedClientSize();
        StopTimer("window-closing");
        Window?.CancelInteractiveResize("win32-window-closing");

        IntPtr result = CallNextWindowProc(hwnd, msg, wParam, lParam);
        if (msg == WM_NCDESTROY)
            RestoreHook("nc-destroy");

        return result;
    }

    private void StartTimer()
    {
        if (_timerActive || _hwnd == IntPtr.Zero)
            return;

        UIntPtr result = SetTimer(_hwnd, TimerId, _timerIntervalMs, IntPtr.Zero);
        _timerActive = result != UIntPtr.Zero;

        Debug.Rendering(
            "[InteractiveResize] Win32 modal loop timer {0} window={1} hwnd=0x{2:X}.",
            _timerActive ? "started" : "failed to start",
            Window?.GetHashCode() ?? 0,
            _hwnd.ToInt64());
    }

    private void StopTimer(string reason)
    {
        if (!_timerActive || _hwnd == IntPtr.Zero)
            return;

        try
        {
            KillTimer(_hwnd, TimerId);
            Debug.Rendering(
                "[InteractiveResize] Win32 modal loop timer stopped window={0} reason={1}.",
                Window?.GetHashCode() ?? 0,
                reason);
        }
        catch (Exception ex)
        {
            Debug.RenderingWarning(
                "[InteractiveResize] Win32 modal loop timer stop failed window={0}. {1}",
                Window?.GetHashCode() ?? 0,
                ex);
        }
        finally
        {
            _timerActive = false;
        }
    }

    private void QueueClientResize(string reason)
    {
        XRWindow? window = Window;
        if (window is null || _hwnd == IntPtr.Zero)
            return;

        if (TryGetClientSize(out Vector2D<int> clientSize))
        {
            Vector2D<int> framebufferSize = window.ConvertWindowSizeToFramebufferSize(clientSize);
            window.QueueFramebufferResize(framebufferSize, clientSize, reason);
        }
        else
        {
            window.QueueCurrentFramebufferResize(reason);
        }
    }

    private void QueueSizeMessageResize(IntPtr lParam, string reason)
    {
        XRWindow? window = Window;
        if (window is null)
            return;

        if (TryGetSizeMessageClientSize(lParam, out Vector2D<int> clientSize))
        {
            window.QueueFramebufferResize(window.ConvertWindowSizeToFramebufferSize(clientSize), clientSize, reason);
            return;
        }

        QueueClientResize(reason);
    }

    private void CaptureSizeMessageClientSize(IntPtr lParam)
    {
        if (TryGetSizeMessageClientSize(lParam, out Vector2D<int> clientSize))
        {
            StorePendingClientSize(clientSize);
            return;
        }

        CaptureCurrentClientSize();
    }

    private void CaptureCurrentClientSize()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        if (TryGetClientSize(out Vector2D<int> clientSize))
            StorePendingClientSize(clientSize);
    }

    private void StorePendingClientSize(Vector2D<int> clientSize)
    {
        if (clientSize.X <= 0 || clientSize.Y <= 0)
            return;

        _pendingClientWidth = clientSize.X;
        _pendingClientHeight = clientSize.Y;
        _hasPendingClientSize = true;
    }

    private void ResetCoalescedClientSize()
    {
        _pendingClientWidth = 0;
        _pendingClientHeight = 0;
        _hasPendingClientSize = false;
        _lastAppliedClientWidth = 0;
        _lastAppliedClientHeight = 0;
    }

    private bool ApplyCoalescedClientPresentationResize(string reason)
    {
        XRWindow? window = Window;
        if (window is null)
            return false;

        if (!TryGetCoalescedClientSize(out Vector2D<int> clientSize))
        {
            window.QueueInteractivePresentationResize(GetCurrentFramebufferSize(window.Window), reason);
            return true;
        }

        if (_lastAppliedClientWidth == clientSize.X && _lastAppliedClientHeight == clientSize.Y)
            return false;

        _lastAppliedClientWidth = clientSize.X;
        _lastAppliedClientHeight = clientSize.Y;
        window.QueueInteractivePresentationResize(window.ConvertWindowSizeToFramebufferSize(clientSize), clientSize, reason);
        return true;
    }

    private bool TryGetCoalescedClientSize(out Vector2D<int> clientSize)
    {
        if (_hasPendingClientSize)
        {
            clientSize = new Vector2D<int>(_pendingClientWidth, _pendingClientHeight);
            _hasPendingClientSize = false;
            return clientSize.X > 0 && clientSize.Y > 0;
        }

        return TryGetClientSize(out clientSize);
    }

    private void EndInteractiveResize(string reason)
    {
        XRWindow? window = Window;
        if (window is null)
            return;

        if (TryGetClientSize(out Vector2D<int> clientSize))
        {
            window.EndInteractiveResize(window.ConvertWindowSizeToFramebufferSize(clientSize), clientSize, reason);
            return;
        }

        window.EndInteractiveResize(GetCurrentFramebufferSize(window.Window), reason);
    }

    private void RecordCallbackAndRenderRateLimited(string reason)
    {
        if (ShouldRenderByRateLimit(ref _lastSizingRenderTimestamp, ResolveActiveSizingRenderHz()))
            RecordCallbackAndRenderImmediate(reason);
    }

    private int ResolveActiveSizingRenderHz()
        => Window?.Window.API.API == ContextAPI.Vulkan
            ? VulkanActiveSizingRenderHz
            : OpenGlActiveSizingRenderHz;

    private void RecordCallbackAndRenderImmediate(string reason)
        => RecordCallbackAndRenderImmediate(reason, deferWhenOnRenderThread: false);

    private void RecordCallbackAndRenderImmediate(string reason, bool deferWhenOnRenderThread)
    {
        XRWindow? window = Window;
        if (window is null)
            return;

        if (_windowClosing)
        {
            window.InteractiveResizeDiagnostics.RecordSuppressedRender(reason + ":window-closing");
            return;
        }

        window.InteractiveResizeDiagnostics.RecordCallback(reason);
        window.RenderInteractiveResizeFrame(
            reason,
            allowCurrentThread: !deferWhenOnRenderThread,
            deferWhenOnRenderThread: deferWhenOnRenderThread);
    }

    private static bool TryGetSizeMessageClientSize(IntPtr lParam, out Vector2D<int> size)
    {
        long packed = lParam.ToInt64();
        int width = (int)(packed & 0xFFFF);
        int height = (int)((packed >> 16) & 0xFFFF);

        if (width <= 0 || height <= 0)
        {
            size = default;
            return false;
        }

        size = new Vector2D<int>(width, height);
        return true;
    }

    private bool TryGetClientSize(out Vector2D<int> size)
    {
        size = default;
        if (!GetClientRect(_hwnd, out RECT rect))
            return false;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            return false;

        size = new Vector2D<int>(width, height);
        return true;
    }

    private static uint ResolveTimerIntervalMs()
    {
        string? rawValue = Environment.GetEnvironmentVariable(TimerIntervalEnvironmentVariable);
        if (uint.TryParse(rawValue, out uint value) && value is >= 1 and <= 250)
            return value;

        if (!string.IsNullOrWhiteSpace(rawValue))
        {
            Debug.RenderingWarning(
                "[InteractiveResize] Ignoring invalid {0} value '{1}'. Expected 1-250 ms.",
                TimerIntervalEnvironmentVariable,
                rawValue);
        }

        return DefaultTimerIntervalMs;
    }

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, index, value)
            : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, index)
            : new IntPtr(GetWindowLong32(hwnd, index));

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern UIntPtr SetTimer(IntPtr hwnd, nuint nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool KillTimer(IntPtr hwnd, nuint uIDEvent);
}
