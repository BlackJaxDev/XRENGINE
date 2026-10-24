using Frame = XREngine.Engine.CodeProfiler.ProfilerFrameSnapshot;
using Node = XREngine.Engine.CodeProfiler.ProfilerNodeSnapshot;

namespace XREngine.Editor;

/// <summary>Builds a capped root index with bounded snapshot visits per tick.</summary>
internal sealed class NativeProfilerRootAggregator
{
    private const int MaximumUniqueRoots = 4096;
    private const int MaximumChildPaths = 4096;
    private List<NativeProfilerRootAggregate> _active = [];
    private List<NativeProfilerRootAggregate> _building = [];
    private List<NativeProfilerRootChildAggregate> _activeChildren = [];
    private List<NativeProfilerRootChildAggregate> _buildingChildren = [];
    private readonly Dictionary<(string Name, XREngine.Data.Profiling.ProfilerScopeKind Kind), int> _index = [];
    private readonly List<(string Name, XREngine.Data.Profiling.ProfilerScopeKind Kind)> _keys = [];
    private readonly Dictionary<(int Root, int Parent, string Name,
        XREngine.Data.Profiling.ProfilerScopeKind Kind), int> _childIndex = [];
    private readonly List<(int Root, int Parent, string Name,
        XREngine.Data.Profiling.ProfilerScopeKind Kind)> _childKeys = [];
    private List<(IReadOnlyList<Node> Nodes, int NextIndex, int Depth, int ParentIndex)> _stack = [];
    private Frame? _buildingFrame;
    private Frame? _pendingFrame;
    private Frame? _publishedFrame;
    private int _threadIndex;
    private int _rootIndex;
    private int _skippedRootCalls;
    private int _skippedChildPaths;
    private Node? _currentRoot;
    private int _currentAggregateIndex;
    private float _downstreamWaitMilliseconds;
    private int _activeCount;
    private int _buildingCount;
    private int _activeChildCount;
    private int _buildingChildCount;
    private int _keyCount;
    private int _childKeyCount;
    private bool _retiring;
    private int _retireIndex;
    private int _retireLimit;

    public Frame? PublishedFrame => _publishedFrame;
    public int Count => _activeCount;
    public int SkippedRootCalls { get; private set; }
    public int SkippedChildPaths { get; private set; }
    public int ChildCount => _activeChildCount;
    public NativeProfilerRootChildAggregate ChildAt(int index) => _activeChildren[index];
    public NativeProfilerRootAggregate this[int index] => _active[index];
    public int LastWorkCount { get; private set; }
    internal bool IsRetiring => _retiring;
    internal int CreatedAggregateCount { get; private set; }

    public void Request(Frame? frame)
    {
        if (!ReferenceEquals(frame, _publishedFrame) && !ReferenceEquals(frame, _buildingFrame))
            _pendingFrame = frame;
    }

