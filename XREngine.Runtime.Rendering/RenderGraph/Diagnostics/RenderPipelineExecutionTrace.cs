namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>
/// Records the order in which the command chain of one pipeline instance entered render graph passes in the newest
/// completed frame. On OpenGL this order is the GPU execution order. On Vulkan it is the recording order, and the
/// backend plan can reorder it.
/// </summary>
/// <remarks>
/// The render thread writes into reused storage. A frame allocates a new published array only when its order is
/// different from the previous published order. Tools read the published array from another thread.
/// </remarks>
public sealed class RenderPipelineExecutionTrace
{
    private readonly List<int> _current = [];
    private readonly HashSet<int> _currentSet = [];
    private int[] _published = [];
    private ulong _publishedFrameId;
    private int _version;

    /// <summary>Gets the pass indices of the newest completed frame, in the order that the commands entered them.</summary>
    public IReadOnlyList<int> LastExecutedPassOrder => Volatile.Read(ref _published);

    /// <summary>Gets the render frame of <see cref="LastExecutedPassOrder"/>.</summary>
    public ulong LastFrameId => Volatile.Read(ref _publishedFrameId);

    /// <summary>Gets a number that changes when the published order changes.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Gets whether a frame published an order since the trace was created or cleared.</summary>
    public bool HasRecords => Volatile.Read(ref _published).Length > 0;

    /// <summary>Starts a new frame. Call it on the render thread before the command chain executes.</summary>
    internal void BeginFrame()
    {
        _current.Clear();
        _currentSet.Clear();
    }

    /// <summary>Records that the command chain entered <paramref name="passIndex"/>. A second entry into the same pass is ignored.</summary>
    internal void Record(int passIndex)
    {
        if (_currentSet.Add(passIndex))
            _current.Add(passIndex);
    }

    /// <summary>Publishes the order of the frame. Call it on the render thread after the command chain completed.</summary>
    internal void Publish(ulong frameId)
    {
        Volatile.Write(ref _publishedFrameId, frameId);
        if (_current.Count == 0 || SequenceEquals(_published, _current))
            return;

        Volatile.Write(ref _published, [.. _current]);
        Interlocked.Increment(ref _version);
    }

    /// <summary>Removes the published order, for example after the pipeline changed.</summary>
    internal void Clear()
    {
        _current.Clear();
        _currentSet.Clear();
        Volatile.Write(ref _published, []);
        Interlocked.Increment(ref _version);
    }

    private static bool SequenceEquals(int[] published, List<int> current)
    {
        if (published.Length != current.Count)
            return false;

        for (int i = 0; i < published.Length; i++)
            if (published[i] != current[i])
                return false;

        return true;
    }
}
