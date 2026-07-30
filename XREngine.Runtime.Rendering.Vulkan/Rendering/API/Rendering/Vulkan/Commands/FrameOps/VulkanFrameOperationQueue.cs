using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns queued frame operations and the reusable capture workspace for each
/// recording thread. The renderer facade supplies policy and operation
/// construction, while this owner contains all mutable queue/capture state.
/// </summary>
internal sealed class VulkanFrameOperationQueue : IDisposable
{
    private readonly ThreadLocal<ThreadWorkspace> _threadWorkspace =
        new(static () => new ThreadWorkspace(), trackAllValues: false);

    public Lock SyncRoot { get; } = new();
    public List<FrameOp> Pending { get; } = [];
    public FrameOp[] DrainedFrameOpsBuffer { get; set; } = [];
    public FrameOp[] DrainedTextureUploadFrameOpsBuffer { get; set; } = [];
    internal VulkanFrameOpDiagnosticsState Diagnostics { get; } = new();

    /// <summary>
    /// Gets the reusable workspace scoped to the calling recording thread.
    /// The first access on a thread allocates the workspace; warmed steady-state
    /// access and capture reuse are allocation-free.
    /// </summary>
    public ThreadWorkspace CurrentThread
        => _threadWorkspace.Value
            ?? throw new InvalidOperationException(
                "The Vulkan frame-operation queue has been disposed.");

    public void ReleaseCurrentThread()
        => CurrentThread.Reset();

    public void Dispose()
        => _threadWorkspace.Dispose();

    internal sealed class ThreadWorkspace
    {
        public FrameOpCapture? Capture;
        public FrameOpCapture? CaptureScratch;
        public FrameOpCapture? OrderedComputeBatchCapture;
        public FrameOpCapture? OrderedComputeBatchCaptureScratch;
        public Dictionary<int, FrameOp[]> CaptureBuffersByCount { get; } = [];
        public int RenderQueryBracketDepth;

        public void Reset()
        {
            Capture = null;
            CaptureScratch = null;
            OrderedComputeBatchCapture = null;
            OrderedComputeBatchCaptureScratch = null;
            CaptureBuffersByCount.Clear();
            RenderQueryBracketDepth = 0;
        }
    }
}