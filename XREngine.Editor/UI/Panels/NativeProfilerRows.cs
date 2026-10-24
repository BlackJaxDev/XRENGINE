using System.Globalization;
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
    private readonly Dictionary<int, NativeProfilerDisplayRow> _formatted = [];
    private readonly Queue<int> _formatOrder = [];
    private Frame? _published;
    private Frame? _building;
    private Frame? _requested;
    private bool _hasRequest;
    private ulong _firstId = 1;
    private ulong _nextId = 2;

    public int Count => _projection.Count + 1;
    public ulong Revision { get; private set; } = 1;
    internal int LastIndexWork { get; private set; }
    internal int FormattedRowCount => _formatted.Count;

    public void Request(Frame? frame)
    {
        _requested = frame;
        _hasRequest = true;
    }

    public bool Advance(int maximumSteps)
    {
        LastIndexWork = 0;
        if (!_projection.IsBuilding)
        {
            if (!_hasRequest)
            {
                _projection.Pump(maximumSteps);
                LastIndexWork = _projection.LastWorkCount;
                return false;
            }
            _hasRequest = false;
            if (ReferenceEquals(_requested, _published))
            {
                _requested = null;
                _projection.Pump(maximumSteps);
                LastIndexWork = _projection.LastWorkCount;
                return false;
            }
            _building = _requested;
            _requested = null;
            _projection.Begin(_building?.Threads ?? Array.Empty<Thread>());
        }
        bool completed = _projection.Pump(maximumSteps);
        LastIndexWork = _projection.LastWorkCount;
        if (!completed)
            return false;
        _published = _building;
        _building = null;
        if ((ulong)Count > ulong.MaxValue - _nextId)
            throw new InvalidOperationException("Profiler row ID space is exhausted.");
        _firstId = _nextId;
        _nextId += (ulong)Count;
        _formatted.Clear();
        _formatOrder.Clear();
        Revision++;
        return true;
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
        Frame? frame = _published;
        if (frame is null)
            return new("No profiler sample is available.", string.Empty, string.Empty);
        if (index == 0)
            return new($"Frame {frame.UpdateFrameId} / Render {frame.RenderFrameId}", Milliseconds(frame.FrameTime), string.Empty);
        NativeProfilerRowRef<Node> item = _projection[index - 1];
        if (item.Node is not { } node)
        {
            Thread thread = frame.Threads[item.GroupIndex];
            return new($"Thread {thread.ThreadId}", Milliseconds(thread.TotalTimeMs), string.Empty);
        }
        return new(Indents[Math.Min(item.Depth, 8)] + node.Name, Milliseconds(node.ElapsedMs), Milliseconds(node.SelfMs));
    }

    private static string Milliseconds(float value) => value.ToString("F2", CultureInfo.InvariantCulture);

    public void CancelPending()
    {
        _projection.Cancel();
        _building = null;
        _requested = null;
        _hasRequest = false;
    }

    public void Clear()
    {
        CancelPending();
        _projection.Clear();
        _published = null;
        _formatted.Clear();
        _formatOrder.Clear();
        _firstId = _nextId++;
        Revision++;
    }
}
