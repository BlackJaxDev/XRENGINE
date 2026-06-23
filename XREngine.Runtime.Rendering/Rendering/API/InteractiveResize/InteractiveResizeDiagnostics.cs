using System.Threading;

namespace XREngine.Rendering;

public sealed class InteractiveResizeDiagnostics
{
    private long _callbackCount;
    private long _interactiveRenderCount;
    private long _suppressedRenderCount;
    private long _resizeQueueCount;
    private long _latestNativeSnapshotSequence;
    private long _latestConsumedNativeSnapshotSequence;
    private long _droppedNativeSnapshotCount;
    private int _latestSnapshotPublicationThreadId;
    private int _nativeClientWidth;
    private int _nativeClientHeight;
    private int _presentationWidth;
    private int _presentationHeight;
    private int _pipelineOutputWidth;
    private int _pipelineOutputHeight;
    private int _fullInternalWidth;
    private int _fullInternalHeight;
    private int _pendingFullInternalWidth;
    private int _pendingFullInternalHeight;
    private long _fullInternalGeneration;
    private long _pendingFullInternalGeneration;
    private int _outputScaleMode;
    private int _outputSourceWidth;
    private int _outputSourceHeight;
    private int _outputDestinationWidth;
    private int _outputDestinationHeight;
    private float _outputScaleX;
    private float _outputScaleY;
    private string _lastResizeReason = string.Empty;

    public long CallbackCount => Interlocked.Read(ref _callbackCount);
    public long InteractiveRenderCount => Interlocked.Read(ref _interactiveRenderCount);
    public long SuppressedRenderCount => Interlocked.Read(ref _suppressedRenderCount);
    public long ResizeQueueCount => Interlocked.Read(ref _resizeQueueCount);
    public ulong LatestNativeSnapshotSequence => (ulong)Math.Max(0L, Interlocked.Read(ref _latestNativeSnapshotSequence));
    public ulong LatestConsumedNativeSnapshotSequence => (ulong)Math.Max(0L, Interlocked.Read(ref _latestConsumedNativeSnapshotSequence));
    public ulong DroppedNativeSnapshotCount => (ulong)Math.Max(0L, Interlocked.Read(ref _droppedNativeSnapshotCount));
    public int LatestSnapshotPublicationThreadId => Volatile.Read(ref _latestSnapshotPublicationThreadId);
    public int NativeClientWidth => Volatile.Read(ref _nativeClientWidth);
    public int NativeClientHeight => Volatile.Read(ref _nativeClientHeight);
    public int PresentationWidth => Volatile.Read(ref _presentationWidth);
    public int PresentationHeight => Volatile.Read(ref _presentationHeight);
    public int PipelineOutputWidth => Volatile.Read(ref _pipelineOutputWidth);
    public int PipelineOutputHeight => Volatile.Read(ref _pipelineOutputHeight);
    public int FullInternalWidth => Volatile.Read(ref _fullInternalWidth);
    public int FullInternalHeight => Volatile.Read(ref _fullInternalHeight);
    public int PendingFullInternalWidth => Volatile.Read(ref _pendingFullInternalWidth);
    public int PendingFullInternalHeight => Volatile.Read(ref _pendingFullInternalHeight);
    public ulong FullInternalGeneration => (ulong)Math.Max(0L, Interlocked.Read(ref _fullInternalGeneration));
    public ulong PendingFullInternalGeneration => (ulong)Math.Max(0L, Interlocked.Read(ref _pendingFullInternalGeneration));
    public WindowOutputScaleMode OutputScaleMode => (WindowOutputScaleMode)Volatile.Read(ref _outputScaleMode);
    public int OutputSourceWidth => Volatile.Read(ref _outputSourceWidth);
    public int OutputSourceHeight => Volatile.Read(ref _outputSourceHeight);
    public int OutputDestinationWidth => Volatile.Read(ref _outputDestinationWidth);
    public int OutputDestinationHeight => Volatile.Read(ref _outputDestinationHeight);
    public float OutputScaleX => Volatile.Read(ref _outputScaleX);
    public float OutputScaleY => Volatile.Read(ref _outputScaleY);
    public string LastResizeReason => Volatile.Read(ref _lastResizeReason);

