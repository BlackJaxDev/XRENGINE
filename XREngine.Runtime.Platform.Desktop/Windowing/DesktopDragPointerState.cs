using System.Numerics;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>Keeps one window's native press and first terminal receipt.</summary>
internal sealed class DesktopDragPointerState(long backendLifetime, nint sourceWindowHandle)
{
    private RuntimeDesktopDragPointerSnapshot _snapshot = new(
        backendLifetime, sourceWindowHandle, 0, 0, 0, false, 0, default, 0,
        default, default, default);

    internal RuntimeDesktopDragPointerSnapshot Snapshot => _snapshot;

    internal DesktopDragPointerObservation CurrentObservation => new(
        _snapshot.SourceWindowHandle, _snapshot.PressSequence, _snapshot.Sequence);

    private bool Matches(DesktopDragPointerObservation observation) =>
        observation.SourceWindowHandle != 0 && _snapshot.LeftButtonDown &&
        _snapshot.SourceWindowHandle == observation.SourceWindowHandle &&
        _snapshot.PressSequence == observation.PressSequence &&
        _snapshot.Sequence == observation.Sequence;

    internal void RefreshGeometry(WindowSurfaceSnapshot surface, Vector2 clientOrigin)
    {
        _snapshot = _snapshot with
        {
            Sequence = checked(_snapshot.Sequence + 1),
            TimestampTicks = surface.TimestampTicks,
            Surface = surface,
            ClientScreenPosition = clientOrigin,
        };
    }

