using System.Collections.Concurrent;

namespace XREngine.Rendering;

/// <summary>
/// Budgeted scheduler state for texture uploads that are executed by render-thread coroutines.
/// Thread-safety: queue/slot members are free-threaded; upload execution callbacks remain owned
/// by the caller because the concrete backend primitives are renderer-specific.
/// </summary>
internal sealed class TextureUploadScheduler
{
    // Progressive uploads run as render-thread coroutines, so these budgets trade
    // streaming latency against per-frame stall risk. The original 1 upload / 4 MB
    // budget serialized streaming so aggressively that, with ~100+ textures and
    // plenty of free VRAM, queue waits grew into tens of seconds. Allow a couple of
    // concurrent uploads and a larger byte budget while still bounding each frame's
    // synchronous upload work.
    private const int MaxConcurrentProgressiveOpenGlUploads = 2;
    private const long ProgressiveOpenGlUploadBytesPerFrame = 16L * 1024L * 1024L;
    private const long CaptureDeferralBacklogBytesPerFrame = 24L * 1024L * 1024L;

    private readonly ConcurrentDictionary<XRTexture2D, TextureUploadWorkItem> _progressiveUploads = [];
    private int _activeProgressiveUploadCount;
    private long _progressiveUploadBytesScheduledThisFrame;
    private long _progressiveUploadBytesFrameTicks = -1;
    private long _progressiveUploadTelemetryFrameTicks = -1;

    public static TextureUploadScheduler Instance { get; } = new();

    public int ActiveUploadCount => Volatile.Read(ref _activeProgressiveUploadCount);
    public int QueuedUploadCount => _progressiveUploads.Count;
    public long BytesScheduledThisFrame => GetBytesScheduledThisFrame();
    public bool HasLargeBacklog
        => ActiveUploadCount >= MaxConcurrentProgressiveOpenGlUploads
            && BytesScheduledThisFrame >= CaptureDeferralBacklogBytesPerFrame;

    public bool TryRegister(XRTexture2D texture, TextureUploadWorkItem workItem)
        => _progressiveUploads.TryAdd(texture, workItem);

    public void ForceRemove(XRTexture2D texture)
        => _progressiveUploads.TryRemove(texture, out _);

    public bool TryRemove(XRTexture2D texture, TextureUploadWorkItem workItem)
        => ((ICollection<KeyValuePair<XRTexture2D, TextureUploadWorkItem>>)_progressiveUploads).Remove(
            new KeyValuePair<XRTexture2D, TextureUploadWorkItem>(texture, workItem));

    public bool HasHigherPriorityUpload(XRTexture2D currentTexture, TextureUploadWorkItem current)
    {
        foreach (KeyValuePair<XRTexture2D, TextureUploadWorkItem> pair in _progressiveUploads)
        {
            if (ReferenceEquals(pair.Key, currentTexture))
                continue;

            if (!pair.Key.RuntimeManagedProgressiveUploadActive)
                continue;

            if (IsHigherPriority(pair.Value, current))
                return true;
        }

        return false;
    }

    public bool TryAcquireUploadSlot()
    {
        int active = Interlocked.Increment(ref _activeProgressiveUploadCount);
        if (active <= MaxConcurrentProgressiveOpenGlUploads)
            return true;

        Interlocked.Decrement(ref _activeProgressiveUploadCount);
        return false;
    }

    public void ReleaseUploadSlot()
    {
        int active = Interlocked.Decrement(ref _activeProgressiveUploadCount);
        if (active < 0)
            Interlocked.Exchange(ref _activeProgressiveUploadCount, 0);
    }

    public bool WouldExceedFrameByteBudget(long nextBytes)
    {
        if (nextBytes <= 0)
            return false;

        long scheduledBytes = GetBytesScheduledThisFrame();
        return scheduledBytes > 0 && scheduledBytes + nextBytes > ProgressiveOpenGlUploadBytesPerFrame;
    }

    public void RegisterBytesForCurrentFrame(long bytes)
    {
        if (bytes <= 0)
            return;

        long currentFrame = RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks;
        long previousFrame = Interlocked.Exchange(ref _progressiveUploadBytesFrameTicks, currentFrame);
        if (previousFrame != currentFrame)
            Interlocked.Exchange(ref _progressiveUploadBytesScheduledThisFrame, 0L);

        _ = Interlocked.Add(ref _progressiveUploadBytesScheduledThisFrame, bytes);
        _ = Interlocked.Exchange(ref _progressiveUploadTelemetryFrameTicks, currentFrame);
    }

    public void RecordQueueWait(TextureUploadWorkItem workItem, XRTexture2D texture)
    {
        double queueWaitMilliseconds = TextureRuntimeDiagnostics.ElapsedMilliseconds(workItem.QueueTimestamp);
        texture.RecordTextureQueueWait(queueWaitMilliseconds);
        TextureRuntimeDiagnostics.RecordQueueWait(queueWaitMilliseconds);
        RenderWorkBudgetCoordinator.RecordTextureQueue(QueuedUploadCount, queueWaitMilliseconds);
    }

    private long GetBytesScheduledThisFrame()
    {
        long currentFrame = RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks;
        if (Volatile.Read(ref _progressiveUploadBytesFrameTicks) != currentFrame)
            return 0L;

        return Interlocked.Read(ref _progressiveUploadBytesScheduledThisFrame);
    }

    private static bool IsHigherPriority(TextureUploadWorkItem candidate, TextureUploadWorkItem current)
    {
        int candidateRank = GetPriorityRank(candidate.PriorityClass);
        int currentRank = GetPriorityRank(current.PriorityClass);
        if (candidateRank != currentRank)
            return candidateRank > currentRank;

        return candidate.QueueTimestamp < current.QueueTimestamp;
    }

    private static int GetPriorityRank(TextureUploadPriorityClass priorityClass)
        => priorityClass switch
        {
            TextureUploadPriorityClass.VisibleNow => 3,
            TextureUploadPriorityClass.NearVisible => 2,
            TextureUploadPriorityClass.Background => 1,
            _ => 0,
        };
}
