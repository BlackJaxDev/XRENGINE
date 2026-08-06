namespace XREngine;

/// <summary>
/// Preallocated output DAG shared by primary, mirror, capture, and probe work.
/// Stable node slots preserve cached content age and resumable progress.
/// </summary>
public sealed class RenderOutputDag
{
    private readonly RenderOutputDagNodeDescriptor[] _nodes;
    private readonly RenderOutputDagNodeStatus[] _status;
    private readonly bool[] _active;
    private readonly ulong[] _reservedOutputKeys;
    private readonly Edge[] _edges;
    private int _slotCount;
    private int _activeCount;
    private int _edgeCount;
    private uint _frameIndex;
    private ERenderOutputDagCompilationFailure _buildFailure;

    public RenderOutputDag(int nodeCapacity, int edgeCapacity)
    {
        if (nodeCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(nodeCapacity));
        if (edgeCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(edgeCapacity));
        _nodes = new RenderOutputDagNodeDescriptor[nodeCapacity];
        _status = new RenderOutputDagNodeStatus[nodeCapacity];
        _active = new bool[nodeCapacity];
        _reservedOutputKeys = new ulong[nodeCapacity];
        _edges = new Edge[edgeCapacity];
    }

    public int NodeCount => _activeCount;
    public int EdgeCount => _edgeCount;
    /// <summary>Number of persistent node slots required by compilation scratch storage.</summary>
    public int SlotCount => _slotCount;

    public void BeginFrame(uint frameIndex)
    {
        _frameIndex = frameIndex;
        // Node keys include output/resource revisions. Clear only this frame's
        // activity/edges: stable keys retain their cache and resumable status,
        // while inactive slots are available for a revised resource key.
        Array.Clear(_active);
        _activeCount = 0;
        _edgeCount = 0;
        _reservedOutputKeyCount = 0;
        _buildFailure = ERenderOutputDagCompilationFailure.None;
    }

    private int _reservedOutputKeyCount;

    /// <summary>
    /// Reserves every current output before lowering begins. This prevents a
    /// newly introduced output from recycling a stable cache slot belonging to
    /// a later output in the same frame.
    /// </summary>
    public bool ReserveOutputKey(ulong stableOutputKey)
    {
        if (stableOutputKey == 0UL)
            return false;
        for (int i = 0; i < _reservedOutputKeyCount; i++)
            if (_reservedOutputKeys[i] == stableOutputKey)
                return true;
        if (_reservedOutputKeyCount >= _reservedOutputKeys.Length)
            return false;
        _reservedOutputKeys[_reservedOutputKeyCount++] = stableOutputKey;
        return true;
    }

    public int AddNode(in RenderOutputDagNodeDescriptor descriptor)
    {
        if (descriptor.StableNodeKey == 0UL)
            throw new ArgumentOutOfRangeException(nameof(descriptor));
        int slot = FindNode(descriptor.StableNodeKey);
        if (slot < 0)
        {
            slot = FindReusableSlot();
            if (slot < 0)
            {
                _buildFailure = ERenderOutputDagCompilationFailure.DestinationCapacity;
                return -1;
            }

            _status[slot] = default;
            if (slot == _slotCount)
                _slotCount++;
        }

        _nodes[slot] = descriptor;
        if (!_active[slot])
        {
            _active[slot] = true;
            _activeCount++;
        }
        return slot;
    }

    public bool AddDependency(int prerequisiteNode, int dependentNode)
    {
        ValidateActiveNode(prerequisiteNode);
        ValidateActiveNode(dependentNode);
        if (prerequisiteNode == dependentNode)
        {
            _buildFailure = ERenderOutputDagCompilationFailure.Cycle;
            return false;
        }
        if (_edgeCount >= _edges.Length)
        {
            _buildFailure = ERenderOutputDagCompilationFailure.DestinationCapacity;
            return false;
        }
        _edges[_edgeCount++] = new(prerequisiteNode, dependentNode);
        return true;
    }

    public ref readonly RenderOutputDagNodeDescriptor GetNode(int nodeIndex)
    {
        ValidateActiveNode(nodeIndex);
        return ref _nodes[nodeIndex];
    }

    public RenderOutputDagNodeStatus GetStatus(int nodeIndex)
    {
        ValidateActiveNode(nodeIndex);
        return _status[nodeIndex];
    }

    public bool TryGetNodeIndex(ulong stableNodeKey, out int nodeIndex)
    {
        nodeIndex = FindNode(stableNodeKey);
        return nodeIndex >= 0 && _active[nodeIndex];
    }

    public bool DependenciesComplete(int nodeIndex)
    {
        ValidateActiveNode(nodeIndex);
        for (int i = 0; i < _edgeCount; i++)
        {
            if (_edges[i].Dependent != nodeIndex)
                continue;
            ERenderOutputNodeState state = _status[_edges[i].Prerequisite].State;
            if (state is not (ERenderOutputNodeState.Complete or ERenderOutputNodeState.Reused))
                return false;
        }
        return true;
    }

