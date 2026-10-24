namespace XREngine.Editor;

/// <summary>
/// Builds a snapshot index with bounded owner-thread work. Readers keep the last
/// complete index until the replacement and its unused tail are complete.
/// </summary>
internal sealed class NativeProfilerProjection<TGroup, TNode>(
    Func<TGroup, IReadOnlyList<TNode>> roots,
    Func<TNode, IReadOnlyList<TNode>> children)
    where TNode : class
{
    private List<NativeProfilerRowRef<TNode>> _active = [];
    private List<NativeProfilerRowRef<TNode>> _building = [];
    private readonly List<NativeProfilerTraversalFrame<TNode>> _stack = [];
    private IReadOnlyList<TGroup>? _groups;
    private int _nextGroup;
    private int _currentGroup;
    private int _written;
    private bool _retiring;

    public int Count => _active.Count;
    public bool IsBuilding => _groups is not null;
    public int LastWorkCount { get; private set; }
    public int PendingRowCount => _written;
    public NativeProfilerRowRef<TNode> this[int index] => _active[index];

    public void Begin(IReadOnlyList<TGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (IsBuilding)
            throw new InvalidOperationException("A profiler index is already being built.");
        _groups = groups;
        _nextGroup = 0;
        _currentGroup = -1;
        _written = 0;
    }

    /// <summary>Counts node visits, group visits, stack removal, and tail retirement against the budget.</summary>
    public bool Pump(int maximumSteps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSteps);
        LastWorkCount = 0;
        IReadOnlyList<TGroup>? groups = _groups;
        if (groups is null && !_retiring)
            return false;
        while (LastWorkCount < maximumSteps)
        {
            LastWorkCount++;
            if (_retiring)
            {
                if (_stack.Count != 0)
                    _stack.RemoveAt(_stack.Count - 1);
                else if (_building.Count != 0)
                    _building.RemoveAt(_building.Count - 1);
                else
                    _retiring = false;
                if (!_retiring && groups is null)
                    return false;
                continue;
            }
            if (_stack.Count != 0)
            {
                int top = _stack.Count - 1;
                NativeProfilerTraversalFrame<TNode> frame = _stack[top];
                if (frame.NextIndex >= frame.Nodes.Count)
                {
                    _stack.RemoveAt(top);
                    continue;
                }
                TNode node = frame.Nodes[frame.NextIndex];
                _stack[top] = frame with { NextIndex = frame.NextIndex + 1 };
                Write(new(_currentGroup, node, frame.Depth));
                IReadOnlyList<TNode> nested = children(node);
                if (nested.Count != 0)
                    _stack.Add(new(nested, 0, frame.Depth + 1));
                continue;
            }
            if (_nextGroup < groups!.Count)
            {
                _currentGroup = _nextGroup++;
                Write(new(_currentGroup, null, 0));
                IReadOnlyList<TNode> groupRoots = roots(groups[_currentGroup]);
                if (groupRoots.Count != 0)
                    _stack.Add(new(groupRoots, 0, 1));
                continue;
            }
            if (_building.Count > _written)
            {
                _building.RemoveAt(_building.Count - 1);
                continue;
            }
            (_active, _building) = (_building, _active);
            _groups = null;
            return true;
        }
        return false;
    }

    private void Write(NativeProfilerRowRef<TNode> row)
    {
        if (_written < _building.Count)
            _building[_written] = row;
        else
            _building.Add(row);
        _written++;
    }

    /// <summary>Stops candidate work. Later pumps release its references within their budget.</summary>
    public void Cancel()
    {
        _groups = null;
        _retiring = true;
        _written = 0;
        LastWorkCount = 0;
    }

    /// <summary>Releases snapshot references when the view owner retires.</summary>
    public void Clear()
    {
        Cancel();
        _stack.Clear();
        _building.Clear();
        _retiring = false;
        _active.Clear();
    }
}
