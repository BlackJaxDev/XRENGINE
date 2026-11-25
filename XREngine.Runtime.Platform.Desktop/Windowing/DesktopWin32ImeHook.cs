using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>Captures Win32 preedit and result strings on the window owner thread.</summary>
internal sealed class DesktopWin32ImeHook : IDisposable
{
    private const uint StartComposition = 0x010D;
    private const uint EndComposition = 0x010E;
    private const uint Composition = 0x010F;
    private const uint KeyDown = 0x0100;
    private const uint NonClientDestroy = 0x0082;
    private const int CompositionString = 0x0008;
    private const int CursorPosition = 0x0080;
    private const int ResultString = 0x0800;
    private static long _nextSubclassId;
    private static readonly ConcurrentDictionary<nuint, DesktopWin32ImeHook> RetainedHooks = new();

    private readonly DesktopSilkWindowBackend _window;
    private readonly nuint _subclassId;
    private readonly SubclassProcedure _callback;
    private readonly nint _callbackPointer;
    private nint _hwnd;
    private bool _abandoned;
    private bool _composing;
    private DesktopSilkWindowBackend.ImeFocusToken? _sessionToken;
    private string _preeditText = string.Empty;
    private string _pendingResultCharacters = string.Empty;
    private int _pendingResultOffset;
    private readonly uint _candidateRefreshMessage;
    private long _candidatePoint;
    private int _candidateSet;
    private int _candidateMessagePending;

    public void SetCandidatePosition(System.Numerics.Vector2 point)
    {
        int x = (int)MathF.Round(point.X);
        int y = (int)MathF.Round(point.Y);
        Interlocked.Exchange(ref _candidatePoint, ((long)x << 32) | (uint)y);
        Volatile.Write(ref _candidateSet, 1);
        if (!_abandoned && _hwnd != 0 &&
            Interlocked.Exchange(ref _candidateMessagePending, 1) == 0 &&
            !PostMessage(_hwnd, _candidateRefreshMessage, 0, 0))
            Volatile.Write(ref _candidateMessagePending, 0);
    }

    public DesktopWin32ImeHook(DesktopSilkWindowBackend window)
    {
        _window = window;
        _hwnd = window.OperatingSystemWindowHandle;
        if (_hwnd == 0)
            throw new InvalidOperationException("Win32 IME requires an HWND.");
        _subclassId = (nuint)Interlocked.Increment(ref _nextSubclassId);
        _candidateRefreshMessage = RegisterWindowMessage("XRENGINE.IME.CandidateRefresh");
        if (_candidateRefreshMessage == 0)
            throw new InvalidOperationException("Unable to register Win32 IME candidate refresh message.");
        _callback = OnWindowMessage;
        _callbackPointer = Marshal.GetFunctionPointerForDelegate(_callback);
        if (!SetWindowSubclass(_hwnd, _callbackPointer, _subclassId, 0))
            throw new InvalidOperationException("Unable to attach Win32 IME subclass.");
    }

    public bool ConsumeResultCharacter(char character)
    {
        if (_composing)
            return true;
        if (_pendingResultOffset >= _pendingResultCharacters.Length)
            return false;
        if (_pendingResultCharacters[_pendingResultOffset] != character)
        {
            _pendingResultCharacters = string.Empty;
            _pendingResultOffset = 0;
            return false;
        }
        _pendingResultOffset++;
        if (_pendingResultOffset == _pendingResultCharacters.Length)
        {
            _pendingResultCharacters = string.Empty;
            _pendingResultOffset = 0;
        }
        return true;
    }

