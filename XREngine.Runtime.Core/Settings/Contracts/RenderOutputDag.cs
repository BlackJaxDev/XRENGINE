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
    private readonly ERenderOutputPriority[] _priorities;
    private readonly double[] _deadlinesMilliseconds;
    private readonly bool[] _xrCriticalPath;
    private readonly int[] _criticalPathDepth;
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
        _priorities = new ERenderOutputPriority[nodeCapacity];
        _deadlinesMilliseconds = new double[nodeCapacity];
        _xrCriticalPath = new bool[nodeCapacity];
        _criticalPathDepth = new int[nodeCapacity];
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
        Array.Clear(_deadlinesMilliseconds);
        Array.Clear(_xrCriticalPath);
        Array.Clear(_criticalPathDepth);
        Array.Fill(_priorities, ERenderOutputPriority.Diagnostic);
        for (int slot = 0; slot < _slotCount; slot++)
        {
            RenderOutputDagNodeStatus previous = _status[slot];
            uint age = previous.HasCompletedResult && previous.ContentAgeFrames != uint.MaxValue
                ? previous.ContentAgeFrames + 1u
                : previous.ContentAgeFrames;
            _status[slot] = previous with
            {
                State = ERenderOutputNodeState.Pending,
                Progress = 0.0f,
                ContentAgeFrames = age,
                AuthorizedReuse = false,
                Disposition = ERenderOutputWorkDisposition.FreshRender,
                PolicyReason = ERenderOutputPolicyReason.None,
            };
        }
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
            slot = FindReusableSlot(descriptor.StableOutputKey);
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
            Disposition = ERenderOutputWorkDisposition.FreshRender,
            PolicyReason = ERenderOutputPolicyReason.None,
            ConsecutiveDeferrals = state == ERenderOutputNodeState.Complete ? 0u : previous.ConsecutiveDeferrals,
        };
    }

    public void SetSkipped(
        int nodeIndex,
        ERenderOutputPolicyReason reason = ERenderOutputPolicyReason.OutputDisabled)
    {
        ValidateActiveNode(nodeIndex);
        _status[nodeIndex] = _status[nodeIndex] with
        {
            State = ERenderOutputNodeState.Skipped,
            AuthorizedReuse = false,
            Disposition = ERenderOutputWorkDisposition.Skipped,
            PolicyReason = reason,
        };
    }

    public void SetDeferred(int nodeIndex, ERenderOutputPolicyReason reason)
    {
        ValidateActiveNode(nodeIndex);
        RenderOutputDagNodeStatus previous = _status[nodeIndex];
        _status[nodeIndex] = previous with
        {
            State = ERenderOutputNodeState.Deferred,
            AuthorizedReuse = false,
            Disposition = ERenderOutputWorkDisposition.Deferred,
            PolicyReason = reason,
            ConsecutiveDeferrals = previous.ConsecutiveDeferrals == uint.MaxValue
                ? uint.MaxValue
                : previous.ConsecutiveDeferrals + 1u,
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
            Disposition = ERenderOutputWorkDisposition.ReusedStale,
            PolicyReason = ERenderOutputPolicyReason.HeldLastImage,
            ConsecutiveDeferrals = status.ConsecutiveDeferrals == uint.MaxValue
                ? uint.MaxValue
                : status.ConsecutiveDeferrals + 1u,
        };
        return true;
    }

    /// <summary>
    /// Applies one terminal output policy to the terminal and all prerequisite
    /// nodes. Acquired XR work therefore promotes uploads/publication on its
    /// reverse dependency path without changing stable graph identity.
    /// </summary>
    public void ApplyScheduleToPrerequisites(
        int terminalNode,
        ERenderOutputPriority priority,
        double deadlineMilliseconds,
        bool xrImagesAcquired)
    {
        ValidateActiveNode(terminalNode);
        ApplySchedule(terminalNode, priority, deadlineMilliseconds, xrImagesAcquired);

        bool changed;
        do
        {
            changed = false;
            for (int edgeIndex = 0; edgeIndex < _edgeCount; edgeIndex++)
            {
                Edge edge = _edges[edgeIndex];
                if (!_active[edge.Prerequisite] || !_active[edge.Dependent])
                    continue;
                if (!ScheduleDominates(edge.Dependent, edge.Prerequisite))
                    continue;

                ApplySchedule(
                    edge.Prerequisite,
                    _priorities[edge.Dependent],
                    _deadlinesMilliseconds[edge.Dependent],
                    _xrCriticalPath[edge.Dependent]);
                changed = true;
            }
        }
        while (changed);
    }

    /// <summary>
    /// Compiles a stable topological order that reserves acquired OpenXR paths,
    /// then orders by output priority, deadline, reverse critical-path depth,
    /// and stable node identity.
    /// </summary>
    public bool TryCompileDeadlineOrder(
        Span<int> destination,
        Span<int> indegreeScratch,
        out int count,
        out ERenderOutputDagCompilationFailure failure)
    {
        ComputeCriticalPathDepth();
        return TryCompileOrder(destination, indegreeScratch, deadlineAware: true, out count, out failure);
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
        => TryCompileOrder(destination, indegreeScratch, deadlineAware: false, out count, out failure);

    private bool TryCompileOrder(
        Span<int> destination,
        Span<int> indegreeScratch,
        bool deadlineAware,
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
                if (selected < 0 || IsPreferredReadyNode(slot, selected, deadlineAware))
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

    private bool IsPreferredReadyNode(int candidate, int selected, bool deadlineAware)
    {
        if (deadlineAware)
        {
            int result = _xrCriticalPath[selected].CompareTo(_xrCriticalPath[candidate]);
            if (result != 0)
                return result < 0;
            result = _priorities[candidate].CompareTo(_priorities[selected]);
            if (result != 0)
                return result < 0;
            result = CompareDeadline(
                _deadlinesMilliseconds[candidate],
                _deadlinesMilliseconds[selected]);
            if (result != 0)
                return result < 0;
            result = _criticalPathDepth[selected].CompareTo(_criticalPathDepth[candidate]);
            if (result != 0)
                return result < 0;
        }

        return _nodes[candidate].StableNodeKey < _nodes[selected].StableNodeKey ||
               (_nodes[candidate].StableNodeKey == _nodes[selected].StableNodeKey && candidate < selected);
    }

    private void ApplySchedule(
        int nodeIndex,
        ERenderOutputPriority priority,
        double deadlineMilliseconds,
        bool xrImagesAcquired)
    {
        if (priority < _priorities[nodeIndex])
            _priorities[nodeIndex] = priority;
        if (deadlineMilliseconds > 0.0 &&
            (_deadlinesMilliseconds[nodeIndex] <= 0.0 ||
             deadlineMilliseconds < _deadlinesMilliseconds[nodeIndex]))
        {
            _deadlinesMilliseconds[nodeIndex] = deadlineMilliseconds;
        }
        _xrCriticalPath[nodeIndex] |= xrImagesAcquired;
    }

    private bool ScheduleDominates(int source, int destination)
        => _xrCriticalPath[source] && !_xrCriticalPath[destination] ||
           _priorities[source] < _priorities[destination] ||
           _deadlinesMilliseconds[source] > 0.0 &&
           (_deadlinesMilliseconds[destination] <= 0.0 ||
            _deadlinesMilliseconds[source] < _deadlinesMilliseconds[destination]);

    private void ComputeCriticalPathDepth()
    {
        Array.Clear(_criticalPathDepth, 0, _slotCount);
        for (int slot = 0; slot < _slotCount; slot++)
            if (_active[slot] && _xrCriticalPath[slot])
                _criticalPathDepth[slot] = 1;

        for (int pass = 0; pass < _activeCount; pass++)
        {
            bool changed = false;
            for (int edgeIndex = 0; edgeIndex < _edgeCount; edgeIndex++)
            {
                Edge edge = _edges[edgeIndex];
                int dependentDepth = _criticalPathDepth[edge.Dependent];
                if (dependentDepth == 0 || _criticalPathDepth[edge.Prerequisite] >= dependentDepth + 1)
                    continue;
                _criticalPathDepth[edge.Prerequisite] = dependentDepth + 1;
                changed = true;
            }
            if (!changed)
                break;
        }
    }

    private static int CompareDeadline(double left, double right)
    {
        if (left <= 0.0)
            return right <= 0.0 ? 0 : 1;
        if (right <= 0.0)
            return -1;
        return left.CompareTo(right);
    }

    private int FindNode(ulong stableNodeKey)
    {
        for (int i = 0; i < _slotCount; i++)
            if (_nodes[i].StableNodeKey == stableNodeKey)
                return i;
        return -1;
    }

    private int FindReusableSlot(ulong requestedOutputKey)
    {
        // A revised target/resource generation for the same output supersedes
        // its inactive node version. Recycle that version first so repeated
        // resize generations cannot exhaust the persistent DAG slot table.
        for (int i = 0; i < _slotCount; i++)
            if (!_active[i] && _nodes[i].StableOutputKey == requestedOutputKey)
                return i;

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
