using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>
/// Records which child container each branch command of one pipeline instance executed, and in which render frame.
/// Branch commands write to the trace on the render thread. Tools read it to dim branches that did not run.
/// </summary>
/// <remarks>
/// The trace keeps one entry for each branch command. After the first frame, a record operation only overwrites an
/// existing entry, so it does not allocate. A short lock protects the map because tools can read it from another thread.
/// </remarks>
public sealed class RenderPipelineBranchTrace
{
    private readonly Dictionary<ViewportRenderCommand, RenderPipelineBranchRecord> _records = new(ReferenceEqualityComparer.Instance);
    private readonly Lock _lock = new();
    private ulong _commandGeneration;
    private ulong _latestFrameId;
    private int _version;

    /// <summary>
    /// Gets the newest render frame in which a branch command recorded. A record from an older frame
    /// means that the command did not execute in the newest frame of this instance.
    /// </summary>
    public ulong LatestFrameId
    {
        get
        {
            lock (_lock)
                return _latestFrameId;
        }
    }

    /// <summary>Gets whether any branch command recorded since the trace was created or cleared.</summary>
    public bool HasRecords
    {
        get
        {
            lock (_lock)
                return _records.Count > 0;
        }
    }

    /// <summary>
    /// Gets a number that changes when the newest frame changes or the trace clears.
    /// Tools compare it to skip work when nothing changed.
    /// </summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>
    /// Gets the command generation of the pipeline when the trace last recorded a branch.
    /// The trace clears itself when a branch command of a newer generation records.
    /// </summary>
    public ulong CommandGeneration
    {
        get
        {
            lock (_lock)
                return _commandGeneration;
        }
    }

    /// <summary>
    /// Records that <paramref name="command"/> executed <paramref name="container"/> in the current render frame.
    /// Pass null when the command executed no child container.
    /// </summary>
    public void Record(ViewportRenderCommand command, ViewportRenderCommandContainer? container)
    {
        ulong frame = RuntimeEngine.Rendering.State.RenderFrameId;
        ulong generation = command.ParentPipeline?.CommandGeneration ?? 0UL;
        lock (_lock)
        {
            if (generation != _commandGeneration)
            {
                _records.Clear();
                _commandGeneration = generation;
                _version++;
            }

            if (frame != _latestFrameId)
            {
                _latestFrameId = frame;
                _version++;
            }

            _records[command] = new RenderPipelineBranchRecord(container, frame);
        }
    }

    /// <summary>
    /// Gets the last record of <paramref name="command"/>. Returns false when the command never executed for this instance.
    /// </summary>
    public bool TryGet(ViewportRenderCommand command, out RenderPipelineBranchRecord record)
    {
        lock (_lock)
            return _records.TryGetValue(command, out record);
    }

    /// <summary>
    /// Removes all records.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _records.Clear();
            _latestFrameId = 0;
            _version++;
        }
    }
}
