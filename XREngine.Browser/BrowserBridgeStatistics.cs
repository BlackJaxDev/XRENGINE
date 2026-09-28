namespace XREngine.Browser;

/// <summary>Cold snapshot of managed bridge storage and current-thread frame allocation counters.</summary>
public sealed record BrowserBridgeStatistics(
    long FrameAttempts,
    long SubmittedFrames,
    long LastFrameAllocatedBytes,
    long FrameAllocatedBytes,
    int LastFramePacketBytes,
    int DrawCapacity,
    int DrawArenaGrowth,
    int UploadCommandCapacity,
    int UploadPayloadCapacity,
    int UploadCommandArenaGrowth,
    int UploadPayloadArenaGrowth);