    public bool Pump(int maximumSteps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSteps);
        LastWorkCount = 0;
        while (_retiring && LastWorkCount < maximumSteps)
        {
            if (_stack.Count != 0)
            {
                _stack.RemoveAt(_stack.Count - 1);
                LastWorkCount++;
                continue;
            }
            if (_retireIndex < _retireLimit)
            {
                int index = _retireIndex++;
                if (index < _keyCount)
                {
                    _index.Remove(_keys[index]);
                    _keys[index] = default;
                }
                if (index < _building.Count)
                    _building[index].Reset(string.Empty, default);
                if (index < _childKeyCount)
                {
                    _childIndex.Remove(_childKeys[index]);
                    _childKeys[index] = default;
                }
                if (index < _buildingChildren.Count)
                    _buildingChildren[index].Reset(string.Empty, default, -1, -1, 0);
                LastWorkCount++;
                continue;
            }
            _retiring = false;
            _keyCount = 0;
            _childKeyCount = 0;
        }
        if (_retiring || LastWorkCount == maximumSteps)
            return false;
        if (_buildingFrame is null && _pendingFrame is { } next)
        {
            _buildingFrame = next;
            _pendingFrame = null;
            _buildingCount = 0;
            _buildingChildCount = 0;
            _threadIndex = 0;
            _rootIndex = 0;
            _skippedRootCalls = 0;
            _skippedChildPaths = 0;
            _currentRoot = null;
        }
        Frame? frame = _buildingFrame;
        if (frame is null)
            return false;
        while (LastWorkCount < maximumSteps &&
            (_threadIndex < frame.Threads.Count || _currentRoot is not null || _stack.Count != 0))
        {
            if (_stack.Count != 0)
            {
                int top = _stack.Count - 1;
                var branch = _stack[top];
                if (branch.NextIndex == branch.Nodes.Count)
                {
                    _stack.RemoveAt(top);
                    LastWorkCount++;
                    continue;
                }
                Node child = branch.Nodes[branch.NextIndex];
                _stack[top] = (branch.Nodes, branch.NextIndex + 1,
                    branch.Depth, branch.ParentIndex);
                if (string.Equals(child.Name, "EngineTimer.CollectVisibleThread.WaitForRender", StringComparison.Ordinal))
                    _downstreamWaitMilliseconds += child.ElapsedMs;
                int parentIndex = AddChild(child, branch.ParentIndex, branch.Depth);
                if (child.Children.Count != 0)
                    _stack.Add((child.Children, 0, branch.Depth + 1, parentIndex));
                LastWorkCount++;
                continue;
            }
            if (_currentRoot is not null)
            {
                if (_currentAggregateIndex >= 0)
                    _building[_currentAggregateIndex].Add(
                        Math.Max(0f, _currentRoot.ElapsedMs - _downstreamWaitMilliseconds),
                        string.Equals(_currentRoot.Name, "EngineTimer.CollectVisibleThread.WaitForRender", StringComparison.Ordinal)
                            ? 0f : _currentRoot.SelfMs);
                _currentRoot = null;
                LastWorkCount++;
                continue;
            }
            var roots = frame.Threads[_threadIndex].RootNodes;
            if (_rootIndex >= roots.Count)
            {
                _threadIndex++;
                _rootIndex = 0;
                LastWorkCount++;
                continue;
            }
            Node root = roots[_rootIndex++];
            LastWorkCount++;
            _currentRoot = root;
            _downstreamWaitMilliseconds = string.Equals(root.Name,
                "EngineTimer.CollectVisibleThread.WaitForRender", StringComparison.Ordinal)
                    ? root.ElapsedMs : 0f;
            if (root.Children.Count != 0)
                _stack.Add((root.Children, 0, 1, -1));
            var key = (root.Name, root.ScopeKind);
            if (!_index.TryGetValue(key, out int index))
            {
                if (_buildingCount >= MaximumUniqueRoots)
                {
                    _skippedRootCalls++;
                    _currentAggregateIndex = -1;
                    continue;
                }
                index = _buildingCount++;
                _index.Add(key, index);
                if (_keyCount == _keys.Count)
                    _keys.Add(key);
                else
                    _keys[_keyCount] = key;
                _keyCount++;
                if (index == _building.Count)
                {
                    _building.Add(new(root.Name, root.ScopeKind));
                    CreatedAggregateCount++;
                }
                else
                    _building[index].Reset(root.Name, root.ScopeKind);
            }
            _currentAggregateIndex = index;
        }
        if (_threadIndex < frame.Threads.Count || _currentRoot is not null || _stack.Count != 0)
            return false;
        (_active, _building) = (_building, _active);
        (_activeChildren, _buildingChildren) = (_buildingChildren, _activeChildren);
        _activeCount = _buildingCount;
        _activeChildCount = _buildingChildCount;
        _buildingCount = 0;
        _buildingChildCount = 0;
        _publishedFrame = frame;
        _buildingFrame = null;
        if (ReferenceEquals(_pendingFrame, frame))
            _pendingFrame = null;
        _currentRoot = null;
        SkippedRootCalls = _skippedRootCalls;
        SkippedChildPaths = _skippedChildPaths;
        BeginRetirement();
        return true;
    }

    private int AddChild(Node child, int parentIndex, int depth)
    {
        if (_currentAggregateIndex < 0 || parentIndex == -2)
            return -2;
        var key = (_currentAggregateIndex, parentIndex, child.Name, child.ScopeKind);
        if (!_childIndex.TryGetValue(key, out int index))
        {
            if (_buildingChildCount >= MaximumChildPaths)
            {
                _skippedChildPaths++;
                return -2;
            }
            index = _buildingChildCount++;
            _childIndex.Add(key, index);
            if (_childKeyCount == _childKeys.Count)
                _childKeys.Add(key);
            else
                _childKeys[_childKeyCount] = key;
            _childKeyCount++;
            if (index == _buildingChildren.Count)
                _buildingChildren.Add(new(child.Name, child.ScopeKind,
                    _currentAggregateIndex, parentIndex, depth));
            else
                _buildingChildren[index].Reset(child.Name, child.ScopeKind,
                    _currentAggregateIndex, parentIndex, depth);
        }
        _buildingChildren[index].Add(child.ElapsedMs, child.SelfMs);
        return index;
    }

    private void BeginRetirement()
    {
        _retiring = true;
        _retireIndex = 0;
        _retireLimit = Math.Max(Math.Max(_keyCount, _building.Count),
            Math.Max(_childKeyCount, _buildingChildren.Count));
    }

    public void Clear()
    {
        _active.Clear();
        _building.Clear();
        _activeChildren.Clear();
        _buildingChildren.Clear();
        _index.Clear();
        _keys.Clear();
        _childIndex.Clear();
        _childKeys.Clear();
        _activeCount = 0;
        _buildingCount = 0;
        _activeChildCount = 0;
        _buildingChildCount = 0;
        _keyCount = 0;
        _childKeyCount = 0;
        _retiring = false;
        CreatedAggregateCount = 0;
        _buildingFrame = null;
        _pendingFrame = null;
        _currentRoot = null;
        _stack.Clear();
        _publishedFrame = null;
        SkippedRootCalls = 0;
        SkippedChildPaths = 0;
    }

    public void CancelPending()
    {
        bool wasBuilding = _buildingFrame is not null;
        _buildingFrame = null;
        _pendingFrame = null;
        _currentRoot = null;
        // Drop the canceled node chain at once. Normal snapshot updates reuse the stack.
        if (_stack.Count != 0)
            _stack = [];
        if (wasBuilding)
            BeginRetirement();
    }
}
