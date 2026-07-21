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
    private readonly Edge[] _edges;
    private int _slotCount;
    private int _activeCount;
    private int _edgeCount;
    private uint _frameIndex;

    public RenderOutputDag(int nodeCapacity, int edgeCapacity)
    {
        if (nodeCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(nodeCapacity));
        if (edgeCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(edgeCapacity));
        _nodes = new RenderOutputDagNodeDescriptor[nodeCapacity];
        _status = new RenderOutputDagNodeStatus[nodeCapacity];
        _active = new bool[nodeCapacity];
        _edges = new Edge[edgeCapacity];
    }

    public int NodeCount => _activeCount;
    public int EdgeCount => _edgeCount;

    public void BeginFrame(uint frameIndex)
    {
        _frameIndex = frameIndex;
        _activeCount = 0;
        _edgeCount = 0;
        for (int i = 0; i < _slotCount; i++)
        {
            _active[i] = false;
            RenderOutputDagNodeStatus status = _status[i];
            uint age = !status.HasCompletedResult
                ? status.ContentAgeFrames
                : frameIndex - status.LastCompletedFrame;
            _status[i] = status with
            {
                State = ERenderOutputNodeState.Pending,
                ContentAgeFrames = age,
                AuthorizedReuse = false,
            };
        }
    }

    public int AddNode(in RenderOutputDagNodeDescriptor descriptor)
    {
        if (descriptor.StableNodeKey == 0UL)
            throw new ArgumentOutOfRangeException(nameof(descriptor));
        int slot = FindNode(descriptor.StableNodeKey);
        if (slot < 0)
        {
            if (_slotCount >= _nodes.Length)
                return -1;
            slot = _slotCount++;
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
        if (prerequisiteNode == dependentNode || _edgeCount >= _edges.Length)
            return false;
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

    private int FindNode(ulong stableNodeKey)
    {
        for (int i = 0; i < _slotCount; i++)
            if (_nodes[i].StableNodeKey == stableNodeKey)
                return i;
        return -1;
    }

    private void ValidateActiveNode(int nodeIndex)
    {
        if ((uint)nodeIndex >= (uint)_slotCount || !_active[nodeIndex])
            throw new ArgumentOutOfRangeException(nameof(nodeIndex));
    }

    private readonly record struct Edge(int Prerequisite, int Dependent);
}
