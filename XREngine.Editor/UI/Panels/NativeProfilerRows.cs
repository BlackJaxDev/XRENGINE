using System.Globalization;
using XREngine.Data.Profiling;
using XREngine.Rendering.UI;
using Frame = XREngine.Engine.CodeProfiler.ProfilerFrameSnapshot;
using Thread = XREngine.Engine.CodeProfiler.ProfilerThreadSnapshot;
using Node = XREngine.Engine.CodeProfiler.ProfilerNodeSnapshot;

namespace XREngine.Editor;

/// <summary>Keeps one complete profiler index and formats only requested rows.</summary>
internal sealed class NativeProfilerRows : IUIVirtualTableSource<NativeProfilerDisplayRow>, IVirtualListItemIndex
{
    private const int MaximumCachedRows = 2048;
    private static readonly string[] Indents = ["", "  ", "    ", "      ", "        ", "          ", "            ", "              ", "                "];
    private readonly NativeProfilerProjection<Thread, Node> _projection = new(
        static thread => thread.RootNodes, static node => node.Children);
    private readonly NativeProfilerRootAggregator _roots = new();
    private readonly List<int> _rootOrder = [];
    private readonly List<int> _rootChildOrder = [];
    private readonly Comparison<int> _rootTimeComparison;
    private readonly Comparison<int> _rootNameComparison;
    private readonly Dictionary<int, NativeProfilerDisplayRow> _formatted = [];
    private readonly Queue<int> _formatOrder = [];
    private readonly List<NativeProfilerDisplayRow> _diagnostics = [];
    private Frame? _published;
    private Frame? _componentFrame;
    private bool _componentTimingEnabled;
    private Frame? _building;
    private Frame? _requested;
    private bool _requestedIsWorst;
    private bool _buildingIsWorst;
    private bool _publishedIsWorst;
    private bool _hasRequest;
    private ulong _firstId = 1;
    private ulong _nextId = 2;
    private ENativeProfilerView _view;
    private float _rootMinimum;
    private float _rootMaximum;
    private bool _sortRootsByTime = true;
    private string? _selectedRootName;
    private ProfilerScopeKind _selectedRootKind;
    private int _selectedRootIndex = -1;

    public int Count => _view switch
    {
        ENativeProfilerView.CpuTimings => _projection.Count + 1,
        ENativeProfilerView.CpuRoots => _rootOrder.Count + 4 +
            (_roots.SkippedRootCalls == 0 ? 0 : 1) +
            (_selectedRootIndex < 0 ? 0 : 1 + _rootChildOrder.Count +
                (_roots.SkippedChildPaths == 0 ? 0 : 1)),
        ENativeProfilerView.ComponentTimings => _componentTimingEnabled
            ? (_componentFrame?.ComponentTimings?.Components.Count ?? 0) + 1 : 1,
        _ => Math.Max(1, _diagnostics.Count),
    };
    public ENativeProfilerView View => _view;
    public ulong Revision { get; private set; } = 1;
    internal int LastIndexWork { get; private set; }
    internal int FormattedRowCount => _formatted.Count;

    public NativeProfilerRows()
    {
        _rootTimeComparison = CompareRootTime;
        _rootNameComparison = CompareRootName;
    }

    internal bool TryGetRootSample(string? name, ProfilerScopeKind kind,
        out long publicationId, out float milliseconds)
    {
        publicationId = _roots.PublishedFrame?.PublicationId ?? 0;
        milliseconds = 0f;
        if (name is null || _roots.PublishedFrame is null)
            return false;
        for (int index = 0; index < _roots.Count; index++)
        {
            NativeProfilerRootAggregate root = _roots[index];
            if (root.Kind != kind || !string.Equals(root.Name, name, StringComparison.Ordinal))
                continue;
            milliseconds = root.TotalMilliseconds;
            return true;
        }
        return false;
    }

    internal bool TryGetRootAtRow(int rowIndex, out NativeProfilerRootAggregate? root)
    {
        int index = rowIndex - 4;
        root = index >= 0 && index < _rootOrder.Count ? _roots[_rootOrder[index]] : null;
        return root is not null;
    }

    internal bool SelectRoot(string name, ProfilerScopeKind kind)
    {
        if (string.Equals(_selectedRootName, name, StringComparison.Ordinal) &&
            _selectedRootKind == kind)
            return false;
        _selectedRootName = name;
        _selectedRootKind = kind;
        BuildRootChildOrder();
        if (_view == ENativeProfilerView.CpuRoots)
            PublishView();
        return true;
    }