    private nint OnWindowMessage(nint hwnd, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint referenceData)
    {
        if (!_abandoned)
        {
            try
            {
                if (message == _candidateRefreshMessage)
                {
                    Volatile.Write(ref _candidateMessagePending, 0);
                    PlaceCandidate();
                }
                switch (message)
                {
                    case StartComposition:
                        PlaceCandidate();
                        _sessionToken = _window.CaptureImeFocusToken();
                        _composing = true;
                        _preeditText = string.Empty;
                        _pendingResultCharacters = string.Empty;
                        _pendingResultOffset = 0;
                        _window.RecordImeEvent(SessionToken(), WindowImeEventKind.Start);
                        break;
                    case Composition:
                        HandleComposition(lParam);
                        break;
                    case KeyDown when !_composing:
                        _pendingResultCharacters = string.Empty;
                        _pendingResultOffset = 0;
                        break;
                    case EndComposition:
                        if (_composing)
                            _window.RecordImeEvent(SessionToken(), WindowImeEventKind.Cancel);
                        _composing = false;
                        _sessionToken = null;
                        _preeditText = string.Empty;
                        break;
                }
            }
            catch (Exception error)
            {
                _composing = false;
                _preeditText = string.Empty;
                try
                {
                    if (_sessionToken is { } token)
                        _window.RecordImeEvent(token, WindowImeEventKind.Cancel);
                    _sessionToken = null;
                    System.Diagnostics.Debug.WriteLine($"Win32 IME handling failed: {error}");
                }
                catch { }
            }
        }
        if (message == NonClientDestroy)
            RemoveWindowSubclass(hwnd, _callbackPointer, _subclassId);
        nint result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == NonClientDestroy)
            ReleaseAfterDestroy();
        return result;
    }

    private void HandleComposition(nint lParam)
    {
        PlaceCandidate();
        bool hasResult = ((long)lParam & ResultString) != 0;
        bool hasPreedit = ((long)lParam & CompositionString) != 0;
        bool hasCursor = ((long)lParam & CursorPosition) != 0;
        if (lParam == 0)
        {
            if (_composing)
                _window.RecordImeEvent(SessionToken(), WindowImeEventKind.Cancel);
            _composing = false;
            _preeditText = string.Empty;
            return;
        }
        if (hasResult)
        {
            if (!_composing)
                _window.RecordImeEvent(SessionToken(), WindowImeEventKind.Start);
            string committedText = ReadCompositionString(ResultString);
            _pendingResultCharacters = committedText;
            _pendingResultOffset = 0;
            _composing = false;
            _preeditText = string.Empty;
            _window.RecordImeEvent(SessionToken(), WindowImeEventKind.Commit, committedText);
        }
        if (hasPreedit || hasCursor)
        {
            if (!_composing)
            {
                _composing = true;
                _window.RecordImeEvent(SessionToken(), WindowImeEventKind.Start);
            }
            if (hasPreedit)
                _preeditText = ReadCompositionString(CompositionString);
            int caret = ReadCursorPosition();
            _window.RecordImeEvent(SessionToken(), WindowImeEventKind.Update, _preeditText,
                Math.Clamp(caret, 0, _preeditText.Length));
        }
    }

    private DesktopSilkWindowBackend.ImeFocusToken SessionToken()
        => _sessionToken ??= _window.CaptureImeFocusToken();

    private string ReadCompositionString(int kind)
    {
        nint context = ImmGetContext(_hwnd);
        if (context == 0)
            return string.Empty;
        try
        {
            int byteCount = ImmGetCompositionString(context, kind, 0, 0);
            if (byteCount <= 0 || byteCount > 1_048_576)
                return string.Empty;
            nint buffer = Marshal.AllocHGlobal(byteCount);
            try
            {
                int copied = ImmGetCompositionString(context, kind, buffer, (uint)byteCount);
                return copied > 0 ? Marshal.PtrToStringUni(buffer, copied / 2) ?? string.Empty : string.Empty;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { ImmReleaseContext(_hwnd, context); }
    }

    private int ReadCursorPosition()
    {
        nint context = ImmGetContext(_hwnd);
        if (context == 0)
            return 0;
        try { return Math.Max(0, ImmGetCompositionString(context, CursorPosition, 0, 0)); }
        finally { ImmReleaseContext(_hwnd, context); }
    }

    private void PlaceCandidate()
    {
        if (Volatile.Read(ref _candidateSet) == 0)
            return;
        long packed = Interlocked.Read(ref _candidatePoint);
        nint context = ImmGetContext(_hwnd);
        if (context == 0)
            return;
        try
        {
            NativePoint point = new((int)(packed >> 32), (int)packed);
            ImmSetCompositionWindow(context, new CompositionForm(0x0002, point, default));
            ImmSetCandidateWindow(context, new CandidateForm(0, 0x0040, point, default));
        }
        finally { ImmReleaseContext(_hwnd, context); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CompositionForm(uint Style, NativePoint Position, NativeRect Area);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CandidateForm(uint Index, uint Style, NativePoint Position, NativeRect Area);

    public void Dispose()
    {
        if (_hwnd == 0)
            return;
        _abandoned = true;
        if (!RemoveWindowSubclass(_hwnd, _callbackPointer, _subclassId))
        {
            // A callback still present in the native subclass chain must remain rooted.
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
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("imm32.dll")]
    private static extern nint ImmGetContext(nint hwnd);
    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(nint hwnd, nint context);
    [DllImport("imm32.dll", EntryPoint = "ImmGetCompositionStringW")]
    private static extern int ImmGetCompositionString(nint context, int kind, nint buffer, uint length);
    [DllImport("imm32.dll", EntryPoint = "ImmSetCompositionWindow")]
    private static extern bool ImmSetCompositionWindow(nint context, in CompositionForm form);
    [DllImport("imm32.dll", EntryPoint = "ImmSetCandidateWindow")]
    private static extern bool ImmSetCandidateWindow(nint context, in CandidateForm form);
}
