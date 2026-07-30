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
    private FrameOp[] _staticSplitBuffer = [];
    private FrameOp[] _dynamicUiSplitBuffer = [];

    internal void StoreTrace(VulkanFrameOpTraceEntry[] entries, ulong frameId, int totalCount)
    {
        lock (_traceLock)
        {
            _traceEntries = entries;
            _traceFrameId = frameId;
            _traceTotalCount = totalCount;
        }
    }

    internal void CaptureTraceSnapshot(
        out VulkanFrameOpTraceEntry[] entries,
        out ulong frameId,
        out int totalCount)
    {
        lock (_traceLock)
        {
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