    public void Request(Frame? frame, bool isWorst = false)
    {
        if (_view == ENativeProfilerView.CpuRoots)
        {
            _roots.Request(frame);
            return;
        }
        if (_view != ENativeProfilerView.CpuTimings)
            return;
        _requested = frame;
        _requestedIsWorst = isWorst;
        _hasRequest = true;
    }

    public bool SelectView(ENativeProfilerView view)
    {
        if (_view == view)
            return false;
        CancelPending();
        _view = view;
        _componentFrame = null;
        _diagnostics.Clear();
        PublishView();
        return true;
    }

    public bool SetRootParameters(float minimum, float maximum, bool sortByTime)
    {
        if (_rootMinimum == minimum && _rootMaximum == maximum && _sortRootsByTime == sortByTime)
            return false;
        _rootMinimum = minimum;
        _rootMaximum = maximum;
        _sortRootsByTime = sortByTime;
        if (_view == ENativeProfilerView.CpuRoots)
        {
            BuildRootOrder();
            PublishView();
        }
        return true;
    }

    public bool SetComponentFrame(Frame? frame, bool enabled)
    {
        if (_view != ENativeProfilerView.ComponentTimings ||
            ReferenceEquals(_componentFrame, frame) && _componentTimingEnabled == enabled)
            return false;
        _componentFrame = frame;
        _componentTimingEnabled = enabled;
        PublishView();
        return true;
    }

    public bool SetDiagnostics(ReadOnlySpan<NativeProfilerDisplayRow> rows)
    {
        if (_view is ENativeProfilerView.CpuTimings or ENativeProfilerView.CpuRoots or
            ENativeProfilerView.ComponentTimings)
            return false;
        bool sameSettings = _view == ENativeProfilerView.Settings && _diagnostics.Count == rows.Length;
        if (_diagnostics.Count == rows.Length)
        {
            bool same = true;
            for (int index = 0; index < rows.Length; index++)
            {
                if (sameSettings && (_diagnostics[index].SettingIndex != rows[index].SettingIndex ||
                    _diagnostics[index].OwnerRevision != rows[index].OwnerRevision))
                    sameSettings = false;
                if (_diagnostics[index] != rows[index])
                {
                    same = false;
                }
            }
            if (same)
                return false;
        }
        _diagnostics.Clear();
        for (int index = 0; index < rows.Length; index++)
            _diagnostics.Add(rows[index]);
        if (sameSettings)
        {
            _formatted.Clear();
            _formatOrder.Clear();
            Revision++;
        }
        else
            PublishView();
        return true;
    }

    public bool Advance(int maximumSteps)
    {
        LastIndexWork = 0;
        if (_view == ENativeProfilerView.CpuRoots)
        {
            bool changed = _roots.Pump(maximumSteps);
            LastIndexWork = _roots.LastWorkCount;
            if (changed)
            {
                BuildRootOrder();
                PublishView();
            }
            return changed;
        }
        int rootRetirementWork = 0;
        if (_roots.IsRetiring)
        {
            _roots.Pump(maximumSteps);
            rootRetirementWork = _roots.LastWorkCount;
            if (rootRetirementWork == maximumSteps)
            {
                LastIndexWork = rootRetirementWork;
                return false;
            }
        }
        int projectionBudget = maximumSteps - rootRetirementWork;
        if (_view != ENativeProfilerView.CpuTimings)
        {
            _projection.Pump(projectionBudget);
            LastIndexWork = rootRetirementWork + _projection.LastWorkCount;
            return false;
        }
        if (!_projection.IsBuilding)
        {
            if (!_hasRequest)
            {
                _projection.Pump(projectionBudget);
                LastIndexWork = rootRetirementWork + _projection.LastWorkCount;
                return false;
            }
            _hasRequest = false;
            if (ReferenceEquals(_requested, _published) && _requestedIsWorst == _publishedIsWorst)
            {
                _requested = null;
                _projection.Pump(projectionBudget);
                LastIndexWork = rootRetirementWork + _projection.LastWorkCount;
                return false;
            }
            _building = _requested;
            _buildingIsWorst = _requestedIsWorst;
            _requested = null;
            _projection.Begin(_building?.Threads ?? Array.Empty<Thread>());
        }
        bool completed = _projection.Pump(projectionBudget);
        LastIndexWork = rootRetirementWork + _projection.LastWorkCount;
        if (!completed)
            return false;
        _published = _building;
        _publishedIsWorst = _buildingIsWorst;
        _building = null;
        PublishView();
        return true;
    }

