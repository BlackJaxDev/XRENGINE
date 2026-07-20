namespace XREngine.Rendering;

/// <summary>
/// Point-in-time diagnostics for a renderer's screenshot readback queue.
/// </summary>
public sealed class ScreenshotReadbackStatus
{
    public string Backend { get; init; } = string.Empty;
    public bool Supported { get; init; }
    public bool NonBlockingGpuWait { get; init; }
    public int QueueCapacity { get; init; }
    public int SubmittedCount { get; init; }
    public int CpuProcessingCount { get; init; }
    public int AbandonedCount { get; init; }
    public long ReservedRawBytes { get; init; }
    public double? OldestSubmittedSeconds { get; init; }
    public long LifetimeQueuedCount { get; init; }
    public long LifetimeCompletedCount { get; init; }
    public long LifetimeFailedCount { get; init; }
    public long LifetimeRejectedCount { get; init; }
    public long LifetimeTimeoutCount { get; init; }
}
