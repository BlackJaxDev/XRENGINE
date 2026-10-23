using XREngine.Components;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Shows the latest in-process profiler sample.</summary>
public class ProfilerPanel : NativeVirtualTextPanel
{
    private sealed record ScopeRow(string Scope, string Total, string Self);

    private readonly List<(ulong Id, ScopeRow Item)> _rows = [];
    private readonly NativeTableRows<ScopeRow> _tableRows = new();
    private DateTime _lastRefresh;
    private bool _paused;

    public ProfilerPanel()
    {
        RowProviderOverride = new UIVirtualTableProvider<ScopeRow>(_tableRows,
            UIVirtualTableColumn<ScopeRow>.Text("scope", "Scope", 420, row => row.Scope),
            UIVirtualTableColumn<ScopeRow>.Text("total", "Total ms", 100, row => row.Total),
            UIVirtualTableColumn<ScopeRow>.Text("self", "Self ms", 100, row => row.Self));
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
        base.OnComponentDeactivated();
    }

    protected override void CreateCommands(SceneNode commands)
        => AddCommand("Pause", () => _paused = !_paused);

    private void RefreshTick()
    {
        if (_paused || DateTime.UtcNow - _lastRefresh < TimeSpan.FromMilliseconds(250))
            return;
        _lastRefresh = DateTime.UtcNow;
        RefreshRows();
    }

    protected override void RefreshRows()
    {
        _rows.Clear();
        if (!Engine.Profiler.TryGetSnapshot(out var snapshot, out _) || snapshot is null)
        {
            _rows.Add((1, new ScopeRow("No profiler sample is available.", string.Empty, string.Empty)));
            PublishRows();
            return;
        }
        ulong id = 1;
        _rows.Add((id++, new ScopeRow($"Frame {snapshot.UpdateFrameId} / Render {snapshot.RenderFrameId}",
            snapshot.FrameTime.ToString("F2"), string.Empty)));
        foreach (var thread in snapshot.Threads)
        {
            _rows.Add((id++, new ScopeRow($"Thread {thread.ThreadId}", thread.TotalTimeMs.ToString("F2"), string.Empty)));
            foreach (var root in thread.RootNodes)
                AppendScope(root, 1, ref id);
        }
        PublishRows();
    }

    private void PublishRows()
    {
        int previousCount = _tableRows.Count;
        _tableRows.Replace(_rows);
        SignalRowsChanged(previousCount);
    }

    private void AppendScope(Engine.CodeProfiler.ProfilerNodeSnapshot scope, int depth, ref ulong id)
    {
        if (_rows.Count >= 1000)
            return;
        _rows.Add((id++, new ScopeRow(
            $"{new string(' ', Math.Min(depth, 8) * 2)}{scope.Name}",
            scope.ElapsedMs.ToString("F2"), scope.SelfMs.ToString("F2"))));
        foreach (var child in scope.Children)
            AppendScope(child, depth + 1, ref id);
    }
}