    private void BuildRootOrder()
    {
        _rootOrder.Clear();
        for (int index = 0; index < _roots.Count; index++)
        {
            NativeProfilerRootAggregate root = _roots[index];
            if (root.TotalMilliseconds >= _rootMinimum &&
                (_rootMaximum <= 0f || root.TotalMilliseconds <= _rootMaximum))
                _rootOrder.Add(index);
        }
        _rootOrder.Sort(_sortRootsByTime ? _rootTimeComparison : _rootNameComparison);
        BuildRootChildOrder();
    }

    private void BuildRootChildOrder()
    {
        _rootChildOrder.Clear();
        _selectedRootIndex = -1;
        for (int index = 0; index < _roots.Count; index++)
        {
            NativeProfilerRootAggregate root = _roots[index];
            if (_selectedRootKind == root.Kind &&
                string.Equals(_selectedRootName, root.Name, StringComparison.Ordinal))
            {
                _selectedRootIndex = index;
                break;
            }
        }
        if (_selectedRootIndex < 0)
            return;
        for (int index = 0; index < _roots.ChildCount; index++)
            if (_roots.ChildAt(index).RootIndex == _selectedRootIndex)
                _rootChildOrder.Add(index);
    }

    private int CompareRootTime(int left, int right)
    {
        int result = _roots[right].TotalMilliseconds.CompareTo(_roots[left].TotalMilliseconds);
        return result != 0 ? result : CompareRootName(left, right);
    }

    private int CompareRootName(int left, int right)
    {
        int result = string.Compare(_roots[left].Name, _roots[right].Name, StringComparison.Ordinal);
        return result != 0 ? result : _roots[left].Kind.CompareTo(_roots[right].Kind);
    }

    private void PublishView()
    {
        if ((ulong)Count > ulong.MaxValue - _nextId)
            throw new InvalidOperationException("Profiler row ID space is exhausted.");
        _firstId = _nextId;
        _nextId += (ulong)Count;
        _formatted.Clear();
        _formatOrder.Clear();
        Revision++;
    }

    public ulong GetItemId(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        return _firstId + (ulong)index;
    }

    public bool TryGetIndexOfItemId(ulong id, out int index)
    {
        if (id >= _firstId && id - _firstId < (ulong)Count)
        {
            index = (int)(id - _firstId);
            return true;
        }
        index = -1;
        return false;
    }

    public NativeProfilerDisplayRow GetItem(int index)
    {
        _ = GetItemId(index);
        if (_formatted.TryGetValue(index, out NativeProfilerDisplayRow row))
            return row;
        row = Format(index);
        if (_formatted.Count == MaximumCachedRows)
            _formatted.Remove(_formatOrder.Dequeue());
        _formatted.Add(index, row);
        _formatOrder.Enqueue(index);
        return row;
    }

    private NativeProfilerDisplayRow Format(int index)
    {
        if (_view == ENativeProfilerView.CpuRoots)
            return FormatRoot(index);
        if (_view == ENativeProfilerView.ComponentTimings)
            return FormatComponent(index);
        if (_view != ENativeProfilerView.CpuTimings)
            return _diagnostics.Count == 0
                ? new("Waiting for diagnostic samples...", string.Empty, string.Empty)
                : _diagnostics[index];
        Frame? frame = _published;
        if (frame is null)
            return new("No profiler sample is available.", string.Empty, string.Empty);
        if (index == 0)
            return new($"Frame {frame.UpdateFrameId} / Render {frame.RenderFrameId}", Milliseconds(frame.FrameTime),
                _publishedIsWorst ? "Worst CPU hierarchy in the last completed 0.5 s window." : string.Empty);
        NativeProfilerRowRef<Node> item = _projection[index - 1];
        if (item.Node is not { } node)
        {
            Thread thread = frame.Threads[item.GroupIndex];
            return new($"Thread {thread.ThreadId}", Milliseconds(thread.TotalTimeMs), string.Empty);
        }
        return new(Indents[Math.Min(item.Depth, 8)] + node.Name, Milliseconds(node.ElapsedMs), Milliseconds(node.SelfMs));
    }

