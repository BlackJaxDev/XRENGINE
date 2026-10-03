using ImGuiNET;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.UI;

/// <summary>
/// Applies Dear ImGui platform-viewport input and activation semantics to native windows.
/// </summary>
internal sealed class NativeImGuiPlatformWindowBehavior : IImGuiPlatformWindowServices
{
    private const int ExtendedWindowStyleIndex = -20;
    private const long ExtendedStyleAppWindow = 0x00040000L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long TaskbarStyleMask = ExtendedStyleAppWindow | ExtendedStyleToolWindow;
    private const int ShowHidden = 0;
    private const int ShowWithoutActivation = 8;
    private const uint WindowMessageMouseActivate = 0x0021;
    private const uint WindowMessageNonClientHitTest = 0x0084;
    private const int MouseActivateNoActivate = 3;
    private const int HitTestTransparent = -1;
    private const nuint ImGuiViewportSubclassId = 0x58524549;

    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionFrameChanged = 0x0020;

    private static readonly ConcurrentDictionary<nint, long> OriginalTaskbarStyleBits = [];
    private static readonly WindowSubclassProcedure SubclassProcedure = HandleWindowMessage;

    /// <summary>
    /// Returns whether the viewport must be ignored by native hit testing and input routing.
    /// </summary>
    public bool IsInputTransparent(uint flags)
        => ((ImGuiViewportFlags)flags & ImGuiViewportFlags.NoInputs) != 0;

    /// <summary>
    /// Applies persistent Win32 styles represented by the viewport flags.
    /// </summary>
    /// <remarks>
    /// <c>NoFocusOnAppearing</c> is deliberately not persisted as
    /// <c>WS_EX_NOACTIVATE</c>; it only affects the initial show operation. No-input viewports,
    /// such as tooltips, remain mouse-transparent without changing explicit focus semantics.
    /// </remarks>
    public void ConfigureNativeWindow(nint windowHandle, uint flags)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == nint.Zero)
            return;

        nint currentStyles = GetWindowLongPtr(windowHandle, ExtendedWindowStyleIndex);
        long currentStyleBits = currentStyles.ToInt64();
        OriginalTaskbarStyleBits.TryAdd(windowHandle, currentStyleBits & TaskbarStyleMask);

        bool wantsToolWindow = ((ImGuiViewportFlags)flags & ImGuiViewportFlags.NoTaskBarIcon) != 0;
        long desiredTaskbarStyle = wantsToolWindow ? ExtendedStyleToolWindow : ExtendedStyleAppWindow;
        ApplyTaskbarStyle(windowHandle, currentStyleBits, desiredTaskbarStyle);

        _ = SetWindowSubclass(
            windowHandle,
            SubclassProcedure,
            ImGuiViewportSubclassId,
            (nuint)flags);
    }

    /// <summary>
    /// Removes the ImGui-specific native behavior before a platform window is destroyed or abandoned.
    /// </summary>
    public void ReleaseNativeWindow(nint windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == nint.Zero)
            return;

        // A tooltip may still be visible when ImGui retires its platform viewport. Hide it
        // synchronously before restoring WS_EX_APPWINDOW so the shell can never observe a
        // visible application window during the short deferred-disposal interval.
        _ = ShowWindow(windowHandle, ShowHidden);
        _ = RemoveWindowSubclass(windowHandle, SubclassProcedure, ImGuiViewportSubclassId);
        if (OriginalTaskbarStyleBits.TryRemove(windowHandle, out long originalTaskbarStyle))
        {
            nint currentStyles = GetWindowLongPtr(windowHandle, ExtendedWindowStyleIndex);
            ApplyTaskbarStyle(windowHandle, currentStyles.ToInt64(), originalTaskbarStyle);
        }
    }

    /// <summary>
    /// Shows a Windows platform viewport without activating it when requested by ImGui.
    /// </summary>
    /// <returns><see langword="true"/> when the native show operation was handled.</returns>
    public bool TryShowWithoutActivation(nint windowHandle, uint flags)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == nint.Zero)
            return false;

        if (((ImGuiViewportFlags)flags & ImGuiViewportFlags.NoFocusOnAppearing) == 0)
            return false;

        ConfigureNativeWindow(windowHandle, flags);
        _ = ShowWindow(windowHandle, ShowWithoutActivation);
        return true;
    }

    private static nint HandleWindowMessage(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        var flags = (ImGuiViewportFlags)referenceData;
        if (message == WindowMessageNonClientHitTest && ((flags & ImGuiViewportFlags.NoInputs) != 0))
            return new nint(HitTestTransparent);

        if (message == WindowMessageMouseActivate && (flags & ImGuiViewportFlags.NoFocusOnClick) != 0)
            return new nint(MouseActivateNoActivate);

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private static void ApplyTaskbarStyle(nint windowHandle, long currentStyles, long desiredTaskbarStyle)
    {
        long updatedStyles = (currentStyles & ~TaskbarStyleMask) | desiredTaskbarStyle;
        if (updatedStyles == currentStyles)
            return;

        _ = SetWindowLongPtr(windowHandle, ExtendedWindowStyleIndex, new nint(updatedStyles));
        _ = SetWindowPos(
            windowHandle,
            nint.Zero,
            0,
            0,
            0,
            0,
            SetWindowPositionNoSize |
            SetWindowPositionNoMove |
            SetWindowPositionNoZOrder |
            SetWindowPositionNoActivate |
            SetWindowPositionFrameChanged);
    }

    private static nint GetWindowLongPtr(nint windowHandle, int index)
        => nint.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : new nint(GetWindowLong32(windowHandle, index));

    private static nint SetWindowLongPtr(nint windowHandle, int index, nint value)
        => nint.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, value)
            : new nint(SetWindowLong32(windowHandle, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint windowHandle, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint windowHandle, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private delegate nint WindowSubclassProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        nint windowHandle,
        WindowSubclassProcedure subclassProcedure,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(
        nint windowHandle,
        WindowSubclassProcedure subclassProcedure,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);
}
