using System.Diagnostics;
using XREngine.Components;

namespace XREngine.Editor;

/// <summary>
/// Writes one layout save after frequent changes, such as wheel scrolling, settle.
/// A pending save is written when the host leaves the scene.
/// </summary>
internal sealed class NativeLayoutSaveDebouncer : XRComponent
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(500);
    private readonly NativeSaveCoalescer _pending = new(SettleTime);
    private readonly WorldTick _tick;
    private Action? _save;
    private bool _tickRegistered;
    private bool _terminal;

    public NativeLayoutSaveDebouncer() => _tick = OnTick;

    /// <summary>Sets the save action. The action writes the complete current layout.</summary>
    internal void Configure(Action save)
    {
        if (!_terminal)
            _save = save;
    }

    /// <summary>Requests a save after changes stop for the settle interval.</summary>
    internal void Request()
    {
        if (_terminal || _save is not { } save)
            return;
        if (!IsActiveInHierarchy)
        {
            _pending.TryTakePending();
            save();
            return;
        }
        _pending.Request(Stopwatch.GetTimestamp());
        if (_tickRegistered)
            return;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, _tick);
        _tickRegistered = true;
    }

    /// <summary>Clears the pending request after a complete save wrote the same layout.</summary>
    internal void MarkSaved()
    {
        _pending.TryTakePending();
        StopTick();
    }

    protected override void OnComponentDeactivated()
    {
        try { Flush(); }
        finally { base.OnComponentDeactivated(); }
    }

    protected override void OnDestroying()
    {
        _terminal = true;
        try { Flush(); }
        finally
        {
            _save = null;
            base.OnDestroying();
        }
    }

    private void OnTick()
    {
        if (!_pending.TryTakeSettled(Stopwatch.GetTimestamp()))
            return;
        StopTick();
        _save?.Invoke();
    }

    private void Flush()
    {
        StopTick();
        if (_pending.TryTakePending())
            _save?.Invoke();
    }

    private void StopTick()
    {
        if (!_tickRegistered)
            return;
        _tickRegistered = false;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, _tick);
    }
}