    private NativeProfilerDisplayRow FormatRoot(int index)
    {
        if (index == 0)
            return new("Root methods", _rootOrder.Count.ToString(CultureInfo.InvariantCulture),
                $"{_roots.PublishedFrame?.Threads.Count ?? 0} threads in the last complete CPU snapshot");
        if (index == 1)
            return new("Sort roots", _sortRootsByTime ? "Total time" : "Name", "Activate to change order.");
        if (index == 2)
            return new("Minimum root time", _rootMinimum.ToString("F2", CultureInfo.InvariantCulture),
                "ms; edit value", (int)ENativeProfilerSetting.RootMinimum);
        if (index == 3)
            return new("Maximum root time", _rootMaximum.ToString("F2", CultureInfo.InvariantCulture),
                "ms; 0 means no maximum", (int)ENativeProfilerSetting.RootMaximum);
        int rootIndex = index - 4;
        if (rootIndex < _rootOrder.Count)
        {
            NativeProfilerRootAggregate root = _roots[_rootOrder[rootIndex]];
            return new($"{root.Name} [{root.Kind}]", root.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                $"self {root.SelfMilliseconds:F3} ms | avg {root.TotalMilliseconds / root.Calls:F3} ms | peak {root.PeakMilliseconds:F3} ms | {root.Calls} calls | activate to inspect");
        }
        rootIndex -= _rootOrder.Count;
        if (_roots.SkippedRootCalls != 0)
        {
            if (rootIndex-- == 0)
                return new("Root calls beyond capacity", _roots.SkippedRootCalls.ToString(CultureInfo.InvariantCulture),
                    "The index retains at most 4096 distinct root methods.");
        }
        if (_selectedRootIndex < 0)
            return new("No root selected", string.Empty, string.Empty);
        if (rootIndex-- == 0)
            return new($"Children of {_selectedRootName}",
                _rootChildOrder.Count.ToString(CultureInfo.InvariantCulture),
                "Aggregated by parent path and scope kind from the last complete CPU snapshot.");
        if (rootIndex >= _rootChildOrder.Count)
            return new("Child paths beyond capacity", _roots.SkippedChildPaths.ToString(CultureInfo.InvariantCulture),
                "The index retains at most 4096 distinct child paths.");
        NativeProfilerRootChildAggregate child = _roots.ChildAt(_rootChildOrder[rootIndex]);
        return new(Indents[Math.Min(child.Depth, 8)] + child.Name,
            child.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
            $"self {child.SelfMilliseconds:F3} ms | avg {child.TotalMilliseconds / child.Calls:F3} ms | peak {child.PeakMilliseconds:F3} ms | {child.Calls} calls");
    }

    private static string Milliseconds(float value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private NativeProfilerDisplayRow FormatComponent(int index)
    {
        if (!_componentTimingEnabled)
            return new("Component timing is disabled. Use the capture control to enable it.", string.Empty, string.Empty);
        var components = _componentFrame?.ComponentTimings?.Components;
        if (components is null || components.Count == 0)
            return new("Waiting for component timing samples...", string.Empty, string.Empty);
        if (index == 0)
            return new($"Components with update work: {components.Count}", string.Empty,
                "Variable update tick delegates; rows are sorted by total time.");
        var item = components[index - 1];
        float averageUs = item.CallCount > 0 ? item.ElapsedMs * 1000f / item.CallCount : 0f;
        return new(item.ComponentName, item.ElapsedMs.ToString("F3", CultureInfo.InvariantCulture),
            $"{item.SceneNodeName} | {item.ComponentType} | {item.CallCount} calls | {averageUs.ToString("F1", CultureInfo.InvariantCulture)} us avg | groups {item.TickGroupMask}");
    }

    public void CancelPending()
    {
        _projection.Cancel();
        _roots.CancelPending();
        _building = null;
        _requested = null;
        _hasRequest = false;
    }

    public void Clear()
    {
        CancelPending();
        _projection.Clear();
        _roots.Clear();
        _rootOrder.Clear();
        _rootChildOrder.Clear();
        _selectedRootIndex = -1;
        _selectedRootName = null;
        _published = null;
        _publishedIsWorst = false;
        _componentFrame = null;
        _diagnostics.Clear();
        _formatted.Clear();
        _formatOrder.Clear();
        _firstId = _nextId++;
        Revision++;
    }
}
