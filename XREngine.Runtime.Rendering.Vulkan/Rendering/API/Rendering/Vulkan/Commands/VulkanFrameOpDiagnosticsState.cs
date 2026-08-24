namespace XREngine.Rendering.Vulkan.Commands;

/// <summary>
/// Owns retained frame-operation diagnostics and render-thread split workspaces.
/// </summary>
internal sealed class VulkanFrameOpDiagnosticsState
{
    private readonly object _traceLock = new();
    private VulkanFrameOpTraceEntry[] _traceEntries = [];
    private ulong _traceFrameId;
    private int _traceTotalCount;
    private readonly Dictionary<int, VulkanFrameOpTraceEntry[]> _pipelineTraceEntries = [];
    private readonly Dictionary<int, ulong> _pipelineTraceFrameIds = [];
    private readonly Dictionary<int, int> _pipelineTraceTotalCounts = [];
    private FrameOp[] _staticSplitBuffer = [];
    private FrameOp[] _dynamicUiSplitBuffer = [];

    internal void StoreTrace(VulkanFrameOpTraceEntry[] entries, ulong frameId, int totalCount)
    {
        lock (_traceLock)
        {
            _traceEntries = entries;
            _traceFrameId = frameId;
            _traceTotalCount = totalCount;

            if (entries.Length == 0)
                return;

            int pipelineIdentity = entries[0].PipelineIdentity;
            if (_pipelineTraceTotalCounts.TryGetValue(pipelineIdentity, out int retainedCount) &&
                retainedCount > totalCount)
            {
                return;
            }

            _pipelineTraceEntries[pipelineIdentity] = entries;
            _pipelineTraceFrameIds[pipelineIdentity] = frameId;
            _pipelineTraceTotalCounts[pipelineIdentity] = totalCount;
        }
    }

    internal void CaptureTraceSnapshot(
        int? pipelineIdentity,
        out VulkanFrameOpTraceEntry[] entries,
        out ulong frameId,
        out int totalCount)
    {
        lock (_traceLock)
        {
            if (pipelineIdentity.HasValue &&
                _pipelineTraceEntries.TryGetValue(pipelineIdentity.Value, out VulkanFrameOpTraceEntry[]? pipelineEntries))
            {
                entries = pipelineEntries;
                frameId = _pipelineTraceFrameIds[pipelineIdentity.Value];
                totalCount = _pipelineTraceTotalCounts[pipelineIdentity.Value];
                return;
            }

            entries = _traceEntries;
            frameId = _traceFrameId;
            totalCount = _traceTotalCount;
        }
    }

    internal void EnsureSplitBuffers(
        int staticCount,
        int dynamicUiCount,
        out FrameOp[] staticOps,
        out FrameOp[] dynamicUiOps)
    {
        if (_staticSplitBuffer.Length != staticCount)
            _staticSplitBuffer = new FrameOp[staticCount];
        if (_dynamicUiSplitBuffer.Length != dynamicUiCount)
            _dynamicUiSplitBuffer = new FrameOp[dynamicUiCount];

        staticOps = _staticSplitBuffer;
        dynamicUiOps = _dynamicUiSplitBuffer;
    }
}