    public void RecordCallback(string reason)
    {
        Interlocked.Increment(ref _callbackCount);
        Volatile.Write(ref _lastResizeReason, reason);
    }

    public void RecordInteractiveRender(string reason)
    {
        Interlocked.Increment(ref _interactiveRenderCount);
        Volatile.Write(ref _lastResizeReason, reason);
    }

    public void RecordSuppressedRender(string reason)
    {
        Interlocked.Increment(ref _suppressedRenderCount);
        Volatile.Write(ref _lastResizeReason, reason);
    }

    public void RecordResizeQueued(string reason)
    {
        Interlocked.Increment(ref _resizeQueueCount);
        Volatile.Write(ref _lastResizeReason, reason);
    }

    public void RecordSurfaceSnapshot(
        WindowSurfaceSnapshot snapshot,
        WindowResizeExtents extents,
        ulong droppedNativeSnapshotCount)
    {
        Interlocked.Exchange(ref _latestNativeSnapshotSequence, unchecked((long)snapshot.Sequence));
        Interlocked.Exchange(ref _droppedNativeSnapshotCount, unchecked((long)droppedNativeSnapshotCount));
        Volatile.Write(ref _latestSnapshotPublicationThreadId, Environment.CurrentManagedThreadId);
        Volatile.Write(ref _nativeClientWidth, snapshot.ClientWidth);
        Volatile.Write(ref _nativeClientHeight, snapshot.ClientHeight);
        RecordResizeExtents(extents);
    }

    public void RecordConsumedSurfaceSnapshot(WindowSurfaceSnapshot snapshot, WindowResizeExtents extents)
    {
        Interlocked.Exchange(ref _latestConsumedNativeSnapshotSequence, unchecked((long)snapshot.Sequence));
        RecordResizeExtents(extents);
    }

    public void RecordResizeExtents(WindowResizeExtents extents)
    {
        Volatile.Write(ref _nativeClientWidth, extents.NativeClientExtent.X);
        Volatile.Write(ref _nativeClientHeight, extents.NativeClientExtent.Y);
        Volatile.Write(ref _presentationWidth, extents.PresentationExtent.X);
        Volatile.Write(ref _presentationHeight, extents.PresentationExtent.Y);
        Volatile.Write(ref _pipelineOutputWidth, extents.PipelineOutputExtent.X);
        Volatile.Write(ref _pipelineOutputHeight, extents.PipelineOutputExtent.Y);
        Volatile.Write(ref _fullInternalWidth, extents.FullInternalExtent.X);
        Volatile.Write(ref _fullInternalHeight, extents.FullInternalExtent.Y);
        Volatile.Write(ref _pendingFullInternalWidth, extents.PendingFullInternalExtent.X);
        Volatile.Write(ref _pendingFullInternalHeight, extents.PendingFullInternalExtent.Y);
        Interlocked.Exchange(ref _fullInternalGeneration, unchecked((long)extents.FullInternalGeneration));
        Interlocked.Exchange(ref _pendingFullInternalGeneration, unchecked((long)extents.PendingFullInternalGeneration));
    }

    public void RecordOutputScale(WindowOutputScaleSnapshot outputScale)
    {
        Volatile.Write(ref _outputScaleMode, (int)outputScale.Mode);
        Volatile.Write(ref _outputSourceWidth, outputScale.SourceExtent.X);
        Volatile.Write(ref _outputSourceHeight, outputScale.SourceExtent.Y);
        Volatile.Write(ref _outputDestinationWidth, outputScale.DestinationExtent.X);
        Volatile.Write(ref _outputDestinationHeight, outputScale.DestinationExtent.Y);
        Volatile.Write(ref _outputScaleX, outputScale.ScaleX);
        Volatile.Write(ref _outputScaleY, outputScale.ScaleY);
    }
}
