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

    private readonly Dictionary<XRTexture2D, TextureUploadWorkItem> _progressiveUploads = [];
    private readonly object _slotLock = new();
    private readonly (XRTexture2D Texture, TextureUploadWorkItem WorkItem)?[] _slotOwners =
        new (XRTexture2D Texture, TextureUploadWorkItem WorkItem)?[MaxConcurrentProgressiveOpenGlUploads];
    private int _activeProgressiveUploadCount;
    private long _progressiveUploadBytesScheduledThisFrame;
    private long _progressiveUploadBytesFrameTicks = -1;
    private long _progressiveUploadTelemetryFrameTicks = -1;

    public static TextureUploadScheduler Instance { get; } = new();

    public int ActiveUploadCount => Volatile.Read(ref _activeProgressiveUploadCount);
    public int QueuedUploadCount
    {
        get
        {
            lock (_slotLock)
                return _progressiveUploads.Count;
        }
    }
    /// <summary>
    /// Returns whether a texture still has a registered progressive upload. The registration
    /// spans both locally scheduled OpenGL mip uploads and runtime-managed uploads, and is
    /// therefore the authoritative readiness check for consumers that freeze native state.
    /// </summary>
    public bool HasPendingUpload(XRTexture2D texture)
    {
        lock (_slotLock)
            return _progressiveUploads.ContainsKey(texture);
    }

    public long BytesScheduledThisFrame => GetBytesScheduledThisFrame();
    public bool HasLargeBacklog
        => ActiveUploadCount >= MaxConcurrentProgressiveOpenGlUploads
            && BytesScheduledThisFrame >= CaptureDeferralBacklogBytesPerFrame;

    public bool TryRegister(XRTexture2D texture, TextureUploadWorkItem workItem)
    {
        lock (_slotLock)
            return _progressiveUploads.TryAdd(texture, workItem);
    }

    public void ForceRemove(XRTexture2D texture)
    {
        lock (_slotLock)
            _progressiveUploads.Remove(texture);
    }

    public bool TryRemove(XRTexture2D texture, TextureUploadWorkItem workItem)
    {
        lock (_slotLock)
        {
            if (!_progressiveUploads.TryGetValue(texture, out TextureUploadWorkItem registered)
                || !registered.Equals(workItem))
                return false;

            return _progressiveUploads.Remove(texture);
        }
    }

    public bool HasHigherPriorityUpload(XRTexture2D currentTexture, TextureUploadWorkItem current)
    {
        lock (_slotLock)
            return HasHigherPriorityWaitingUpload(currentTexture, current);
    }

    private bool HasHigherPriorityWaitingUpload(XRTexture2D currentTexture, TextureUploadWorkItem current)
    {
        foreach (KeyValuePair<XRTexture2D, TextureUploadWorkItem> pair in _progressiveUploads)
        {
            if (ReferenceEquals(pair.Key, currentTexture))
                continue;

            if (OwnsSlot(pair.Key, pair.Value))
                continue;

            if (IsHigherPriority(pair.Value, current))
                return true;
        }

        return false;
    }

    public bool TryAcquireUploadSlot(XRTexture2D texture, TextureUploadWorkItem workItem)
    {
        lock (_slotLock)
        {
            if (!_progressiveUploads.TryGetValue(texture, out TextureUploadWorkItem registered)
                || !SameWorkItem(registered, workItem)
                || HasHigherPriorityWaitingUpload(texture, workItem))
                return false;

            for (int i = 0; i < _slotOwners.Length; i++)
            {
                (XRTexture2D Texture, TextureUploadWorkItem WorkItem)? owner = _slotOwners[i];
                if (owner is not null && ReferenceEquals(owner.Value.Texture, texture))
                    return false;
            }

            for (int i = 0; i < _slotOwners.Length; i++)
            {
                if (_slotOwners[i] is not null)
                    continue;

                _slotOwners[i] = (texture, workItem);
                Volatile.Write(ref _activeProgressiveUploadCount, _activeProgressiveUploadCount + 1);
                return true;
            }

            return false;
        }
    }

    public void ReleaseUploadSlot(XRTexture2D texture, TextureUploadWorkItem workItem)
    {
        lock (_slotLock)
        {
            for (int i = 0; i < _slotOwners.Length; i++)
            {
                (XRTexture2D Texture, TextureUploadWorkItem WorkItem)? owner = _slotOwners[i];
                if (owner is null
                    || !ReferenceEquals(owner.Value.Texture, texture)
                    || !SameWorkItem(owner.Value.WorkItem, workItem))
                    continue;

                _slotOwners[i] = null;
                Volatile.Write(ref _activeProgressiveUploadCount, _activeProgressiveUploadCount - 1);
                return;
            }
        }
    }

    private bool OwnsSlot(XRTexture2D texture, TextureUploadWorkItem workItem)
    {
        for (int i = 0; i < _slotOwners.Length; i++)
        {
            (XRTexture2D Texture, TextureUploadWorkItem WorkItem)? owner = _slotOwners[i];
            if (owner is not null
                && ReferenceEquals(owner.Value.Texture, texture)
                && SameWorkItem(owner.Value.WorkItem, workItem))
                return true;
        }

        return false;
    }

    // Each registration creates its own weak-reference object, which remains stable
    // when the work item is copied into a coroutine or scheduler entry.
    private static bool SameWorkItem(TextureUploadWorkItem left, TextureUploadWorkItem right)
        => ReferenceEquals(left.Texture, right.Texture);

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