    public void SetProgress(int nodeIndex, float progress)
    {
        ValidateActiveNode(nodeIndex);
        progress = Math.Clamp(progress, 0.0f, 1.0f);
        ERenderOutputNodeState state = progress >= 1.0f
            ? ERenderOutputNodeState.Complete
            : ERenderOutputNodeState.Running;
        RenderOutputDagNodeStatus previous = _status[nodeIndex];
        _status[nodeIndex] = previous with
        {
            State = state,
            Progress = progress,
            ContentAgeFrames = state == ERenderOutputNodeState.Complete ? 0u : previous.ContentAgeFrames,
            LastCompletedFrame = state == ERenderOutputNodeState.Complete ? _frameIndex : previous.LastCompletedFrame,
            HasCompletedResult = state == ERenderOutputNodeState.Complete || previous.HasCompletedResult,
        };
    }

    public void SetSkipped(int nodeIndex)
    {
        ValidateActiveNode(nodeIndex);
        _status[nodeIndex] = _status[nodeIndex] with
        {
            State = ERenderOutputNodeState.Skipped,
            AuthorizedReuse = false,
        };
    }

    public bool TryReuse(int nodeIndex)
    {
        ValidateActiveNode(nodeIndex);
        ref readonly RenderOutputDagNodeDescriptor node = ref _nodes[nodeIndex];
        RenderOutputDagNodeStatus status = _status[nodeIndex];
        if (!node.Cacheable || !status.HasCompletedResult ||
            status.ContentAgeFrames > node.MaximumContentAgeFrames)
            return false;
        _status[nodeIndex] = status with
        {
            State = ERenderOutputNodeState.Reused,
            AuthorizedReuse = true,
        };
        return true;
    }

    /// <summary>
    /// Copies a stable topological order for the active frame graph. Nodes with
    /// no dependency relationship are ordered by stable node key, then slot, so
    /// equivalent output sets always lower to the same execution sequence.
    /// </summary>
    public bool TryCompileDeterministicOrder(
        Span<int> destination,
        Span<int> indegreeScratch,
        out int count,
        out ERenderOutputDagCompilationFailure failure)
    {
        count = 0;
        failure = _buildFailure;
        if (failure != ERenderOutputDagCompilationFailure.None)
            return false;
        if (destination.Length < _activeCount || indegreeScratch.Length < _slotCount)
        {
            failure = ERenderOutputDagCompilationFailure.DestinationCapacity;
            return false;
        }

        for (int slot = 0; slot < _slotCount; slot++)
            indegreeScratch[slot] = _active[slot] ? 0 : -1;

        for (int edgeIndex = 0; edgeIndex < _edgeCount; edgeIndex++)
        {
            Edge edge = _edges[edgeIndex];
            if ((uint)edge.Prerequisite >= (uint)_slotCount ||
                (uint)edge.Dependent >= (uint)_slotCount ||
                !_active[edge.Prerequisite] ||
                !_active[edge.Dependent])
            {
                failure = ERenderOutputDagCompilationFailure.MissingPrerequisite;
                return false;
            }

            indegreeScratch[edge.Dependent]++;
        }

        while (count < _activeCount)
        {
            int selected = -1;
            for (int slot = 0; slot < _slotCount; slot++)
            {
                if (indegreeScratch[slot] != 0)
                    continue;
                if (selected < 0 ||
                    _nodes[slot].StableNodeKey < _nodes[selected].StableNodeKey ||
                    (_nodes[slot].StableNodeKey == _nodes[selected].StableNodeKey && slot < selected))
                {
                    selected = slot;
                }
            }

            if (selected < 0)
            {
                failure = ERenderOutputDagCompilationFailure.Cycle;
                return false;
            }

            destination[count++] = selected;
            indegreeScratch[selected] = -1;
            for (int edgeIndex = 0; edgeIndex < _edgeCount; edgeIndex++)
            {
                Edge edge = _edges[edgeIndex];
                if (edge.Prerequisite == selected)
                    indegreeScratch[edge.Dependent]--;
            }
        }

        return true;
    }

    private int FindNode(ulong stableNodeKey)
    {
        for (int i = 0; i < _slotCount; i++)
            if (_nodes[i].StableNodeKey == stableNodeKey)
                return i;
        return -1;
    }

    private int FindReusableSlot()
    {
        for (int i = 0; i < _slotCount; i++)
            if (!_active[i] && !IsReservedOutputKey(_nodes[i].StableOutputKey))
                return i;

        return _slotCount < _nodes.Length ? _slotCount : -1;
    }

    private bool IsReservedOutputKey(ulong stableOutputKey)
    {
        for (int i = 0; i < _reservedOutputKeyCount; i++)
            if (_reservedOutputKeys[i] == stableOutputKey)
                return true;
        return false;
    }

    private void ValidateActiveNode(int nodeIndex)
    {
        if ((uint)nodeIndex >= (uint)_slotCount || !_active[nodeIndex])
            throw new ArgumentOutOfRangeException(nameof(nodeIndex));
    }

    private readonly record struct Edge(int Prerequisite, int Dependent);
}
