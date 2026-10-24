using XREngine.Components;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Shows the latest in-process profiler sample.</summary>
public class ProfilerPanel : NativeVirtualTextPanel
{
    private const int MaximumIndexStepsPerTick = 2048;
    private readonly NativeProfilerRows _rows = new();
    private DateTime _lastRefresh;
    private bool _paused;
    private bool _terminal;
    private bool _ownerActive;
    private bool _tickRegistered;

    public ProfilerPanel()
    {
        RowProviderOverride = new UIVirtualTableProvider<NativeProfilerDisplayRow>(_rows,
            UIVirtualTableColumn<NativeProfilerDisplayRow>.Text("scope", "Scope", 420, row => row.Scope),
            UIVirtualTableColumn<NativeProfilerDisplayRow>.Text("total", "Total ms", 100, row => row.Total),
            UIVirtualTableColumn<NativeProfilerDisplayRow>.Text("self", "Self ms", 100, row => row.Self));
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (_terminal || !IsActiveInHierarchy) return;
        _ownerActive = true;
        if (!_tickRegistered)
        {
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
            _tickRegistered = true;
        }
    }

    protected override void OnComponentDeactivated()
    {
        _ownerActive = false;
        _rows.CancelPending();
        try { DetachTick(); }
        finally { base.OnComponentDeactivated(); }
    }

    protected override void OnDestroying()
    {
        _terminal = true;
        _ownerActive = false;
        _rows.Clear();
        try { DetachTick(); }
        finally { base.OnDestroying(); }
    }

    private void DetachTick()
    {
        if (!_tickRegistered) return;
        _tickRegistered = false;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
    }

    protected override void CreateCommands(SceneNode commands)
        => AddCommand("Pause / Resume", TogglePause);

    private void TogglePause()
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy) return;
        SetField(ref _paused, !_paused, publishNotifications: false);
        if (_paused) _rows.CancelPending();
        else RefreshRows();
    }

    private void RefreshTick()
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy) return;
        if (_paused)
        {
            AdvanceRows();
            return;
        }
        DateTime now = DateTime.UtcNow;
        if (now - _lastRefresh >= TimeSpan.FromMilliseconds(250))
        {
            _lastRefresh = now;
            RequestLatest();
        }
        AdvanceRows();
    }

    protected override void RefreshRows()
    {
        if (_terminal || _paused) return;
        RequestLatest();
        AdvanceRows();
    }

    private void RequestLatest()
    {
        Engine.Profiler.TryGetSnapshot(out var snapshot, out _);
        _rows.Request(snapshot);
    }

    private void AdvanceRows()
    {
        int previousCount = _rows.Count;
        if (_rows.Advance(MaximumIndexStepsPerTick))
            SignalRowsChanged(previousCount);
    }
}