    internal static Vector2 DecodeSignedClientPoint(nint lParam)
    {
        long packed = lParam;
        return new((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
    }

    internal static int LogicalLeftVirtualKey(bool swappedButtons) => swappedButtons ? 0x02 : 0x01;

    internal ulong BeginPress(long ticks, Vector2 point, nint target,
        WindowSurfaceSnapshot surface, Vector2 clientOrigin)
    {
        if (_snapshot.SourceWindowHandle == 0)
            return 0;
        ulong press = checked(_snapshot.PressSequence + 1);
        _snapshot = _snapshot with
        {
            Sequence = checked(_snapshot.Sequence + 1),
            TimestampTicks = ticks,
            PressSequence = press,
            LeftButtonDown = true,
            CaptureWindowHandle = 0,
            ScreenPosition = point,
            TargetWindowHandle = target,
            Surface = surface,
            ClientScreenPosition = clientOrigin,
        };
        return press;
    }

    internal void ConfirmCapture(ulong press, nint capture, long ticks,
        WindowSurfaceSnapshot surface, Vector2 clientOrigin)
    {
        if (_snapshot.SourceWindowHandle == 0 || !_snapshot.LeftButtonDown ||
            _snapshot.PressSequence != press)
            return;
        if (capture != _snapshot.SourceWindowHandle)
        {
            Cancel(ticks, capture, surface, clientOrigin);
            return;
        }
        _snapshot = _snapshot with
        {
            Sequence = checked(_snapshot.Sequence + 1),
            TimestampTicks = ticks,
            CaptureWindowHandle = capture,
            Surface = surface,
            ClientScreenPosition = clientOrigin,
        };
    }

    internal void Observe(long ticks, Vector2 point, nint target, nint capture,
        bool leftButtonDown, WindowSurfaceSnapshot surface, Vector2 clientOrigin,
        DesktopDragPointerObservation? expected = null)
    {
        if (expected is { } observation && !Matches(observation))
            return;
        if (_snapshot.LeftButtonDown &&
            (!leftButtonDown || capture != _snapshot.SourceWindowHandle))
            Cancel(ticks, capture, surface, clientOrigin);
        _snapshot = _snapshot with
        {
            Sequence = checked(_snapshot.Sequence + 1),
            TimestampTicks = ticks,
            CaptureWindowHandle = capture,
            ScreenPosition = point,
            TargetWindowHandle = target,
            Surface = surface,
            ClientScreenPosition = clientOrigin,
        };
    }

    internal void Release(long ticks, Vector2 point, nint target, nint capture,
        bool targetUnambiguous, Vector2 targetClientOrigin, Vector2 targetClientSize,
        WindowSurfaceSnapshot surface, Vector2 clientOrigin,
        DesktopDragPointerObservation? expected = null)
    {
        if (expected is { } observation && !Matches(observation))
            return;
        bool active = _snapshot.LeftButtonDown;
        bool captured = _snapshot.CaptureWindowHandle == _snapshot.SourceWindowHandle &&
            capture == _snapshot.SourceWindowHandle;
        bool validTarget = targetUnambiguous && target != 0 &&
            float.IsFinite(targetClientOrigin.X) && float.IsFinite(targetClientOrigin.Y) &&
            float.IsFinite(targetClientSize.X) && float.IsFinite(targetClientSize.Y) &&
            targetClientSize.X > 0 && targetClientSize.Y > 0;
        RuntimeDesktopDragTerminal terminal = _snapshot.Terminal;
        if (active && terminal.PressSequence != _snapshot.PressSequence)
            terminal = new(_snapshot.PressSequence,
                captured && validTarget ? RuntimeDesktopDragTerminalKind.Released :
                    RuntimeDesktopDragTerminalKind.Cancelled,
                point, target, ticks, targetClientOrigin, targetClientSize);
        _snapshot = _snapshot with
        {
            Sequence = checked(_snapshot.Sequence + 1),
            TimestampTicks = ticks,
            LeftButtonDown = false,
            CaptureWindowHandle = capture,
            ScreenPosition = point,
            TargetWindowHandle = target,
            Terminal = terminal,
            Surface = surface,
            ClientScreenPosition = clientOrigin,
        };
    }

    internal void Cancel(long ticks, nint capture,
        WindowSurfaceSnapshot surface, Vector2 clientOrigin,
        DesktopDragPointerObservation? expected = null)
    {
        if (expected is { } observation && !Matches(observation))
            return;
        RuntimeDesktopDragTerminal terminal = _snapshot.Terminal;
        if (_snapshot.LeftButtonDown && terminal.PressSequence != _snapshot.PressSequence)
            terminal = new(_snapshot.PressSequence, RuntimeDesktopDragTerminalKind.Cancelled,
                _snapshot.ScreenPosition, _snapshot.TargetWindowHandle, ticks);
        _snapshot = _snapshot with
        {
            Sequence = checked(_snapshot.Sequence + 1),
            TimestampTicks = ticks,
            LeftButtonDown = false,
            CaptureWindowHandle = capture,
            Terminal = terminal,
            Surface = surface,
            ClientScreenPosition = clientOrigin,
        };
    }

    internal void Invalidate(long ticks)
    {
        RuntimeDesktopDragTerminal terminal = _snapshot.PressSequence == 0 ? default :
            new(_snapshot.PressSequence, RuntimeDesktopDragTerminalKind.Cancelled,
                _snapshot.ScreenPosition, _snapshot.TargetWindowHandle, ticks);
        _snapshot = _snapshot with
        {
            Sequence = checked(_snapshot.Sequence + 1),
            TimestampTicks = ticks,
            SourceWindowHandle = 0,
            LeftButtonDown = false,
            CaptureWindowHandle = 0,
            Terminal = terminal,
        };
    }

    internal void Restore(nint sourceWindowHandle, WindowSurfaceSnapshot surface, Vector2 clientOrigin)
    {
        _snapshot = _snapshot with
        {
            Sequence = checked(_snapshot.Sequence + 1),
            TimestampTicks = surface.TimestampTicks,
            SourceWindowHandle = sourceWindowHandle,
            LeftButtonDown = false,
            CaptureWindowHandle = 0,
            Terminal = default,
            Surface = surface,
            ClientScreenPosition = clientOrigin,
        };
    }
}
